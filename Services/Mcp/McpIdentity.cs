using System.Security.Claims;
using DocuSearch.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocuSearch.Services.Mcp;

/// <summary>
/// Resuelve una única identidad desde el entorno del proceso. El cliente MCP no puede
/// elegir usuario, rol ni departamento mediante argumentos de una herramienta.
/// </summary>
public sealed class McpIdentity(
    IDbContextFactory<AppDbContext> factory,
    LocalSession session,
    ILogger<McpIdentity> logger,
    IHttpContextAccessor? httpContext = null)
{
    private bool signedIn;

    public async Task EnsureSignedInAsync(CancellationToken cancellationToken = default)
    {
        if (signedIn) return;

        // HTTP: la identidad procede del token bearer validado por el middleware.
        // stdio: procede de DOCUSEARCH_MCP_USER; nunca de los argumentos de una tool.
        var httpUser = httpContext?.HttpContext?.User;
        var email = httpUser?.Identity?.IsAuthenticated == true
            ? httpUser.FindFirstValue(ClaimTypes.Email) ?? httpUser.FindFirstValue(ClaimTypes.Name)
            : Environment.GetEnvironmentVariable("DOCUSEARCH_MCP_USER")?.Trim();
        if (string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException(
                "No hay identidad MCP: configura DOCUSEARCH_MCP_USER (stdio) o usa un token HTTP válido.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Email == email && candidate.Status == "Activo", cancellationToken)
            ?? throw new UnauthorizedAccessException("El usuario MCP no existe o no está activo.");

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim("mcp", "true")
        };
        session.SignIn(new ClaimsPrincipal(new ClaimsIdentity(claims, "mcp")));
        signedIn = true;
        logger.LogInformation("MCP: identidad fijada a {Email} ({Role}).", user.Email, user.Role);
    }
}
