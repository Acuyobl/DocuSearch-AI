using DocuSearch.Data;
using DocuSearch.Services.Mcp;
using DocuSearch.Services.Rag;
using DocuSearch.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Threading.RateLimiting;

namespace DocuSearch.Hosting;

/// <summary>Registro de servicios, autenticación, límites de peticiones y opciones de la aplicación.</summary>
public static class ServiceRegistration
{
    /// <summary>Todo lo que antes vivía antes de <c>builder.Build()</c> en Program.cs.</summary>
    public static void AddDocuSearch(this WebApplicationBuilder builder)
    {
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
        // Sin SizeLimit a propósito: la caché compartida guarda el anti-replay TOTP y el bloqueo de login,
        // y con límite una inundación de entradas impediría registrarlos. Cada entrada lleva su caducidad.
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<AssistantMemory>();
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
            var mcpLimit = builder.Configuration.GetValue("Mcp:RateLimitPerMinute", 60);
            options.AddPolicy("mcp", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.Email) ?? context.Connection.RemoteIpAddress?.ToString() ?? "mcp",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = mcpLimit,
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
                // La API responde 401/403 con JSON; solo la interfaz web redirige al inicio de sesión.
                options.Events.OnRedirectToLogin = context => ApiResponses.RedirectOrStatus(context, StatusCodes.Status401Unauthorized);
                options.Events.OnRedirectToAccessDenied = context => ApiResponses.RedirectOrStatus(context, StatusCodes.Status403Forbidden);
            });
        builder.Services.AddDocuSearchAuthorization();
        // Configuración incoherente: falla al arrancar con un mensaje claro, no en la primera consulta.
        builder.Services.AddDocuSearchOptionsValidation();
        builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
        builder.Services.AddScoped<AuthService>();
        builder.Services.AddSingleton<LoginAttemptLimiter>();
        // Fábrica por ámbito: el interceptor de gobernanza necesita la sesión del circuito actual.
        builder.Services.AddScoped<GovernanceAuditInterceptor>();
        builder.Services.AddDbContextFactory<AppDbContext>((provider, options) =>
            options.UseSqlServer(builder.Configuration.GetConnectionString("Documents")
                ?? throw new InvalidOperationException("Falta ConnectionStrings:Documents."),
                sql => sql.UseCompatibilityLevel(150))
                .AddInterceptors(provider.GetRequiredService<GovernanceAuditInterceptor>()), ServiceLifetime.Scoped);
        builder.Services.AddScoped<LocalSession>();
        builder.Services.Configure<GovernanceOptions>(builder.Configuration.GetSection("Governance"));
        builder.Services.AddScoped<ApprovalService>();
        builder.Services.AddScoped<RoutineService>();
        builder.Services.AddHostedService<GovernanceScheduler>();
        builder.Services.Configure<AiGovernanceOptions>(builder.Configuration.GetSection("AiGovernance"));
        builder.Services.AddScoped<AiGovernanceService>();
        builder.Services.AddScoped<IAiGovernance>(provider => provider.GetRequiredService<AiGovernanceService>());
        builder.Services.AddSingleton<PdfProcessor>();
        builder.Services.AddScoped<DocumentService>();
        builder.Services.AddScoped<DocumentQualityService>();
        builder.Services.AddScoped<CatalogSuggester>();
        builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection("Embeddings"));
        var embeddingBase = builder.Configuration["Embeddings:BaseUrl"] ?? "https://api.pinecone.io";
        if (!Uri.TryCreate(embeddingBase, UriKind.Absolute, out var embeddingUri) || embeddingUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Embeddings:BaseUrl debe ser una URL HTTP, por ejemplo https://api.pinecone.io.");
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
        builder.Services.AddScoped<IRagEmbedder, PineconeRagEmbedder>();
        builder.Services.AddSingleton<RagVectorCache>();
        builder.Services.AddScoped(provider => new SqlRagVectorStore(
            provider.GetRequiredService<IDbContextFactory<AppDbContext>>(),
            provider.GetRequiredService<IOptions<EmbeddingOptions>>(),
            provider.GetRequiredService<ILogger<SqlRagVectorStore>>(),
            provider.GetRequiredService<IOptions<RagOptions>>().Value.VectorCache ? provider.GetRequiredService<RagVectorCache>() : null));
        builder.Services.AddScoped<PineconeRagVectorStore>();
        builder.Services.AddScoped<IRagVectorStore>(provider =>
        {
            var rag = provider.GetRequiredService<IOptions<RagOptions>>().Value;
            return string.Equals(rag.VectorStore, "pinecone", StringComparison.OrdinalIgnoreCase)
                ? provider.GetRequiredService<PineconeRagVectorStore>()
                : provider.GetRequiredService<SqlRagVectorStore>();
        });
        builder.Services.AddScoped<IRagReranker, CrossEncoderRagReranker>();
        builder.Services.AddScoped<SemanticRagReranker>();
        builder.Services.AddHttpClient("rag-reranker", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(Math.Max(5, builder.Configuration.GetValue("Rag:RerankerTimeoutSeconds", 60)));
        });
        builder.Services.AddScoped<IRagRetriever, HybridRagRetriever>();
        builder.Services.AddScoped<RagIngestionService>();
        builder.Services.AddScoped<RagSourceStore>();
        builder.Services.AddScoped<RagQueryService>();
        builder.Services.AddScoped<LangGraphRagService>();
        builder.Services.AddSingleton<ILangGraphRunner, ProcessLangGraphRunner>();
        builder.Services.AddScoped<RagEvaluationService>();
        builder.Services.AddScoped<RagObservabilityService>();
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
        // MCP HTTP (Fase 2): herramientas read-only con bearer configurado fuera del cliente.
        builder.Services.AddHttpContextAccessor();
        builder.Services.Configure<McpHttpOptions>(builder.Configuration.GetSection("Mcp:Http"));
        builder.Services.AddScoped<McpIdentity>();
        builder.Services.AddScoped<ReadOnlyDocumentTools>();
        builder.Services.AddScoped<McpTokenService>();
        builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();
    }
}
