using System.Data.Common;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using DocuSearch.Components;
using DocuSearch.Data;
using DocuSearch.Models;
using DocuSearch.Services;
using DocuSearch.Services.Rag;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using QRCoder;

var builder = WebApplication.CreateBuilder(args);
// Interruptores de demo: en producción deben quedar en false (ver appsettings.Production.example.json).
DemoPolicy.AllowReset = builder.Configuration.GetValue("Demo:AllowReset", true);
DemoPolicy.SeedOnInitialize = builder.Configuration.GetValue("Demo:SeedOnInitialize", true);
var httpsEnabled = builder.Configuration.GetValue("Security:Https:Enabled", false);
if (builder.Configuration.GetValue("Logging:Json", false))
{
    builder.Logging.AddJsonConsole(options =>
    {
        options.IncludeScopes = true;
        options.UseUtcTimestamp = true;
        options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    });
}
// Falla al arrancar si algún servicio (p. ej. el asistente con OpenCode) no se puede construir.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = builder.Environment.IsDevelopment();
});

builder.Services.AddRazorComponents().AddInteractiveServerComponents(options =>
    options.DetailedErrors = builder.Environment.IsDevelopment());
builder.Services.AddCascadingAuthenticationState();
// El estado de autenticación del circuito interactivo procede de la cookie del handshake.
builder.Services.AddScoped<AuthenticationStateProvider, ServerAuthenticationStateProvider>();
builder.Services.AddMemoryCache();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json; charset=utf-8";
        await context.HttpContext.Response.WriteAsync("{\"error\":\"rate_limited\"}", cancellationToken);
    };
    var permitLimit = builder.Configuration.GetValue("Security:LoginRateLimitPerMinute", 10);
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/account/logout";
        options.AccessDeniedPath = "/login?denied=1";
        options.Cookie.Name = "DocuSearch.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = httpsEnabled ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(builder.Configuration.GetValue("Auth:SessionHours", 8));
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.AddScoped<AuthService>();
builder.Services.AddSingleton<LoginAttemptLimiter>();
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Documents")
        ?? throw new InvalidOperationException("Falta ConnectionStrings:Documents."),
        sql => sql.UseCompatibilityLevel(150)));
builder.Services.AddScoped<LocalSession>();
builder.Services.AddSingleton<PdfProcessor>();
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<DocumentQualityService>();
builder.Services.AddScoped<CatalogSuggester>();
builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection("Embeddings"));
var embeddingBase = builder.Configuration["Embeddings:BaseUrl"] ?? "http://127.0.0.1:11434";
if (!Uri.TryCreate(embeddingBase, UriKind.Absolute, out var embeddingUri) || embeddingUri.Scheme is not ("http" or "https"))
    throw new InvalidOperationException("Embeddings:BaseUrl debe ser una URL HTTP como http://127.0.0.1:11434.");
builder.Services.AddHttpClient<EmbeddingClient>(client =>
{
    client.BaseAddress = new Uri(embeddingUri.AbsoluteUri.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(Math.Max(5, builder.Configuration.GetValue("Embeddings:TimeoutSeconds", 60)));
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseProxy = false,
    ConnectTimeout = TimeSpan.FromSeconds(5)
});
builder.Services.AddScoped<EmbeddingIndexer>();
builder.Services.Configure<PineconeOptions>(builder.Configuration.GetSection("Pinecone"));
builder.Services.AddHttpClient<PineconeClient>(client =>
{
    client.BaseAddress = new Uri("https://api.pinecone.io/");
    client.Timeout = TimeSpan.FromSeconds(Math.Max(5, builder.Configuration.GetValue("Pinecone:TimeoutSeconds", 30)));
});
builder.Services.AddScoped<PineconeSyncService>();
builder.Services.AddScoped<PineconeRetriever>();
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection("Rag"));
builder.Services.AddScoped<FixedRagChunker>();
builder.Services.AddScoped<SemanticRagChunker>();
builder.Services.AddScoped<IRagChunker>(provider => provider.GetRequiredService<FixedRagChunker>());
builder.Services.AddScoped<IRagEmbedder, BgeM3RagEmbedder>();
builder.Services.AddScoped<SqlRagVectorStore>();
builder.Services.AddScoped<PineconeRagVectorStore>();
builder.Services.AddScoped<IRagVectorStore>(provider =>
{
    var rag = provider.GetRequiredService<IOptions<RagOptions>>().Value;
    return string.Equals(rag.VectorStore, "pinecone", StringComparison.OrdinalIgnoreCase)
        ? provider.GetRequiredService<PineconeRagVectorStore>()
        : provider.GetRequiredService<SqlRagVectorStore>();
});
builder.Services.AddScoped<IRagReranker, SemanticRagReranker>();
builder.Services.AddScoped<IRagRetriever, HybridRagRetriever>();
builder.Services.AddScoped<RagIngestionService>();
builder.Services.AddScoped<RagQueryService>();
builder.Services.AddScoped<RagEvaluationService>();
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"));
builder.Services.AddHttpClient<OllamaClient>().ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseProxy = false,
    ConnectTimeout = TimeSpan.FromSeconds(5)
});
builder.Services.Configure<OpenCodeOptions>(builder.Configuration.GetSection("OpenCode"));
builder.Services.Configure<ResilienceOptions>(builder.Configuration.GetSection("Resilience"));
builder.Services.Configure<AssistantOptions>(builder.Configuration.GetSection("Assistant"));
builder.Services.AddSingleton<AgentResilience>();
builder.Services.AddSingleton<AssistantRuntime>();
builder.Services.AddHttpClient<OpenCodeClient>().ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
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

