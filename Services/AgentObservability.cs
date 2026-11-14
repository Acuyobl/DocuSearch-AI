using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Text.Json;
using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuSearch.Services;

public sealed class AgentObservability(IDbContextFactory<AppDbContext> factory, LocalSession session,
    ILogger<AgentObservability> logger)
{
    public async Task RecordAsync(AgentTrace trace)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            db.AgentTraces.Add(trace);
            await db.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or InvalidOperationException)
        {
            // No se registra la excepcion completa: un proveedor puede incluir datos sensibles.
            logger.LogError("No se pudo persistir la traza {TraceId}. Categoria: {ErrorType}.",
                trace.Id, ex.GetType().Name);
            throw new InvalidOperationException("No se pudo guardar la observabilidad del agente en SQL Server. Revisa el esquema y la consola del servidor.");
        }
    }

    public async Task<List<AgentTrace>> LoadAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == session.UserId && u.Status == "Activo");
        if (actor is null || !Permissions.CanAudit(actor))
            throw new UnauthorizedAccessException("Solo administración general y auditoría pueden consultar la observabilidad.");
        return await db.AgentTraces.AsNoTracking().OrderByDescending(t => t.StartedUtc).Take(500).ToListAsync();
    }

    /// <summary>Purga trazas antiguas (solo administración general) y deja el evento en la bitácora.</summary>
    public async Task<int> PurgeAsync(int retentionDays)
    {
        if (retentionDays < 1)
            throw new ValidationException("La retención debe ser de al menos 1 día.");
        await using var db = await factory.CreateDbContextAsync();
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == session.UserId && u.Status == "Activo");
        if (actor is null || !Permissions.CanAdmin(actor))
            throw new UnauthorizedAccessException("Solo administración general puede purgar trazas de observabilidad.");

        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var deleted = await db.AgentTraces.Where(trace => trace.StartedUtc < cutoff).ExecuteDeleteAsync();
        db.Audit.Add(new AuditEvent
        {
            UserName = actor.Name,
            UserRole = actor.Role,
            Action = "PURGE_TRACES",
            Details = $"Trazas anteriores a {cutoff:yyyy-MM-dd}: {deleted}.",
            DocumentCode = "SYSTEM",
            Status = "EXITOSO"
        });
        await db.SaveChangesAsync();
        return deleted;
    }

    public static string ErrorCode(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "access_denied",
        ValidationException => "invalid_input",
        DbException or DbUpdateException => "database",
        JsonException => "invalid_response",
        _ when exception.InnerException is not null => ErrorCode(exception.InnerException),
        OperationCanceledException => "timeout",
        HttpRequestException => "connection",
        IOException => "io",
        InvalidOperationException => "generation_or_validation",
        _ => "unexpected"
    };
}
