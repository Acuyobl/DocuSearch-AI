using System.Data.Common;
using System.Net;
using System.Security.Claims;
using DocuSearch.Components;
using DocuSearch.Data;
using DocuSearch.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using QRCoder;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents(options =>
    options.DetailedErrors = builder.Environment.IsDevelopment());
builder.Services.AddCascadingAuthenticationState();
// El estado de autenticación del circuito interactivo procede de la cookie del handshake.
builder.Services.AddScoped<AuthenticationStateProvider, ServerAuthenticationStateProvider>();
builder.Services.AddMemoryCache();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/account/logout";
        options.AccessDeniedPath = "/login?denied=1";
        options.Cookie.Name = "DocuSearch.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(builder.Configuration.GetValue("Auth:SessionHours", 8));
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.AddScoped<AuthService>();
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Documents")
        ?? throw new InvalidOperationException("Falta ConnectionStrings:Documents."),
        sql => sql.UseCompatibilityLevel(150)));
builder.Services.AddScoped<LocalSession>();
builder.Services.AddSingleton<PdfProcessor>();
builder.Services.AddScoped<DocumentService>();
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"));
builder.Services.AddHttpClient<OllamaClient>().ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseProxy = false,
    ConnectTimeout = TimeSpan.FromSeconds(5)
});
builder.Services.AddScoped<LocalAgent>();
builder.Services.AddScoped<AgentObservability>();
builder.Services.AddScoped<LegacyImport>();
builder.Services.AddScoped<WorkspaceState>();

var app = builder.Build();

if (args.Contains("--initialize-database", StringComparer.Ordinal))
{
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await DatabaseSetup.InitializeAsync(db);
        app.Logger.LogInformation("Base SQL Server inicializada. Los datos existentes se han conservado.");
    }
    catch (Exception ex) when (ex is DbException or InvalidOperationException)
    {
        app.Logger.LogError(ex, "No se pudo inicializar SQL Server. Revisa la instancia, los permisos de Windows y ConnectionStrings:Documents.");
        Environment.ExitCode = 1;
    }
    return;
}

if (app.Configuration.GetValue("Security:Https:Enabled", false))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Cabeceras de seguridad para todo lo que sirve la aplicación.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    headers["Cross-Origin-Opener-Policy"] = "same-origin";
    var csp = app.Configuration["Security:ContentSecurityPolicy"];
    if (!string.IsNullOrWhiteSpace(csp)) headers["Content-Security-Policy"] = csp;
    await next(context);
});

// La demo solo admite peticiones locales directas. Rechazar cabeceras de proxy evita
// que un proxy inverso en la propia máquina convierta RemoteIpAddress en 127.0.0.1.
var localOnly = app.Configuration.GetValue("Demo:LocalOnly", true);
if (localOnly)
{
    app.Use(async (context, next) =>
    {
        var forwarded = context.Request.Headers.ContainsKey("X-Forwarded-For")
            || context.Request.Headers.ContainsKey("X-Forwarded-Host")
            || context.Request.Headers.ContainsKey("X-Forwarded-Proto")
            || context.Request.Headers.ContainsKey("Forwarded");
        var remote = context.Connection.RemoteIpAddress;
        var host = context.Request.Host.Host;
        if (forwarded || remote is null || !IPAddress.IsLoopback(remote) ||
            host is not ("localhost" or "127.0.0.1" or "::1" or "[::1]"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Esta demostración solo admite conexiones locales. Configura Demo:LocalOnly=false y TLS antes de publicarla.");
            return;
        }
        await next(context);
    });
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();

// Defensa en profundidad: ninguna página se sirve sin autenticar. Los recursos
// estáticos y el propio inicio de sesión sí pasan; el resto redirige a /login.
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        await next(context);
        return;
    }
    var path = context.Request.Path.Value ?? "/";
    var isStatic = Path.HasExtension(path)
        || path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/_content", StringComparison.OrdinalIgnoreCase);
    var isPublic = path.Equals("/login", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/account/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/enroll", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/health", StringComparison.OrdinalIgnoreCase);
    if (isStatic || isPublic)
    {
        await next(context);
        return;
    }
    context.Response.Redirect("/login?returnUrl=" + Uri.EscapeDataString(path + context.Request.QueryString));
});

app.UseAuthorization();
app.UseAntiforgery();

// --- Inicio y cierre de sesión (cookie de autenticación) --------------------
app.MapGet("/login", (HttpContext context, IAntiforgery antiforgery, IOptions<AuthOptions> auth) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    var returnUrl = context.Request.Query["returnUrl"].ToString();
    var failed = context.Request.Query.ContainsKey("error");
    var denied = context.Request.Query.ContainsKey("denied");
    var html = AuthPages.Login(tokens.RequestToken ?? "", returnUrl, failed, denied, auth.Value.EnrollmentEnabled);
    return Results.Content(html, "text/html; charset=utf-8");
});

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
    if (user is null) return Results.Redirect("/login?error=1");

    var identity = new ClaimsIdentity(AuthService.BuildClaims(user), CookieAuthenticationDefaults.AuthenticationScheme);
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
});

app.MapGet("/account/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

// --- Alta de dispositivos TOTP ---------------------------------------------
// Muestra el secreto y el QR de cada usuario autorizado. Solo debe estar activo
// mientras se inscriben los teléfonos: Auth:EnrollmentEnabled=false lo desactiva.
app.MapGet("/enroll", (IOptions<AuthOptions> auth) =>
{
    var options = auth.Value;
    if (!options.EnrollmentEnabled) return Results.NotFound();

    var users = new List<(AuthUserConfig User, string OtpAuthUri, string QrSvg)>();
    foreach (var user in options.Users.Where(u => !string.IsNullOrWhiteSpace(u.TotpSecret)))
    {
        var uri = Totp.OtpAuthUri(options.Issuer, user.Email, user.TotpSecret);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
        var svg = new SvgQRCode(data).GetGraphic(6);
        users.Add((user, uri, svg));
    }
    return Results.Content(AuthPages.Enroll(options.Issuer, users), "text/html; charset=utf-8");
});

app.MapGet("/health", async (IDbContextFactory<AppDbContext> factory, ILogger<Program> logger, IOptions<OllamaOptions> assistant) =>
{
    try
    {
        await using var db = await factory.CreateDbContextAsync();
        _ = await db.Users.CountAsync();
        return Results.Ok(new { status = "ready", database = "SQL Server", assistant = assistant.Value.Enabled ? assistant.Value.UseLangChain ? "langchain-ollama-local" : "ollama-local" : "local-extractive" });
    }
    catch (Exception ex) when (ex is DbException or InvalidOperationException)
    {
        logger.LogWarning(ex, "SQL Server no está disponible o no se ha inicializado.");
        return Results.Json(new { status = "unavailable", database = "SQL Server", instruction = "Configura la conexión y ejecuta dotnet run -- --initialize-database." }, statusCode: 503);
    }
});
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
