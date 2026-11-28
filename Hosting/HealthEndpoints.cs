using DocuSearch.Data;
using DocuSearch.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Data.Common;

namespace DocuSearch.Hosting;

/// <summary>Sondas de salud para orquestadores y diagnóstico.</summary>
public static class HealthEndpoints
{
    /// <summary>/health, /health/live y /health/ready.</summary>
    public static void MapDocuSearchHealth(this WebApplication app)
    {
        app.MapGet("/health", async (HttpContext context, IDbContextFactory<AppDbContext> factory, ILogger<Program> logger,
            AssistantRuntime runtime, IOptions<OpenCodeOptions> openCodeOptions) =>
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync();
                _ = await db.Users.CountAsync();
                // Sin sesión no se revela el proveedor ni el modelo de IA configurado.
                if (context.User.Identity?.IsAuthenticated != true)
                    return Results.Ok(new { status = "ready", database = "SQL Server" });
                var active = runtime.Active;
                var engine = active.Engine.ToLowerInvariant() switch
                {
                    "opencode" => (openCodeOptions.Value.UseLangChain ? "langchain+opencode:" : "opencode:") + $"{active.ProviderId}/{active.ModelId}",
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
    }
}
