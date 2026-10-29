using DocuSearch.Components;
using DocuSearch.Data;
using DocuSearch.Services;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Data.Common;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents(options =>
    options.DetailedErrors = builder.Environment.IsDevelopment());
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

// El selector de perfiles conserva la demo original y nunca debe exponerse a una red.
app.Use(async (context, next) =>
{
    var remote = context.Connection.RemoteIpAddress;
    var host = context.Request.Host.Host;
    if (remote is null || !IPAddress.IsLoopback(remote) ||
        host is not ("localhost" or "127.0.0.1" or "[::1]" or "::1"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Esta demostración solo admite conexiones locales. Se requiere autenticación antes de publicarla en una red.");
        return;
    }
    await next(context);
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapGet("/health", async (IDbContextFactory<AppDbContext> factory, ILogger<Program> logger, Microsoft.Extensions.Options.IOptions<OllamaOptions> assistant) =>
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