if (args.Contains("--reindex-embeddings", StringComparer.Ordinal))
{
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        var indexer = scope.ServiceProvider.GetRequiredService<EmbeddingIndexer>();
        var updated = await indexer.ReindexAllAsync();
        Console.WriteLine($"Índice semántico actualizado: {updated} páginas.");
        var pinecone = scope.ServiceProvider.GetService<PineconeSyncService>();
        if (pinecone is { Enabled: true })
        {
            var synced = await pinecone.SyncAllAsync();
            Console.WriteLine($"Pinecone: {synced} vectores sincronizados.");
        }
    }
    catch (Exception ex) when (ex is DbException or InvalidOperationException or HttpRequestException)
    {
        app.Logger.LogError(ex, "No se pudo reindexar la búsqueda semántica. Revisa Embeddings:BaseUrl y el modelo configurado.");
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Contains("--sync-pinecone", StringComparer.Ordinal))
{
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        var pinecone = scope.ServiceProvider.GetRequiredService<PineconeSyncService>();
        if (!pinecone.Enabled)
        {
            Console.WriteLine("Pinecone está desactivado o falta Pinecone:ApiKey.");
            Environment.ExitCode = 1;
            return;
        }
        var synced = await pinecone.SyncAllAsync();
        Console.WriteLine($"Pinecone: {synced} vectores sincronizados.");
    }
    catch (Exception ex) when (ex is DbException or InvalidOperationException or HttpRequestException)
    {
        app.Logger.LogError(ex, "No se pudo sincronizar Pinecone. Revisa Pinecone:ApiKey, IndexName y conectividad.");
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Contains("--rag-reindex", StringComparer.Ordinal))
{
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        var ingestion = scope.ServiceProvider.GetRequiredService<RagIngestionService>();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var result = await ingestion.IngestAsync(new SqlRagLoader(factory));
        Console.WriteLine($"RAG: {result.Documents} documentos, {result.Chunks} fragmentos indexados.");
    }
    catch (Exception ex) when (ex is DbException or InvalidOperationException or HttpRequestException)
    {
        app.Logger.LogError(ex, "No se pudo reindexar el pipeline RAG por fragmentos.");
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Contains("--rag-pinecone-sync", StringComparer.Ordinal))
{
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<PineconeRagVectorStore>();
        var synced = await store.SyncExistingAsync();
        Console.WriteLine($"Pinecone RAG: {synced} fragmentos sincronizados.");
    }
    catch (Exception ex) when (ex is DbException or InvalidOperationException or HttpRequestException)
    {
        app.Logger.LogError(ex, "No se pudo sincronizar los fragmentos RAG con Pinecone.");
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Contains("--rag-eval-export", StringComparer.Ordinal))
{
    try
    {
        var position = Array.IndexOf(args, "--rag-eval-export");
        var outputPath = position + 1 < args.Length ? args[position + 1] : "";
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            Console.WriteLine("Uso: --rag-eval-export ruta.json [--generate]");
            Environment.ExitCode = 1;
            return;
        }
        await using var scope = app.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking()
            .Where(candidate => candidate.Status == "Activo" && candidate.Role == "admin_general")
            .FirstOrDefaultAsync()
            ?? throw new InvalidOperationException("No hay administrador activo para exportar evaluación RAG.");
        var session = scope.ServiceProvider.GetRequiredService<LocalSession>();
        session.SignIn(new ClaimsPrincipal(new ClaimsIdentity(AuthService.BuildClaims(user), "cli")));
        var evaluation = scope.ServiceProvider.GetRequiredService<RagEvaluationService>();
        var questions = Path.Combine(AppContext.BaseDirectory, "eval", "rag_questions.json");
        if (!File.Exists(questions)) questions = Path.Combine(Directory.GetCurrentDirectory(), "eval", "rag_questions.json");
        var count = await evaluation.ExportAsync(questions, outputPath, args.Contains("--generate", StringComparer.Ordinal));
        Console.WriteLine($"RAG eval: {count} casos exportados en {outputPath}.");
    }
    catch (Exception ex) when (ex is DbException or InvalidOperationException or IOException)
    {
        app.Logger.LogError(ex, "No se pudo exportar el set de evaluación RAG.");
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Contains("--rag-eval", StringComparer.Ordinal))
{
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking()
            .Where(candidate => candidate.Status == "Activo" && candidate.Role == "admin_general")
            .FirstOrDefaultAsync()
            ?? throw new InvalidOperationException("No hay administrador activo para evaluar RAG.");
        var session = scope.ServiceProvider.GetRequiredService<LocalSession>();
        session.SignIn(new ClaimsPrincipal(new ClaimsIdentity(AuthService.BuildClaims(user), "cli")));
        var evaluation = scope.ServiceProvider.GetRequiredService<RagEvaluationService>();
        var path = Path.Combine(AppContext.BaseDirectory, "eval", "rag_questions.json");
        if (!File.Exists(path)) path = Path.Combine(Directory.GetCurrentDirectory(), "eval", "rag_questions.json");
        var report = await evaluation.RunAsync(path, args.Contains("--generate", StringComparer.Ordinal));
        Console.WriteLine($"RAG eval: {report.Cases} casos");
        Console.WriteLine($"Context Recall: {report.ContextRecall:F3}");
        Console.WriteLine($"Context Precision: {report.ContextPrecision:F3}");
        Console.WriteLine($"Faithfulness (proxy): {report.Faithfulness:F3}");
        Console.WriteLine($"Answer Relevancy (proxy): {report.AnswerRelevancy:F3}");
        Console.WriteLine($"Casos sin la fuente esperada: {report.FailedCases.Count}");
        if (report.ContextRecall < 0.8 || report.Faithfulness < 0.9)
            Console.WriteLine("Umbrales del prompt no alcanzados (0.80 / 0.90).");
    }
    catch (Exception ex) when (ex is DbException or InvalidOperationException or IOException)
    {
        app.Logger.LogError(ex, "No se pudo ejecutar la evaluación RAG.");
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Contains("--search", StringComparer.Ordinal))
{
    try
    {
        var position = Array.IndexOf(args, "--search");
        var query = position + 1 < args.Length ? args[position + 1] : "";
        if (string.IsNullOrWhiteSpace(query))
        {
            Console.WriteLine("Uso: --search \"tu consulta\"");
            Environment.ExitCode = 1;
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking()
            .Where(candidate => candidate.Status == "Activo" && (candidate.Role == "admin_general" || candidate.Role == "gestor_documental"))
            .OrderBy(candidate => candidate.Role == "admin_general" ? 0 : 1)
            .FirstOrDefaultAsync()
            ?? throw new InvalidOperationException("No hay un usuario activo para la consulta de diagnóstico.");
        var session = scope.ServiceProvider.GetRequiredService<LocalSession>();
        session.SignIn(new ClaimsPrincipal(new ClaimsIdentity(AuthService.BuildClaims(user), "cli")));

        var documents = await scope.ServiceProvider.GetRequiredService<DocumentService>().LoadAsync();
        var embeddings = scope.ServiceProvider.GetRequiredService<EmbeddingClient>();
        var queryVector = await embeddings.TryEmbedAsync(query);
        var pinecone = scope.ServiceProvider.GetRequiredService<PineconeRetriever>();
        var externalScores = queryVector is null || !pinecone.Enabled
            ? null
            : await pinecone.SearchAsync(queryVector, documents.Documents.Select(document => document.Id).ToArray());
        var hits = LocalSearch.Search(documents.Documents, query, minimum: 20, queryVector: queryVector,
            externalScores: externalScores).Take(5).ToList();
        Console.WriteLine($"Consulta: {query}");
        Console.WriteLine(queryVector is null ? "Vector: no disponible (recuperación léxica)." : "Vector: bge-m3 (búsqueda híbrida).");
        Console.WriteLine(pinecone.Enabled ? "Pinecone: consultado." : "Pinecone: desactivado.");
        Console.WriteLine($"Resultados: {hits.Count}");
        foreach (var hit in hits)
            Console.WriteLine($"{hit.Score,3} · {hit.Document.InternalCode} p.{hit.PageNumber} · {TextAnalysis.Truncate(hit.Snippet, 160)}");
    }
    catch (Exception ex) when (ex is DbException or InvalidOperationException)
    {
        app.Logger.LogError(ex, "No se pudo ejecutar la consulta de diagnóstico.");
        Environment.ExitCode = 1;
    }
    return;
}

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

app.MapGet("/account/logout", async (HttpContext context, IDbContextFactory<AppDbContext> factory) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
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

app.MapGet("/health", async (IDbContextFactory<AppDbContext> factory, ILogger<Program> logger,
    AssistantRuntime runtime, IOptions<OpenCodeOptions> openCodeOptions) =>
{
    try
    {
        await using var db = await factory.CreateDbContextAsync();
        _ = await db.Users.CountAsync();
        var active = runtime.Active;
        var engine = active.Engine.ToLowerInvariant() switch
        {
            "opencode" => (openCodeOptions.Value.UseLangChain ? "langchain+opencode:" : "opencode:") + $"{active.ProviderId}/{active.ModelId}",
            "ollama" => "ollama-local",
            _ => "local-extractive"
        };
        return Results.Ok(new { status = "ready", database = "SQL Server", assistant = engine, profile = active.Id });
    }
    catch (Exception ex) when (ex is DbException or InvalidOperationException)
    {
        logger.LogWarning(ex, "SQL Server no está disponible o no se ha inicializado.");
        return Results.Json(new { status = "unavailable", database = "SQL Server", instruction = "Configura la conexión y ejecuta dotnet run -- --initialize-database." }, statusCode: 503);
    }
});
// Sondas separadas para orquestadores: liveness no toca la base; readiness exige esquema al día.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (IDbContextFactory<AppDbContext> factory) =>
{
    try
    {
        await using var db = await factory.CreateDbContextAsync();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
        if (pending.Length > 0)
            return Results.Json(new { status = "degraded", database = "SQL Server", pendingMigrations = pending }, statusCode: 503);
        await db.Users.CountAsync();
        return Results.Ok(new { status = "ready", database = "SQL Server", pendingMigrations = 0 });
    }
    catch (Exception ex) when (ex is DbException or InvalidOperationException)
    {
        return Results.Json(new { status = "unavailable", database = "SQL Server" }, statusCode: 503);
    }
});
// Catálogo del selector de motores: lo lee el panel del asistente y sirve para
// comprobar desde fuera que la lista gratis/local se descubrió en este OpenCode.
app.MapGet("/assistant/engines", async (AssistantRuntime runtime, AgentResilience resilience) =>
{    await runtime.EnsureLoadedAsync();
    return Results.Ok(new
    {
        active = runtime.Active.Id,
        loadedAt = runtime.LoadedAt,
        error = runtime.LastError,
        circuit = resilience.Describe("opencode.generate"),
        groups = runtime.Profiles.GroupBy(profile => profile.Group).Select(group => new
        {
            group = group.Key,
            models = group.Select(profile => new
            {
                id = profile.Id, engine = profile.Engine, provider = profile.ProviderId,
                model = profile.ModelId, available = profile.Available, label = profile.Label
            })
        })
    });
});

app.MapPost("/api/rag/query", async (HttpContext context, LocalSession session, RagQueryService rag,
    RagQueryRequest request) =>
{
    if (context.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    try
    {
        session.SignIn(context.User);
        return Results.Ok(await rag.QueryAsync(request.Question));
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
