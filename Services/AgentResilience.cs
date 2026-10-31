using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services;

public sealed class ResilienceOptions
{
    /// <summary>Intentos totales por operación (1 = sin reintentos).</summary>
    public int MaxAttempts { get; set; } = 3;
    /// <summary>Espera base del backoff exponencial con jitter.</summary>
    public int BaseDelayMs { get; set; } = 400;
    public int MaxDelayMs { get; set; } = 5_000;
    /// <summary>Fallos consecutivos antes de abrir el circuito.</summary>
    public int FailureThreshold { get; set; } = 5;
    /// <summary>Tiempo que el circuito permanece abierto antes de probar de nuevo.</summary>
    public int BreakSeconds { get; set; } = 60;
    /// <summary>Tiempo máximo por intento; 0 desactiva el timeout por operación.</summary>
    public int AttemptTimeoutSeconds { get; set; } = 75;
}

/// <summary>Fallo recuperable de una API: se puede reintentar con backoff.</summary>
public sealed class TransientAgentException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>El motor no respondió tras agotar reintentos o con el circuito abierto.</summary>
public class AgentUnavailableException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

public sealed class CircuitOpenException(string operation, TimeSpan remaining)
    : AgentUnavailableException($"Circuito abierto para «{operation}»; se evitan más llamadas durante {Math.Ceiling(remaining.TotalSeconds)} s.")
{
    public string Operation { get; } = operation;
}

/// <summary>
/// Política de resiliencia para las llamadas al modelo: reintentos con backoff
/// exponencial + jitter, timeout por intento y circuit breaker por operación.
/// </summary>
public sealed class AgentResilience(IOptions<ResilienceOptions> options, ILogger<AgentResilience> logger)
{
    private static readonly AgentResilience DefaultInstance = new(
        Options.Create(new ResilienceOptions()),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentResilience>.Instance);

    private readonly ResilienceOptions settings = options.Value;
    private readonly ConcurrentDictionary<string, Breaker> breakers = new(StringComparer.OrdinalIgnoreCase);

    public static AgentResilience Default => DefaultInstance;

    public async Task<T> ExecuteAsync<T>(string operation, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        var breaker = breakers.GetOrAdd(operation, _ => new Breaker());
        var now = DateTimeOffset.UtcNow;
        if (breaker.OpenUntil > now)
            throw new CircuitOpenException(operation, breaker.OpenUntil - now);

        var maxAttempts = Math.Max(1, settings.MaxAttempts);
        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (settings.AttemptTimeoutSeconds > 0)
                attemptCts.CancelAfter(TimeSpan.FromSeconds(settings.AttemptTimeoutSeconds));
            try
            {
                var result = await action(attemptCts.Token);
                breaker.RecordSuccess();
                return result;
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken))
            {
                last = ex;
                logger.LogWarning("Fallo transitorio en {Operation} (intento {Attempt}/{Max}): {Message}",
                    operation, attempt, maxAttempts, ex.Message);
                if (attempt == maxAttempts) break;
                var delay = Backoff(attempt);
                logger.LogInformation("Reintentando {Operation} en {Delay} ms.", operation, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        breaker.RecordFailure(settings.FailureThreshold, TimeSpan.FromSeconds(settings.BreakSeconds));
        throw new AgentUnavailableException(
            $"El motor «{operation}» no respondió tras {maxAttempts} intento(s). Se usará la respuesta extractiva y se requiere revisión humana.", last);
    }

    public string Describe(string operation)
    {
        var breaker = breakers.GetOrAdd(operation, _ => new Breaker());
        return breaker.OpenUntil > DateTimeOffset.UtcNow
            ? $"open:{Math.Ceiling((breaker.OpenUntil - DateTimeOffset.UtcNow).TotalSeconds)}s"
            : breaker.Failures > 0 ? $"closed:{breaker.Failures} fallo(s)" : "closed";
    }

    public void Reset(string? operation = null)
    {
        if (operation is null) breakers.Clear();
        else breakers.TryRemove(operation, out _);
    }

    private TimeSpan Backoff(int attempt)
    {
        var exponential = settings.BaseDelayMs * Math.Pow(2, attempt - 1);
        var capped = Math.Min(exponential, settings.MaxDelayMs);
        var jitter = Random.Shared.NextDouble() * (capped * 0.25);
        return TimeSpan.FromMilliseconds(capped + jitter);
    }

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        return exception switch
        {
            TransientAgentException => true,
            HttpRequestException => true,
            TaskCanceledException => true,
            OperationCanceledException => true,
            _ => false
        };
    }

    private sealed class Breaker
    {
        private readonly object gate = new();
        private int failures;

        public int Failures { get { lock (gate) return failures; } }
        public DateTimeOffset OpenUntil { get { lock (gate) return openUntil; } }
        private DateTimeOffset openUntil;

        public void RecordSuccess()
        {
            lock (gate)
            {
                failures = 0;
                openUntil = DateTimeOffset.MinValue;
            }
        }

        public void RecordFailure(int threshold, TimeSpan breakFor)
        {
            lock (gate)
            {
                failures++;
                if (failures >= Math.Max(1, threshold))
                {
                    openUntil = DateTimeOffset.UtcNow.Add(breakFor);
                    failures = 0;
                }
            }
        }
    }
}
