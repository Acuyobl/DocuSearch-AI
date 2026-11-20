using System.Security.Cryptography;
using System.Text;
using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuSearch.Services;

/// <summary>Token opaco de MCP: solo se persiste su SHA-256; el valor se muestra una vez.</summary>
public sealed record McpTokenView(string Id, string Name, string UserId, string Scopes,
    DateTimeOffset CreatedUtc, DateTimeOffset? ExpiresUtc, DateTimeOffset? LastUsedUtc, DateTimeOffset? RevokedUtc);

public sealed record McpTokenAuthentication(AppUser User, string TokenId, string Scopes);

public sealed class McpTokenService(IDbContextFactory<AppDbContext> factory)
{
    public const string DefaultScopes = "documents:read";

    public static string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();

    public async Task<McpTokenAuthentication?> AuthenticateAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken) || rawToken.Length is < 20 or > 200) return null;
        var hash = Hash(rawToken);
        var now = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var token = await db.McpApiTokens.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.TokenHash == hash && candidate.RevokedUtc == null &&
            (candidate.ExpiresUtc == null || candidate.ExpiresUtc > now), cancellationToken);
        if (token is null) return null;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.Id == token.UserId && candidate.Status == "Activo", cancellationToken);
        if (user is null) return null;
        await db.McpApiTokens.Where(candidate => candidate.Id == token.Id)
            .ExecuteUpdateAsync(setter => setter.SetProperty(candidate => candidate.LastUsedUtc, now), cancellationToken);
        return new McpTokenAuthentication(user, token.Id, token.Scopes);
    }

    public async Task<(string RawToken, McpTokenView Token)> CreateAsync(string userId, string name,
        int? validDays = 90, string scopes = DefaultScopes, string actorName = "Sistema",
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.Id == userId && candidate.Status == "Activo", cancellationToken)
            ?? throw new InvalidOperationException("El usuario no existe o no está activo.");
        var raw = "dsmcp_" + Base64Url(RandomNumberGenerator.GetBytes(32));
        var token = new McpApiToken
        {
            UserId = user.Id,
            Name = string.IsNullOrWhiteSpace(name) ? "MCP" : TextAnalysis.Truncate(name.Trim(), 120),
            TokenHash = Hash(raw),
            Scopes = scopes,
            CreatedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = validDays is > 0 ? DateTimeOffset.UtcNow.AddDays(Math.Min(validDays.Value, 365)) : null
        };
        db.McpApiTokens.Add(token);
        db.Audit.Add(new AuditEvent
        {
            UserName = actorName,
            UserRole = "admin_general",
            Action = "MCP_TOKEN_CREATED",
            Details = $"Token MCP «{token.Name}» creado para {user.Email}; expira {token.ExpiresUtc:O}.",
            DocumentCode = "SYSTEM",
            Status = "EXITOSO"
        });
        await db.SaveChangesAsync(cancellationToken);
        return (raw, View(token));
    }

    public async Task<List<McpTokenView>> ListAsync(string? userId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.McpApiTokens.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(userId)) query = query.Where(token => token.UserId == userId);
        return (await query.OrderByDescending(token => token.CreatedUtc).ToListAsync(cancellationToken))
            .Select(View).ToList();
    }

    public async Task<bool> RevokeAsync(string id, string? userId = null, string actorName = "Sistema",
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var token = await db.McpApiTokens.SingleOrDefaultAsync(candidate =>
            candidate.Id == id && candidate.RevokedUtc == null, cancellationToken);
        if (token is null) return false;
        if (!string.IsNullOrWhiteSpace(userId) && token.UserId != userId) return false;
        token.RevokedUtc = DateTimeOffset.UtcNow;
        db.Audit.Add(new AuditEvent
        {
            UserName = actorName,
            UserRole = "admin_general",
            Action = "MCP_TOKEN_REVOKED",
            Details = $"Token MCP «{token.Name}» revocado.",
            DocumentCode = "SYSTEM",
            Status = "EXITOSO"
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static McpTokenView View(McpApiToken token) => new(token.Id, token.Name, token.UserId, token.Scopes,
        token.CreatedUtc, token.ExpiresUtc, token.LastUsedUtc, token.RevokedUtc);
}
