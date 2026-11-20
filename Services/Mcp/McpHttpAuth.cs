using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace DocuSearch.Services.Mcp;

/// <summary>
/// Autenticación bearer del endpoint MCP. No usa cookies ni acepta la identidad en el cuerpo
/// de una herramienta: el token se compara en tiempo constante y solo identifica al usuario
/// configurado para este servidor.
/// </summary>
public static class McpHttpAuth
{
    public static bool TryAuthenticate(HttpContext context, McpHttpOptions options, out ClaimsPrincipal principal)
    {
        principal = new ClaimsPrincipal(new ClaimsIdentity());
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.User) || string.IsNullOrWhiteSpace(options.Token))
            return false;

        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var supplied = header[7..].Trim();
        if (!FixedTimeEquals(supplied, options.Token)) return false;

        var claims = new[]
        {
            new Claim(ClaimTypes.Email, options.User),
            new Claim(ClaimTypes.Name, options.User),
            new Claim("mcp", "http")
        };
        principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "McpBearer"));
        return true;
    }

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        var left = Encoding.UTF8.GetBytes(supplied);
        var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
