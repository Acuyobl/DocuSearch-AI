using System.Net;
using System.Security.Claims;
using Document = DocuSearch.Models;
using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services;

/// <summary>Usuario autorizado a iniciar sesión con su correo y un código TOTP.</summary>
public sealed class AuthUserConfig
{
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "consultor";
    public string Department { get; set; } = "";
    public string TotpSecret { get; set; } = "";
}

public sealed class AuthOptions
{
    public int SessionHours { get; set; } = 8;
    /// <summary>Pasos de 30 s de tolerancia por reloj desajustado (−ventana…+ventana).</summary>
    public int TotpWindow { get; set; } = 1;
    /// <summary>Habilita /enroll para mostrar los QR de alta. Desactívalo tras inscribir los dispositivos.</summary>
    public bool EnrollmentEnabled { get; set; } = true;
    public string Issuer { get; set; } = "DocuSearch AI";
    public List<AuthUserConfig> Users { get; set; } = [];
}

public sealed class AuthService(
    IDbContextFactory<AppDbContext> factory,
    IOptions<AuthOptions> options,
    IMemoryCache cache,
    ILogger<AuthService> logger)
{
    private static string CacheKey(string email) => $"totp-step:{email.Trim().ToLowerInvariant()}";

    /// <summary>
    /// Valida correo + código TOTP. El código es de un solo uso: se rechaza reutilizar
    /// el mismo paso de 30 s aunque siga dentro de la ventana de tolerancia.
    /// </summary>
    public async Task<AppUser?> ValidateAsync(string email, string? code)
    {
        var config = options.Value.Users.FirstOrDefault(u => string.Equals(u.Email, email?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (config is null || string.IsNullOrWhiteSpace(config.TotpSecret))
        {
            logger.LogWarning("Intento de inicio de sesión con un correo no autorizado: {Email}.", email);
            return null;
        }

        if (!Totp.TryVerify(config.TotpSecret, code, DateTimeOffset.UtcNow, options.Value.TotpWindow, out var step))
        {
            logger.LogWarning("Código TOTP no válido para {Email}.", config.Email);
            return null;
        }

        if (cache.TryGetValue(CacheKey(config.Email), out long previousStep) && step <= previousStep)
        {
            logger.LogWarning("Código TOTP reutilizado para {Email} (paso {Step}).", config.Email, step);
            return null;
        }
        cache.Set(CacheKey(config.Email), step, TimeSpan.FromSeconds(Totp.PeriodSeconds * (Math.Clamp(options.Value.TotpWindow, 0, 3) + 2)));

        var user = await EnsureUserAsync(config);
        logger.LogInformation("Inicio de sesión correcto de {Email} ({Role}).", user.Email, user.Role);
        return user;
    }

    /// <summary>
    /// Mantiene la tabla de usuarios alineada con la configuración: crea el usuario si no
    /// existe y actualiza nombre, rol y departamento si cambiaron.
    /// </summary>
    private async Task<AppUser> EnsureUserAsync(AuthUserConfig config)
    {
        await using var db = await factory.CreateDbContextAsync();
        var email = config.Email.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email);
        var department = await db.Departments.SingleOrDefaultAsync(d => d.Name == config.Department);
        if (department is null)
        {
            department = new DepartmentInfo { Name = string.IsNullOrWhiteSpace(config.Department) ? "Sin departamento" : config.Department, Lead = "Sin asignar" };
            db.Departments.Add(department);
            logger.LogInformation("Se crea el departamento {Department} para el usuario {Email}.", department.Name, email);
        }
        if (user is null)
        {
            user = new AppUser
            {
                Email = email,
                Name = string.IsNullOrWhiteSpace(config.Name) ? email.Split('@')[0] : config.Name,
                Role = ValidateRole(config.Role),
                Department = department.Name,
                Status = "Activo"
            };
            db.Users.Add(user);
            logger.LogInformation("Se crea el usuario {Email} con rol {Role}.", email, user.Role);
        }
        else
        {
            user.Name = string.IsNullOrWhiteSpace(config.Name) ? user.Name : config.Name;
            user.Role = ValidateRole(config.Role);
            user.Department = department.Name;
            user.Status = "Activo";
        }
        db.Audit.Add(new AuditEvent
        {
            UserName = user.Name,
            UserRole = user.Role,
            Action = "LOGIN",
            Details = "Inicio de sesión con verificación TOTP.",
            DocumentCode = "SYSTEM",
            Status = "EXITOSO"
        });
        await db.SaveChangesAsync();
        return user;
    }

    private static string ValidateRole(string role) =>
        Document.Catalog.Roles.Contains(role) ? role : throw new InvalidOperationException($"Rol no válido en Auth:Users: {role}.");

    public static Claim[] BuildClaims(AppUser user) =>
    [
        new(ClaimTypes.NameIdentifier, user.Id),
        new(ClaimTypes.Name, user.Name),
        new(ClaimTypes.Email, user.Email),
        new(ClaimTypes.Role, user.Role),
        new("department", user.Department)
    ];
}

/// <summary>HTML estático de inicio de sesión y del alta de dispositivos TOTP.</summary>
public static class AuthPages
{
    public static string Login(string requestToken, string? returnUrl, bool failed, bool denied, bool enrollmentEnabled)
    {
        var error = failed
            ? """<p class="error" role="alert">Correo no autorizado o código incorrecto/caducado. Inténtalo de nuevo.</p>"""
            : denied
                ? """<p class="error" role="alert">Tu perfil no tiene permiso para esa sección.</p>"""
                : "";
        var target = string.IsNullOrWhiteSpace(returnUrl) ? "/" : WebUtility.HtmlEncode(returnUrl);
        var enroll = enrollmentEnabled
            ? """<p class="hint">¿Primera vez? Abre <a href="/enroll">/enroll</a> para escanear el código QR de tu cuenta en la aplicación autenticadora.</p>"""
            : "";
        return $$"""
            <!DOCTYPE html>
            <html lang="es">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>Verificación en dos pasos — DocuSearch AI</title>
                <style>
                    :root { color-scheme: dark; --bg: #020617; --panel: #0f172a; --line: #1e293b; --ink: #e2e8f0; --muted: #94a3b8; --accent: #6366f1; }
                    * { box-sizing: border-box; }
                    body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: radial-gradient(1200px 600px at 20% -10%, #1e1b4b 0%, var(--bg) 55%); color: var(--ink); font-family: "Segoe UI", system-ui, sans-serif; padding: 24px; }
                    main { width: min(430px, 100%); background: var(--panel); border: 1px solid var(--line); border-radius: 16px; padding: 28px; box-shadow: 0 20px 60px #00000066; }
                    h1 { margin: 0 0 4px; font-size: 22px; }
                    .brand { color: var(--accent); font-weight: 700; letter-spacing: .04em; text-transform: uppercase; font-size: 12px; }
                    p.lead { margin: 0 0 20px; color: var(--muted); font-size: 14px; }
                    label { display: block; margin: 14px 0 6px; font-size: 13px; color: var(--muted); }
                    input { width: 100%; padding: 11px 12px; border-radius: 10px; border: 1px solid var(--line); background: #0b1220; color: var(--ink); font-size: 14px; }
                    input#code { letter-spacing: .35em; font-size: 22px; text-align: center; font-variant-numeric: tabular-nums; }
                    input:focus { outline: 2px solid var(--accent); outline-offset: 1px; }
                    button { width: 100%; margin-top: 20px; padding: 12px; border: 0; border-radius: 10px; background: linear-gradient(115deg, #4f46e5, #7260ef); color: #fff; font-size: 15px; font-weight: 600; cursor: pointer; }
                    button:hover { filter: brightness(1.08); }
                    .error { background: #450a0a; border: 1px solid #be123c66; color: #fecdd3; padding: 10px 12px; border-radius: 10px; font-size: 13px; }
                    .hint { color: var(--muted); font-size: 12px; line-height: 1.5; margin-top: 18px; }
                    a { color: #a5b4fc; }
                    code { color: #c7d2fe; }
                </style>
            </head>
            <body>
                <main>
                    <span class="brand">DocuSearch AI</span>
                    <h1>Verificación en dos pasos</h1>
                    <p class="lead">Introduce tu correo y el código de 6 dígitos de tu aplicación autenticadora.</p>
                    {{error}}
                    <form method="post" action="/account/login">
                        <input type="hidden" name="__RequestVerificationToken" value="{{WebUtility.HtmlEncode(requestToken)}}" />
                        <input type="hidden" name="returnUrl" value="{{target}}" />
                        <label for="email">Correo</label>
                        <input id="email" name="email" type="email" autocomplete="username" required autofocus />
                        <label for="code">Código de la app autenticadora</label>
                        <input id="code" name="code" type="text" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" autocomplete="one-time-code" required placeholder="000000" />
                        <button type="submit">Entrar</button>
                    </form>
                    {{enroll}}
                </main>
            </body>
            </html>
            """;
    }

    public static string Enroll(string issuer, IReadOnlyList<(AuthUserConfig User, string OtpAuthUri, string QrSvg)> users)
    {
        var cards = string.Join("\n", users.Select(entry => $$"""
            <section class="card">
                <h2>{{WebUtility.HtmlEncode(entry.User.Email)}}</h2>
                <p class="meta">{{WebUtility.HtmlEncode(entry.User.Name)}} · {{entry.User.Role}} · {{WebUtility.HtmlEncode(entry.User.Department)}}</p>
                <div class="qr">{{entry.QrSvg}}</div>
                <p>Escanea el código con Google Authenticator, Microsoft Authenticator, Authy o similar.</p>
                <p class="secret">Clave manual: <code>{{Totp.FormatSecret(entry.User.TotpSecret)}}</code></p>
                <p class="uri">{{WebUtility.HtmlEncode(entry.OtpAuthUri)}}</p>
            </section>
            """));
        return $$"""
            <!DOCTYPE html>
            <html lang="es">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>Alta de verificación en dos pasos — DocuSearch AI</title>
                <style>
                    :root { color-scheme: dark; --bg: #020617; --panel: #0f172a; --line: #1e293b; --ink: #e2e8f0; --muted: #94a3b8; --accent: #6366f1; }
                    * { box-sizing: border-box; }
                    body { margin: 0; min-height: 100vh; background: radial-gradient(1200px 600px at 20% -10%, #1e1b4b 0%, var(--bg) 55%); color: var(--ink); font-family: "Segoe UI", system-ui, sans-serif; padding: 32px 16px; }
                    main { width: min(1080px, 100%); margin: 0 auto; }
                    h1 { margin: 0 0 6px; font-size: 24px; }
                    .brand { color: var(--accent); font-weight: 700; letter-spacing: .04em; text-transform: uppercase; font-size: 12px; }
                    p.lead { color: var(--muted); font-size: 14px; }
                    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); gap: 18px; margin-top: 22px; }
                    .card { background: var(--panel); border: 1px solid var(--line); border-radius: 14px; padding: 20px; }
                    h2 { margin: 0 0 4px; font-size: 15px; overflow-wrap: anywhere; }
                    .meta { color: var(--muted); font-size: 12px; margin: 0 0 12px; }
                    .qr { background: #fff; padding: 10px; border-radius: 10px; display: inline-block; line-height: 0; }
                    .qr svg { width: 210px; height: 210px; display: block; }
                    p { font-size: 12px; color: var(--muted); line-height: 1.5; }
                    code { color: #c7d2fe; overflow-wrap: anywhere; }
                    .uri { font-size: 10px; color: #64748b; }
                </style>
            </head>
            <body>
                <main>
                    <span class="brand">DocuSearch AI</span>
                    <h1>Alta de verificación en dos pasos</h1>
                    <p class="lead">Cada persona escanea únicamente <strong>su propio</strong> código QR con la aplicación autenticadora. Esta página muestra secretos: desactívala con <code>Auth:EnrollmentEnabled=false</code> cuando todas las cuentas estén dadas de alta y no la publiques.</p>
                    <div class="grid">
                    {{cards}}
                    </div>
                </main>
            </body>
            </html>
            """;
    }
}
