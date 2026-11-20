using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace DocuSearch.Services.Mcp;

/// <summary>
/// Autenticación bearer del endpoint MCP. El token principal se busca como hash en SQL;
/// se mantiene un token de configuración solo como arranque local/legacy.
/// </summary>
public static class McpHttpAuth
{
    public static async Task<ClaimsPrincipal?> AuthenticateAsync(HttpContext context, McpHttpOptions options,
        McpTokenService tokens, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled) return null;
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var supplied = header[7..].Trim();
        if (supplied.Length == 0) return null;

        var authenticated = await tokens.AuthenticateAsync(supplied, cancellationToken);
        if (authenticated is not null)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.Email, authenticated.User.Email),
                new Claim(ClaimTypes.Name, authenticated.User.Name),
                new Claim(ClaimTypes.Role, authenticated.User.Role),
                new Claim("mcp", "http"),
                new Claim("mcp_token_id", authenticated.TokenId)
            };
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "McpBearer"));
        }

        // Compatibilidad de arranque: token de configuración, sin tabla SQL.
        if (!string.IsNullOrWhiteSpace(options.User) && !string.IsNullOrWhiteSpace(options.Token) &&
            FixedTimeEquals(supplied, options.Token))
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.Email, options.User),
                new Claim(ClaimTypes.Name, options.User),
                new Claim(ClaimTypes.Role, ""),
                new Claim("mcp", "http")
            };
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "McpBearer"));
        }
        return null;
    }

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        var left = Encoding.UTF8.GetBytes(supplied);
        var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
