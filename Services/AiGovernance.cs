using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services;

public sealed class AiGovernanceOptions
{
    /// <summary>Precio por defecto por 1 000 tokens de entrada (USD) si el catálogo no lo informa.</summary>
    public decimal UsdPer1kInput { get; set; }
    /// <summary>Precio por defecto por 1 000 tokens de salida (USD).</summary>
    public decimal UsdPer1kOutput { get; set; }
    /// <summary>Precios por «proveedor/modelo» (USD por 1 000 tokens); prevalecen sobre el catálogo.</summary>
    public Dictionary<string, AiPricing> Pricing { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Precio en USD por 1 000 tokens.</summary>
public sealed record AiPricing(decimal UsdPer1kInput = 0, decimal UsdPer1kOutput = 0)
{
    public static readonly AiPricing Free = new();
    public bool IsFree => UsdPer1kInput <= 0 && UsdPer1kOutput <= 0;
}

/// <summary>Presupuesto o cuota agotados: no se llama al proveedor (equivale a HTTP 429 AI_QUOTA_EXCEEDED).</summary>
public sealed class AiQuotaExceededException(string message, long spentMicroUsd, long limitMicroUsd, string kind)
    : InvalidOperationException(message)
{
    public const string Code = "AI_QUOTA_EXCEEDED";
    public long SpentMicroUsd { get; } = spentMicroUsd;
    public long LimitMicroUsd { get; } = limitMicroUsd;
    public string Kind { get; } = kind;
}

/// <summary>Datos de una llamada real para el registro de consumo.</summary>
public sealed record AiUsageRecord(string Feature, string Provider, string Model, string PromptTemplateKey, string PromptVersion,
    string RequestHash, long TokensIn, long TokensOut, bool TokensEstimated, long CostMicroUsd, long LatencyMs, string Status,
    string? ErrorCode = null, string? EntityType = null, string? EntityId = null);

/// <summary>Decisión previa a una llamada.</summary>
public sealed record AiBudgetDecision(bool Allowed, string? IncidentKind, long SpentMicroUsd, long LimitMicroUsd, string Message);

public sealed record AiFeatureUsage(string Feature, int Calls, long TokensIn, long TokensOut, long CostMicroUsd);
public sealed record AiModelUsage(string Provider, string Model, int Calls, long TokensIn, long TokensOut, long CostMicroUsd);

public sealed record AiUsageReport(
    string Period,
    int Calls,
    int Errors,
    long TokensIn,
    long TokensOut,
    bool HasEstimatedTokens,
    long SpentMicroUsd,
    AiBudgetPolicy? Policy,
    double? PercentUsed,
    long ForecastMicroUsd,
    IReadOnlyList<AiFeatureUsage> Features,
    IReadOnlyList<AiModelUsage> Models,
    IReadOnlyList<AiBudgetIncident> Incidents,
    IReadOnlyList<AiUsageLedger> Recent);

/// <summary>Contrato que usan los procesos que llaman a un motor generativo.</summary>
public interface IAiGovernance
{
    /// <summary>Precio aplicable al motor/proveedor/modelo de la llamada.</summary>
    AiPricing ResolvePricing(string engine, string provider, string model, AssistantProfile? profile);
    /// <summary>SIEMPRE antes del proveedor. Lanza <see cref="AiQuotaExceededException"/> si no se puede gastar.</summary>
    Task AssertCanSpendAsync(string feature, long estimateMicroUsd, CancellationToken cancellationToken = default);
    /// <summary>SIEMPRE después de la llamada real. Nunca lanza.</summary>
    Task RecordAsync(AiUsageRecord record, CancellationToken cancellationToken = default);
}

/// <summary>Cálculos puros del presupuesto (periodo UTC, coste, porcentaje, previsión, decisión).</summary>
public static class AiBudgetMath
{
    public const long MicroPerUsd = 1_000_000;

    /// <summary>Mes calendario UTC «YYYY-MM».</summary>
    public static string CurrentPeriod(DateTimeOffset now) => now.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static (DateTimeOffset Start, DateTimeOffset End) PeriodRange(string period)
    {
        var start = DateTimeOffset.ParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return (start, start.AddMonths(1));
    }

    /// <summary>Coste en micro-dólares enteros, truncado y nunca negativo.</summary>
    public static long ComputeCostMicroUsd(long tokensIn, long tokensOut, AiPricing pricing)
    {
        var usd = Math.Max(0, tokensIn) / 1000m * pricing.UsdPer1kInput + Math.Max(0, tokensOut) / 1000m * pricing.UsdPer1kOutput;
        return Math.Max(0, (long)decimal.Truncate(usd * MicroPerUsd));
    }

    /// <summary>Porcentaje del presupuesto consumido; null si no hay presupuesto (0 = sin límite).</summary>
    public static double? ComputePercentUsed(long spentMicroUsd, long budgetMicroUsd) =>
        budgetMicroUsd <= 0 ? null : Math.Round(100d * spentMicroUsd / budgetMicroUsd, 1);

    /// <summary>Previsión de cierre de mes: gasto diario medio × días del mes.</summary>
    public static long ComputeForecastMicroUsd(long spentMicroUsd, DateTimeOffset now, string period)
    {
        var (start, end) = PeriodRange(period);
        if (now >= end) return spentMicroUsd;
        if (now <= start) return 0;
        var elapsed = Math.Max((now - start).TotalDays, 1d / 24);
        return (long)Math.Round(spentMicroUsd / elapsed * (end - start).TotalDays);
    }

    /// <summary>
    /// Presupuesto en USD → con hardStop y gasto proyectado ≥ límite se bloquea (HARD_STOP);
    /// al cruzar alertPercent se avisa (WARNING) y continúa. Presupuesto 0 → rige la cuota de tokens.
    /// </summary>
    public static AiBudgetDecision Evaluate(AiBudgetPolicy? policy, long spentMicroUsd, long tokensUsed, long estimateMicroUsd)
    {
        if (policy is null) return new(true, null, spentMicroUsd, 0, "Sin política de presupuesto.");
        if (policy.MonthlyBudgetMicroUsd > 0)
        {
            var limit = policy.MonthlyBudgetMicroUsd;
            var projected = spentMicroUsd + Math.Max(0, estimateMicroUsd);
            if (policy.HardStop && projected >= limit)
                return new(false, AiBudgetIncident.HardStop, spentMicroUsd, limit,
                    $"Presupuesto mensual de IA agotado ({FormatMicroUsd(spentMicroUsd)} de {FormatMicroUsd(limit)}).");
            if (projected * 100 >= limit * Math.Clamp(policy.AlertPercent, 1, 100))
                return new(true, AiBudgetIncident.Warning, spentMicroUsd, limit,
                    $"Consumo de IA al {ComputePercentUsed(projected, limit)} % del presupuesto mensual.");
            return new(true, null, spentMicroUsd, limit, "Dentro del presupuesto.");
        }
        if (policy.MonthlyTokenQuota > 0 && tokensUsed >= policy.MonthlyTokenQuota)
            return new(false, AiBudgetIncident.QuotaExceeded, spentMicroUsd, 0,
                $"Cuota mensual de {policy.MonthlyTokenQuota:N0} tokens agotada.");
        return new(true, null, spentMicroUsd, 0, "Sin límite de gasto.");
    }

    /// <summary>USD con un máximo de 4 decimales.</summary>
    public static string FormatMicroUsd(long microUsd) =>
        "$" + (microUsd / (decimal)MicroPerUsd).ToString("0.00##", CultureInfo.InvariantCulture);

    public static long ToMicroUsd(decimal usd) => Math.Max(0, (long)decimal.Truncate(usd * MicroPerUsd));

    /// <summary>Estimación de tokens por longitud de texto (≈ 4 caracteres por token).</summary>
    public static long EstimateTokens(int characters) => Math.Max(0, (characters + 3) / 4);

    public static string ShortHash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    public static AiUsageReport BuildReport(string period, IReadOnlyList<AiUsageLedger> rows, AiBudgetPolicy? policy,
        IReadOnlyList<AiBudgetIncident> incidents, DateTimeOffset now, int recent = 25)
    {
        var spent = rows.Sum(row => row.CostMicroUsd);
        return new AiUsageReport(
            period, rows.Count, rows.Count(row => row.Status != "ok"),
            rows.Sum(row => row.TokensIn), rows.Sum(row => row.TokensOut), rows.Any(row => row.TokensEstimated), spent, policy,
            ComputePercentUsed(spent, policy?.MonthlyBudgetMicroUsd ?? 0),
            period == CurrentPeriod(now) ? ComputeForecastMicroUsd(spent, now, period) : spent,
            rows.GroupBy(row => row.Feature)
                .Select(group => new AiFeatureUsage(group.Key, group.Count(), group.Sum(row => row.TokensIn), group.Sum(row => row.TokensOut), group.Sum(row => row.CostMicroUsd)))
                .OrderByDescending(item => item.CostMicroUsd).ThenByDescending(item => item.Calls).ToList(),
            rows.GroupBy(row => (row.Provider, row.Model))
                .Select(group => new AiModelUsage(group.Key.Provider, group.Key.Model, group.Count(), group.Sum(row => row.TokensIn), group.Sum(row => row.TokensOut), group.Sum(row => row.CostMicroUsd)))
                .OrderByDescending(item => item.CostMicroUsd).ThenByDescending(item => item.Calls).ToList(),
            incidents.OrderByDescending(incident => incident.LastUtc).ToList(),
            rows.OrderByDescending(row => row.CreatedUtc).Take(recent).ToList());
    }
}

/// <summary>Contexto de una llamada para el guardián de consumo.</summary>
/// <param name="PromptText">Instrucciones del sistema (se usa su hash como versión y su longitud para estimar).</param>
/// <param name="RequestText">Pregunta y fuentes enviadas (solo para estimar y calcular el hash; no se guarda).</param>
public sealed record AiCallContext(string Feature, string Engine, string Provider, string Model, string PromptTemplateKey,
    string PromptText, string RequestText, AiPricing Pricing, int ExpectedOutputTokens, string? EntityType = null, string? EntityId = null);

/// <summary>
/// Envuelve cada llamada al proveedor: comprueba el presupuesto antes, mide y registra después
/// (también si falla). Una fila por llamada real; si se bloquea, no llama ni registra.
/// </summary>
public static class AiCallGuard
{
    public static async Task<AgentAnswer> RunAsync(IAiGovernance? governance, AiCallContext context,
        Func<Task<AgentAnswer>> call, CancellationToken cancellationToken = default)
    {
        if (governance is null) return await call();
        var estimatedIn = AiBudgetMath.EstimateTokens(context.PromptText.Length + context.RequestText.Length);
        var estimate = AiBudgetMath.ComputeCostMicroUsd(estimatedIn, context.ExpectedOutputTokens, context.Pricing);
        await governance.AssertCanSpendAsync(context.Feature, estimate, cancellationToken);

        var version = AiBudgetMath.ShortHash(context.PromptText)[..8];
        var requestHash = AiBudgetMath.ShortHash($"{context.Feature}|{context.Provider}/{context.Model}|{context.RequestText}");
        var timer = Stopwatch.StartNew();
        try
        {
            var answer = await call();
            var estimated = answer.InputTokens is null || answer.OutputTokens is null;
            var tokensIn = answer.InputTokens ?? estimatedIn;
            var tokensOut = answer.OutputTokens ?? AiBudgetMath.EstimateTokens(answer.Text.Length);
            await governance.RecordAsync(new AiUsageRecord(context.Feature, context.Provider, context.Model, context.PromptTemplateKey,
                version, requestHash, tokensIn, tokensOut, estimated,
                AiBudgetMath.ComputeCostMicroUsd(tokensIn, tokensOut, context.Pricing), timer.ElapsedMilliseconds, "ok",
                EntityType: context.EntityType, EntityId: context.EntityId), cancellationToken);
            return answer;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // La llamada se hizo y falló: se registra sin coste (no hay tokens fiables).
            await governance.RecordAsync(new AiUsageRecord(context.Feature, context.Provider, context.Model, context.PromptTemplateKey,
                version, requestHash, 0, 0, true, 0, timer.ElapsedMilliseconds, "error", AgentObservability.ErrorCode(ex),
                context.EntityType, context.EntityId), CancellationToken.None);
            throw;
        }
    }
}

/// <summary>
/// Gobernanza del consumo de IA: registro de llamadas (ledger), política de presupuesto e
/// incidentes. DocuSearch es de una sola organización: la «organización» es la instalación.
/// Usuario siempre desde la sesión autenticada. Si la base falla, degrada sin bloquear.
/// </summary>
public sealed class AiGovernanceService(
    IDbContextFactory<AppDbContext> factory,
    LocalSession session,
    IOptions<AiGovernanceOptions> options,
    ILogger<AiGovernanceService> logger,
    TimeProvider? time = null) : IAiGovernance
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    public AiPricing ResolvePricing(string engine, string provider, string model, AssistantProfile? profile)
    {
        if (!string.Equals(engine, "opencode", StringComparison.OrdinalIgnoreCase) || profile?.Group == "Locales")
            return AiPricing.Free;
        if (options.Value.Pricing.TryGetValue($"{provider}/{model}", out var configured)) return configured;
        if (profile is { InputCostPerMillion: > 0 } or { OutputCostPerMillion: > 0 })
            return new AiPricing(profile.InputCostPerMillion / 1000m, profile.OutputCostPerMillion / 1000m);
        return profile?.Group == "Gratis" ? AiPricing.Free : new AiPricing(options.Value.UsdPer1kInput, options.Value.UsdPer1kOutput);
    }

    public async Task AssertCanSpendAsync(string feature, long estimateMicroUsd, CancellationToken cancellationToken = default)
    {
        AiBudgetDecision decision;
        var period = AiBudgetMath.CurrentPeriod(clock.GetUtcNow());
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var policy = await db.AiBudgetPolicies.AsNoTracking().SingleOrDefaultAsync(item => item.Id == AiBudgetPolicy.DefaultId, cancellationToken);
            if (policy is null) return;
            var month = db.AiUsageLedger.Where(row => row.Period == period);
            var spent = await month.SumAsync(row => row.CostMicroUsd, cancellationToken);
            var tokens = await month.SumAsync(row => row.TokensIn + row.TokensOut, cancellationToken);
            decision = AiBudgetMath.Evaluate(policy, spent, tokens, estimateMicroUsd);
            if (decision.IncidentKind is { } kind)
                await UpsertIncidentAsync(db, period, kind, decision, feature, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AiQuotaExceededException)
        {
            // Política o incidentes no disponibles: se degrada sin bloquear el proceso.
            logger.LogWarning("ai_budget_check_degraded: {Error}", ex.GetType().Name);
            return;
        }
        if (!decision.Allowed)
            throw new AiQuotaExceededException(decision.Message, decision.SpentMicroUsd, decision.LimitMicroUsd, decision.IncidentKind ?? "");
    }

    public async Task RecordAsync(AiUsageRecord record, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var now = clock.GetUtcNow();
            db.AiUsageLedger.Add(new AiUsageLedger
            {
                CreatedUtc = now, Period = AiBudgetMath.CurrentPeriod(now),
                Feature = Bound(record.Feature, 60), Provider = Bound(record.Provider, 80), Model = Bound(record.Model, 200),
                PromptTemplateKey = Bound(record.PromptTemplateKey, 80), PromptVersion = Bound(record.PromptVersion, 16),
                RequestHash = Bound(record.RequestHash, 16), TokensIn = Math.Max(0, record.TokensIn), TokensOut = Math.Max(0, record.TokensOut),
                TokensEstimated = record.TokensEstimated, CostMicroUsd = Math.Max(0, record.CostMicroUsd), LatencyMs = Math.Max(0, record.LatencyMs),
                Status = record.Status, ErrorCode = record.ErrorCode, UserId = session.IsAuthenticated ? session.UserId : null,
                EntityType = record.EntityType, EntityId = record.EntityId
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Nunca tumba el proceso: solo deja constancia en el log (sin contenido ni claves).
            logger.LogError("ai_usage_record_failed: {Feature} {Error}", record.Feature, ex.GetType().Name);
        }
    }

    /// <summary>Incidente idempotente por (periodo, tipo): se crea una vez y después solo se actualiza.</summary>
    private async Task UpsertIncidentAsync(AppDbContext db, string period, string kind, AiBudgetDecision decision, string feature,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        async Task<int> Touch() => await db.AiBudgetIncidents.Where(item => item.Period == period && item.Kind == kind)
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(item => item.LastUtc, now)
                .SetProperty(item => item.Occurrences, item => item.Occurrences + 1)
                .SetProperty(item => item.SpentMicroUsd, decision.SpentMicroUsd)
                .SetProperty(item => item.LimitMicroUsd, decision.LimitMicroUsd)
                .SetProperty(item => item.LastFeature, feature), cancellationToken);
        try
        {
            if (await Touch() > 0) return;
            db.AiBudgetIncidents.Add(new AiBudgetIncident
            {
                Period = period, Kind = kind, FirstUtc = now, LastUtc = now, SpentMicroUsd = decision.SpentMicroUsd,
                LimitMicroUsd = decision.LimitMicroUsd, LastFeature = feature, Detail = Bound(decision.Message, 400)
            });
            db.Audit.Add(new AuditEvent
            {
                UserName = session.IsAuthenticated ? session.DisplayName : "Sistema", UserRole = session.IsAuthenticated ? session.Role : "system",
                Action = "ai.incident", Details = $"{kind} {period}: {decision.Message}",
                Status = kind == AiBudgetIncident.Warning ? "ADVERTENCIA" : "DENEGADO", Entity = nameof(AiBudgetIncident), EntityId = $"{period}:{kind}"
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Otra petición lo creó a la vez (índice único): se actualiza el existente.
            db.ChangeTracker.Clear();
            await Touch();
        }
    }

    private async Task<AppUser> UserAsync(AppDbContext db, Func<AppUser, bool> allowed, string message)
    {
        if (!session.IsAuthenticated) throw new UnauthorizedAccessException("No hay una sesión autenticada.");
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == session.UserId && item.Status == "Activo");
        if (user is null || !allowed(user)) throw new UnauthorizedAccessException(message);
        return user;
    }

    /// <summary>Permiso «ai:usage_read».</summary>
    public async Task<AiUsageReport> GetUsageAsync(string? period = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        await UserAsync(db, Permissions.CanReadAiUsage, "Solo administración general y auditoría pueden consultar el consumo de IA.");
        var now = clock.GetUtcNow();
        var selected = string.IsNullOrWhiteSpace(period) ? AiBudgetMath.CurrentPeriod(now) : period;
        _ = AiBudgetMath.PeriodRange(selected);
        var rows = await db.AiUsageLedger.AsNoTracking().Where(row => row.Period == selected).ToListAsync();
        AiBudgetPolicy? policy = null;
        List<AiBudgetIncident> incidents = [];
        try
        {
            policy = await db.AiBudgetPolicies.AsNoTracking().SingleOrDefaultAsync(item => item.Id == AiBudgetPolicy.DefaultId);
            incidents = await db.AiBudgetIncidents.AsNoTracking().Where(item => item.Period == selected).ToListAsync();
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            logger.LogWarning("ai_budget_read_degraded: {Error}", ex.GetType().Name);
        }
        return AiBudgetMath.BuildReport(selected, rows, policy, incidents, now);
    }

    public async Task<List<string>> PeriodsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        await UserAsync(db, Permissions.CanReadAiUsage, "Solo administración general y auditoría pueden consultar el consumo de IA.");
        var periods = await db.AiUsageLedger.Select(row => row.Period).Distinct().ToListAsync();
        var current = AiBudgetMath.CurrentPeriod(clock.GetUtcNow());
        if (!periods.Contains(current)) periods.Add(current);
        return periods.OrderDescending().ToList();
    }

    /// <summary>Permiso «ai:budget_manage». Queda en la bitácora con antes/después.</summary>
    public async Task UpdateBudgetAsync(long monthlyBudgetMicroUsd, int alertPercent, bool hardStop, long monthlyTokenQuota)
    {
        if (monthlyBudgetMicroUsd < 0 || monthlyTokenQuota < 0)
            throw new System.ComponentModel.DataAnnotations.ValidationException("El presupuesto y la cuota no pueden ser negativos.");
        if (alertPercent is < 1 or > 100)
            throw new System.ComponentModel.DataAnnotations.ValidationException("El umbral de aviso debe estar entre 1 y 100 %.");
        await using var db = await factory.CreateDbContextAsync();
        var user = await UserAsync(db, Permissions.CanManageAiBudget, "Solo administración general puede cambiar el presupuesto de IA.");
        var policy = await db.AiBudgetPolicies.SingleOrDefaultAsync(item => item.Id == AiBudgetPolicy.DefaultId);
        if (policy is null) db.AiBudgetPolicies.Add(policy = new AiBudgetPolicy());
        policy.MonthlyBudgetMicroUsd = monthlyBudgetMicroUsd;
        policy.AlertPercent = alertPercent;
        policy.HardStop = hardStop;
        policy.MonthlyTokenQuota = monthlyTokenQuota;
        policy.UpdatedUtc = clock.GetUtcNow();
        policy.UpdatedBy = user.Name;
        db.Audit.Add(new AuditEvent
        {
            UserName = user.Name, UserRole = user.Role, Action = "ai.budget_updated",
            Details = $"Presupuesto {(monthlyBudgetMicroUsd == 0 ? "sin límite" : AiBudgetMath.FormatMicroUsd(monthlyBudgetMicroUsd))}, aviso {alertPercent} %, " +
                $"corte {(hardStop ? "activo" : "no")}, cuota {(monthlyTokenQuota == 0 ? "sin cuota" : monthlyTokenQuota.ToString("N0") + " tokens")}."
        });
        await db.SaveChangesAsync();
    }

    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max];
}
