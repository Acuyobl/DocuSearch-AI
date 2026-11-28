using DocuSearch.Services.Mcp;
using DocuSearch.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Memory;
using System.Net;

namespace DocuSearch.Hosting;

/// <summary>Canal de middleware. El orden importa: no reordenar sin revisar seguridad (LocalOnly, MCP, autenticación).</summary>
public static class RequestPipeline
{
    /// <summary>Guarda de producción, cabeceras, correlación, LocalOnly, MCP bearer, autenticación y antiforgery.</summary>
    public static void UseDocuSearchPipeline(this WebApplication app)
    {
        var httpsEnabled = app.Configuration.GetValue("Security:Https:Enabled", false);
        var production = ProductionGuard.Validate(app.Configuration);
        foreach (var warning in production.Warnings)
            app.Logger.LogWarning("Producción: {Warning}", warning);
        if (production.Fatal.Count > 0)
        {
            foreach (var issue in production.Fatal)
                app.Logger.LogCritical("Producción: configuración insegura: {Issue}", issue);
            throw new InvalidOperationException("Configuración de producción insegura. Corrige los mensajes de arranque antes de exponer la aplicación.");
        }

        var localOnly = app.Configuration.GetValue("Demo:LocalOnly", true);
        if (!localOnly && app.Configuration.GetValue("Security:ForwardedHeaders:Enabled", false))
        {
            var forwarded = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
            };
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            foreach (var proxy in app.Configuration.GetSection("Security:ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
                if (IPAddress.TryParse(proxy, out var address)) forwarded.KnownProxies.Add(address);
            app.UseForwardedHeaders(forwarded);
        }

        if (httpsEnabled)
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
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["X-Permitted-Cross-Domain-Policies"] = "none";
            var path = context.Request.Path.Value ?? "";
            if (path.Equals("/login", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/account/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/enroll", StringComparison.OrdinalIgnoreCase))
            {
                headers["Cache-Control"] = "no-store";
                headers["Pragma"] = "no-cache";
            }
            var csp = app.Configuration["Security:ContentSecurityPolicy"];
            if (!string.IsNullOrWhiteSpace(csp)) headers["Content-Security-Policy"] = csp;
            await next(context);
        });

        // Correlación de peticiones y log estructurado sin contenido de documentos ni query string.
        app.Use(async (context, next) =>
        {
            var requestId = context.Request.Headers["X-Request-Id"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 100) requestId = context.TraceIdentifier;
            context.Response.Headers["X-Request-Id"] = requestId;
            using (app.Logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = requestId }))
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await next(context);
                }
                finally
                {
                    app.Logger.LogInformation("HTTP {Method} {Path} -> {StatusCode} en {ElapsedMs:N0} ms",
                        context.Request.Method, context.Request.Path.Value ?? "/", context.Response.StatusCode, started.ElapsedMilliseconds);
                }
            }
        });

        // La demo solo admite peticiones locales directas. Rechazar cabeceras de proxy evita
        // que un proxy inverso en la propia máquina convierta RemoteIpAddress en 127.0.0.1.
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
        // Endpoint MCP HTTP: el token bearer se valida en tiempo constante y fija la identidad
        // antes de que la tool intente leer cualquier expediente.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/mcp"))
            {
                var options = context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<McpHttpOptions>>().Value;
                var tokens = context.RequestServices.GetRequiredService<McpTokenService>();
                var principal = await McpHttpAuth.AuthenticateAsync(context, options, tokens, context.RequestAborted);
                if (principal is null)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    return;
                }
                context.User = principal;
            }
            await next(context);
        });

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseRouting();
        app.UseRateLimiter();
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
                || path.Equals("/health", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/health/", StringComparison.OrdinalIgnoreCase);
            if (isStatic || isPublic)
            {
                await next(context);
                return;
            }
            if (ApiResponses.IsApi(context.Request))
            {
                await ApiResponses.WriteAsync(context.Response, StatusCodes.Status401Unauthorized);
                return;
            }
            context.Response.Redirect("/login?returnUrl=" + Uri.EscapeDataString(path + context.Request.QueryString));
        });

        app.UseAuthorization();
        app.UseAntiforgery();
    }
}
