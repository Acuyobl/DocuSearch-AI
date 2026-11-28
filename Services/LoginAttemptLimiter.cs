using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services;

/// <summary>
/// Bloqueo temporal por cuenta tras varios intentos fallidos de TOTP. Usa la caché
/// compartida de la aplicación; no sustituye un bloqueo distribuido en varias instancias.
/// </summary>
public sealed class LoginAttemptLimiter(IMemoryCache cache, IOptions<AuthOptions> options)
{
    private sealed class State
    {
        public int Failures;
        public DateTimeOffset LockedUntil;
    }

    private static string Key(string email) => $"login-lock:{email.Trim().ToLowerInvariant()}";

    public bool IsLocked(string? email, out DateTimeOffset until)
    {
        until = default;
        if (string.IsNullOrWhiteSpace(email)) return false;
        if (cache.TryGetValue(Key(email), out State? state) && state is not null && state.LockedUntil > DateTimeOffset.UtcNow)
        {
            until = state.LockedUntil;
            return true;
        }
        return false;
    }

    public void RegisterFailure(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        var duration = TimeSpan.FromMinutes(Math.Max(1, options.Value.LockoutMinutes) + 1);
        var state = cache.GetOrCreate(Key(email), entry =>
        {
            entry.Size = 1;
            entry.AbsoluteExpirationRelativeToNow = duration;
            return new State();
        })!;
        lock (state)
        {
            state.Failures++;
            if (state.Failures >= Math.Max(1, options.Value.MaxFailedAttempts))
                state.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, options.Value.LockoutMinutes));
        }
        cache.Set(Key(email), state, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = duration,
            Size = 1
        });
    }

    public void Reset(string? email)
    {
        if (!string.IsNullOrWhiteSpace(email)) cache.Remove(Key(email));
    }
}
