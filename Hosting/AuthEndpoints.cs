using DocuSearch.Data;
using DocuSearch.Models;
using DocuSearch.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QRCoder;
using System.Data.Common;
using System.Net;
using System.Security.Claims;

namespace DocuSearch.Hosting;

/// <summary>Inicio y cierre de sesión con TOTP y alta de dispositivos.</summary>
public static class AuthEndpoints
{
    /// <summary>/login, /account/login, /account/logout y /enroll.</summary>
    public static void MapDocuSearchAuth(this WebApplication app)
    {
        // --- Inicio y cierre de sesión (cookie de autenticación) --------------------
        app.MapGet("/login", (HttpContext context, IAntiforgery antiforgery, IOptions<AuthOptions> auth) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            var returnUrl = context.Request.Query["returnUrl"].ToString();
            var failed = context.Request.Query.ContainsKey("error");
            var denied = context.Request.Query.ContainsKey("denied");
            var html = AuthPages.Login(tokens.RequestToken ?? "", returnUrl, failed, denied, auth.Value.EnrollmentEnabled);
            return Results.Content(html, "text/html; charset=utf-8");
        }).RequireRateLimiting("login");

        app.MapPost("/account/login", async (HttpContext context, IAntiforgery antiforgery, AuthService auth, IOptions<AuthOptions> authOptions) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Redirect("/login?error=1");
            }

            var form = await context.Request.ReadFormAsync();
            var email = form["email"].ToString();
            var code = form["code"].ToString();
            var returnUrl = form["returnUrl"].ToString();
            var user = await auth.ValidateAsync(email, code);
            if (user is null)
            {
                await auth.RecordFailedLoginAsync(email);
                return Results.Redirect("/login?error=1");
            }

            var identity = new ClaimsIdentity(AuthService.BuildClaims(user), CookieAuthenticationDefaults.AuthenticationScheme);
            // La IP de inicio de sesión acompaña a los eventos de la bitácora (el circuito Blazor no la expone).
            if (context.Connection.RemoteIpAddress is { } remoteIp)
                identity.AddClaim(new Claim(AuthService.IpClaim, remoteIp.ToString()));
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddHours(authOptions.Value.SessionHours)
                });

            var target = returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal) ? returnUrl : "/";
            return Results.Redirect(target);
        }).RequireRateLimiting("login");

        app.MapGet("/account/logout", async (HttpContext context, IDbContextFactory<AppDbContext> factory, AssistantMemory memory) =>
        {
            if (context.User.Identity?.IsAuthenticated == true)
            {
                // La conversación del asistente y el estado del buscador mueren con la sesión.
                if (context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } userId)
                    memory.ClearSession(userId, context.User.FindFirstValue(AuthService.SessionClaim));
                try
                {
                    await using var db = await factory.CreateDbContextAsync();
                    db.Audit.Add(new AuditEvent
                    {
                        UserName = context.User.Identity.Name ?? "desconocido",
                        UserRole = context.User.FindFirstValue(ClaimTypes.Role) ?? "",
                        Action = "LOGOUT",
                        Details = "Cierre de sesión.",
                        DocumentCode = "SYSTEM",
                        Status = "EXITOSO"
                    });
                    await db.SaveChangesAsync();
                }
                catch (Exception ex) when (ex is DbException or DbUpdateException or InvalidOperationException)
                {
                    app.Logger.LogWarning("No se pudo auditar el cierre de sesión ({Error}).", ex.GetType().Name);
                }
            }
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });

        // --- Alta de dispositivos TOTP ---------------------------------------------
        // Muestra el secreto y el QR de cada usuario autorizado. Solo debe estar activo
        // mientras se inscriben los teléfonos: Auth:EnrollmentEnabled=false lo desactiva.
        app.MapGet("/enroll", (HttpContext context, IOptions<AuthOptions> auth) =>
        {
            var options = auth.Value;
            // Solo se permite inscribir dispositivos desde el propio servidor: la página muestra secretos.
            var remote = context.Connection.RemoteIpAddress;
            if (!options.EnrollmentEnabled || remote is null || !IPAddress.IsLoopback(remote))
                return Results.Json(new { status = "not_found" }, statusCode: StatusCodes.Status404NotFound);

            var users = new List<(AuthUserConfig User, string Secret, string OtpAuthUri, string QrSvg)>();
            foreach (var user in options.Users)
            {
                var secret = options.ResolveTotpSecret(user);
                if (string.IsNullOrWhiteSpace(secret)) continue;
                var uri = Totp.OtpAuthUri(options.Issuer, user.Email, secret);
                using var generator = new QRCodeGenerator();
                using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
                var svg = new SvgQRCode(data).GetGraphic(6);
                users.Add((user, secret, uri, svg));
            }
            return Results.Content(AuthPages.Enroll(options.Issuer, users), "text/html; charset=utf-8");
        });
    }
}
