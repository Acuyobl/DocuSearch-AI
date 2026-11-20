using System.Security.Claims;
using DocuSearch.Data;
using DocuSearch.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocuSearch.Mcp;

/// <summary>
/// Resuelve una única identidad desde el entorno del proceso. El cliente MCP no puede
/// elegir usuario, rol ni departamento mediante argumentos de una herramienta.
/// </summary>
public sealed class McpIdentity(
    IDbContextFactory<AppDbContext> factory,
    LocalSession session,
    ILogger<McpIdentity> logger)
{
    private bool signedIn;

    public async Task EnsureSignedInAsync(CancellationToken cancellationToken = default)
    {
        if (signedIn) return;
        var email = Environment.GetEnvironmentVariable("DOCUSEARCH_MCP_USER")?.Trim();
        if (string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException("Configura DOCUSEARCH_MCP_USER con el correo del usuario autorizado.");

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
