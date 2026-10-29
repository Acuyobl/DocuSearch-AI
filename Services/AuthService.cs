using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services;

/// <summary>Credenciales de demostración declaradas en configuración (nunca contraseñas en claro).</summary>
public sealed class DemoCredential
{
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

public sealed class AuthOptions
{
    public const string HashPrefix = "PBKDF2-SHA256";
    public const int DefaultIterations = 210_000;

    public List<DemoCredential> Users { get; set; } = [];
    public int SessionHours { get; set; } = 8;

    /// <summary>Comprueba <c>PBKDF2-SHA256$iteraciones$sal$hash</c> en tiempo constante.</summary>
    public static bool VerifyPassword(string password, string stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || !parts[0].Equals(HashPrefix, StringComparison.Ordinal)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iterations) || iterations < 10_000) return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        if (salt.Length < 8 || expected.Length < 16) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Genera un hash almacenable; úsalo para rotar las credenciales de demostración.</summary>
    public static string CreatePasswordHash(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"{HashPrefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
}

public sealed class AuthService(IDbContextFactory<AppDbContext> factory, IOptions<AuthOptions> options, ILogger<AuthService> logger)
{
    // Hash señuelo para no revelar por tiempo de respuesta si el correo existe.
    private static readonly string DummyHash = AuthOptions.CreatePasswordHash("credential-dummy-not-valid");

    public async Task<AppUser?> ValidateAsync(string email, string password)
    {
        var demo = options.Value.Users.FirstOrDefault(u => string.Equals(u.Email, email?.Trim(), StringComparison.OrdinalIgnoreCase));
        var valid = AuthOptions.VerifyPassword(password ?? "", demo?.PasswordHash ?? DummyHash);
        if (demo is null || !valid)
        {
            logger.LogWarning("Inicio de sesión rechazado para {Email}.", email);
            return null;
        }
        await using var db = await factory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == demo.Email && u.Status == "Activo");
        if (user is null)
        {
            logger.LogWarning("Credenciales válidas sin usuario activo en la base: {Email}.", demo.Email);
            return null;
        }
        logger.LogInformation("Inicio de sesión correcto de {Email} ({Role}).", user.Email, user.Role);
        return user;
    }

    public static Claim[] BuildClaims(AppUser user) =>
    [
        new(ClaimTypes.NameIdentifier, user.Id),
        new(ClaimTypes.Name, user.Name),
        new(ClaimTypes.Email, user.Email),
        new(ClaimTypes.Role, user.Role),
        new("department", user.Department)
    ];
}

/// <summary>HTML estático de la página de inicio de sesión y del aviso de sesión cerrada.</summary>
public static class AuthPages
{
    public static string Login(string requestToken, string? returnUrl, bool failed, bool denied, int demoUserCount)
    {
        var error = failed
            ? """<p class="error" role="alert">Credenciales no válidas o usuario inactivo. Inténtalo de nuevo.</p>"""
            : denied
                ? """<p class="error" role="alert">Tu perfil no tiene permiso para esa sección.</p>"""
                : "";
        var target = string.IsNullOrWhiteSpace(returnUrl) ? "/" : WebUtility.HtmlEncode(returnUrl);
        var demoHint = demoUserCount > 0
            ? $"""<p class="hint">Usuarios de demostración: <code>fernando.aranda@empresa.com</code>, <code>carlos.mendoza@empresa.com</code>, <code>elena.rostova@empresa.com</code>, <code>ana.martinez@empresa.com</code> y <code>mariana.silva@empresa.com</code>. Contraseña de demostración en el README. Sustituye estas credenciales por ASP.NET Core Identity u OIDC antes de publicar.</p>"""
            : """<p class="hint">No hay usuarios de demostración configurados. Define <code>Auth:Users</code> en <code>appsettings.json</code>.</p>""";
        return $$"""
            <!DOCTYPE html>
            <html lang="es">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>Iniciar sesión — DocuSearch AI</title>
                <style>
                    :root { color-scheme: dark; --bg: #020617; --panel: #0f172a; --line: #1e293b; --ink: #e2e8f0; --muted: #94a3b8; --accent: #6366f1; }
                    * { box-sizing: border-box; }
                    body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: radial-gradient(1200px 600px at 20% -10%, #1e1b4b 0%, var(--bg) 55%); color: var(--ink); font-family: "Segoe UI", system-ui, sans-serif; padding: 24px; }
                    main { width: min(420px, 100%); background: var(--panel); border: 1px solid var(--line); border-radius: 16px; padding: 28px; box-shadow: 0 20px 60px #00000066; }
                    h1 { margin: 0 0 4px; font-size: 22px; }
                    .brand { color: var(--accent); font-weight: 700; letter-spacing: .04em; text-transform: uppercase; font-size: 12px; }
                    p.lead { margin: 0 0 20px; color: var(--muted); font-size: 14px; }
                    label { display: block; margin: 14px 0 6px; font-size: 13px; color: var(--muted); }
                    input { width: 100%; padding: 11px 12px; border-radius: 10px; border: 1px solid var(--line); background: #0b1220; color: var(--ink); font-size: 14px; }
                    input:focus { outline: 2px solid var(--accent); outline-offset: 1px; }
                    button { width: 100%; margin-top: 20px; padding: 12px; border: 0; border-radius: 10px; background: linear-gradient(115deg, #4f46e5, #7260ef); color: #fff; font-size: 15px; font-weight: 600; cursor: pointer; }
                    button:hover { filter: brightness(1.08); }
                    .error { background: #450a0a; border: 1px solid #be123c66; color: #fecdd3; padding: 10px 12px; border-radius: 10px; font-size: 13px; }
                    .hint { color: var(--muted); font-size: 12px; line-height: 1.5; margin-top: 18px; }
                    code { color: #c7d2fe; }
                </style>
            </head>
            <body>
                <main>
                    <span class="brand">DocuSearch AI</span>
                    <h1>Iniciar sesión</h1>
                    <p class="lead">Control documental local. Acceso restringido a usuarios autenticados.</p>
                    {{error}}
                    <form method="post" action="/account/login">
                        <input type="hidden" name="__RequestVerificationToken" value="{{WebUtility.HtmlEncode(requestToken)}}" />
                        <input type="hidden" name="returnUrl" value="{{target}}" />
                        <label for="email">Correo</label>
                        <input id="email" name="email" type="email" autocomplete="username" required autofocus />
                        <label for="password">Contraseña</label>
                        <input id="password" name="password" type="password" autocomplete="current-password" required />
                        <button type="submit">Entrar</button>
                    </form>
                    {{demoHint}}
                </main>
            </body>
            </html>
            """;
    }
}
