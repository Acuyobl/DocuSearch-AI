using System.Data;
using System.Data.Common;
using System.Diagnostics;
using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services;

public sealed record RoutineSpec(string Key, string Name, string Description, int IntervalMinutes);

public sealed record RoutineStatus(RoutineDefinition Routine, IReadOnlyList<RoutineRun> Runs);

/// <summary>Reglas puras de planificación.</summary>
public static class RoutineSchedule
{
    public const string Scan = "ESCANEO";
    public const string DailySummary = "RESUMEN_DIARIO";
    public const string Cleanup = "LIMPIEZA";
    public const int MaxOutputChars = 2000;

    public static readonly IReadOnlyList<RoutineSpec> Specs =
    [
        new(Scan, "Escaneo de salud", "Revisa el índice RAG (AG-10) y los riesgos de la bitácora de las últimas 24 h (AG-05); deja una alerta si hay hallazgos.", 60),
        new(DailySummary, "Resumen diario", "Registra en la bitácora las métricas del día: cargas, consultas, accesos denegados y aprobaciones pendientes.", 1440),
        new(Cleanup, "Limpieza", "Purga trazas del agente según la retención configurada, vence aprobaciones sin decidir y recorta el historial de rutinas. La bitácora no se borra nunca.", 1440)
    ];

    public static bool IsDue(RoutineDefinition routine, DateTimeOffset now) =>
        routine.Enabled && (routine.NextRunUtc is null || routine.NextRunUtc <= now);

    public static DateTimeOffset Next(RoutineDefinition routine, DateTimeOffset now) =>
        now.AddMinutes(Math.Max(1, routine.IntervalMinutes));

    public static string Bound(string output) =>
        output.Length <= MaxOutputChars ? output : output[..(MaxOutputChars - 1)] + "…";
}

/// <summary>
/// Rutinas programadas con historial. Cada ejecución toma un bloqueo de aplicación en SQL Server
/// (sp_getapplock), así dos instancias sobre la misma base no repiten la misma rutina.
/// </summary>
public sealed class RoutineService(
    IDbContextFactory<AppDbContext> factory,
    LocalSession session,
    IOptions<GovernanceOptions> options,
    IConfiguration configuration,
    IServiceProvider services,
    ILogger<RoutineService> logger,
    TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    private async Task<AppUser> UserAsync(AppDbContext db, Func<AppUser, bool> allowed, string message)
    {
        if (!session.IsAuthenticated) throw new UnauthorizedAccessException("No hay una sesión autenticada.");
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == session.UserId && item.Status == "Activo");
        if (user is null || !allowed(user)) throw new UnauthorizedAccessException(message);
        return user;
    }

    private async Task EnsureAsync(AppDbContext db)
    {
        var existing = await db.Routines.Select(routine => routine.Key).ToListAsync();
        var now = clock.GetUtcNow();
        foreach (var spec in RoutineSchedule.Specs.Where(spec => !existing.Contains(spec.Key)))
            db.Routines.Add(new RoutineDefinition
            {
                Key = spec.Key, Name = spec.Name, Description = spec.Description,
                IntervalMinutes = spec.IntervalMinutes, Enabled = true, NextRunUtc = now.AddMinutes(1)
            });
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync();
    }

    public async Task<List<RoutineStatus>> ListAsync(int runsPerRoutine = 10)
    {
        await using var db = await factory.CreateDbContextAsync();
        await UserAsync(db, Permissions.CanAudit, "Solo administración general y auditoría pueden consultar las rutinas.");
        await EnsureAsync(db);
        var routines = await db.Routines.AsNoTracking().OrderBy(routine => routine.Key).ToListAsync();
        var result = new List<RoutineStatus>();
        foreach (var routine in routines)
            result.Add(new(routine, await db.RoutineRuns.AsNoTracking().Where(run => run.RoutineKey == routine.Key)
                .OrderByDescending(run => run.StartedUtc).Take(Math.Clamp(runsPerRoutine, 1, 50)).ToListAsync()));
        return result;
    }

    public async Task SetEnabledAsync(string key, bool enabled)
    {
        await using var db = await factory.CreateDbContextAsync();
        var user = await UserAsync(db, Permissions.CanAdmin, "Solo administración general puede pausar o reanudar rutinas.");
        await EnsureAsync(db);
        var routine = await db.Routines.SingleOrDefaultAsync(item => item.Key == key)
            ?? throw new System.ComponentModel.DataAnnotations.ValidationException("La rutina no existe.");
        routine.Enabled = enabled;
        if (enabled) routine.NextRunUtc = RoutineSchedule.Next(routine, clock.GetUtcNow());
        db.Audit.Add(new AuditEvent
        {
            UserName = user.Name, UserRole = user.Role, Action = enabled ? "routine.enabled" : "routine.paused",
            Details = $"{routine.Name} ({routine.Key}) {(enabled ? "reanudada" : "pausada")}.", Entity = nameof(RoutineDefinition), EntityId = key
        });
        await db.SaveChangesAsync();
    }

    /// <summary>«Ejecutar ahora» (solo administración general).</summary>
    public async Task<RoutineRun?> RunNowAsync(string key)
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            var user = await UserAsync(db, Permissions.CanAdmin, "Solo administración general puede ejecutar rutinas.");
            await EnsureAsync(db);
            return await ExecuteAsync(key, $"manual: {user.Name}");
        }
    }

    /// <summary>Tick del planificador: ejecuta las rutinas activas que tocan.</summary>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken = default)
    {
        List<string> due;
        await using (var db = await factory.CreateDbContextAsync(cancellationToken))
        {
            await EnsureAsync(db);
            var now = clock.GetUtcNow();
            due = (await db.Routines.AsNoTracking().ToListAsync(cancellationToken))
                .Where(routine => RoutineSchedule.IsDue(routine, now)).Select(routine => routine.Key).ToList();
        }
        var ran = 0;
        foreach (var key in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ExecuteAsync(key, "scheduler", cancellationToken) is not null) ran++;
        }
        return ran;
    }

    /// <summary>Ejecuta una rutina con bloqueo; devuelve null si otra instancia la está ejecutando.</summary>
    private async Task<RoutineRun?> ExecuteAsync(string key, string trigger, CancellationToken cancellationToken = default)
    {
        if (RoutineSchedule.Specs.All(spec => spec.Key != key))
            throw new System.ComponentModel.DataAnnotations.ValidationException("La rutina no existe.");
        await using var lockDb = await factory.CreateDbContextAsync(cancellationToken);
        var connection = lockDb.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            if (!await AppLockAsync(connection, "sp_getapplock", key, cancellationToken))
            {
                logger.LogInformation("Rutina {Routine} omitida: otra instancia la está ejecutando.", key);
                return null;
            }
            var started = clock.GetUtcNow();
            var timer = Stopwatch.StartNew();
            string status = "OK", output;
            try
            {
                output = key switch
                {
                    RoutineSchedule.Scan => await ScanAsync(),
                    RoutineSchedule.DailySummary => await DailySummaryAsync(),
                    _ => await CleanupAsync()
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status = "ERROR";
                output = $"{ex.GetType().Name}: {ex.Message}";
                logger.LogWarning("La rutina {Routine} falló ({Error}).", key, ex.GetType().Name);
            }
            var run = new RoutineRun
            {
                RoutineKey = key, StartedUtc = started, DurationMs = timer.ElapsedMilliseconds,
                Status = status, Output = RoutineSchedule.Bound(output), Trigger = TextAnalysis.Truncate(trigger, 190)
            };
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var routine = await db.Routines.SingleAsync(item => item.Key == key, cancellationToken);
            routine.LastRunUtc = started;
            routine.LastStatus = status;
            routine.NextRunUtc = RoutineSchedule.Next(routine, clock.GetUtcNow());
            db.RoutineRuns.Add(run);
            db.Audit.Add(new AuditEvent
            {
                UserName = trigger.StartsWith("manual", StringComparison.Ordinal) ? session.DisplayName : "Planificador",
                UserRole = trigger.StartsWith("manual", StringComparison.Ordinal) ? session.Role : "system",
                Action = "routine.run", Details = TextAnalysis.Truncate($"{routine.Name} ({trigger}): {status}. {output}", 2000),
                Status = status == "OK" ? "EXITOSO" : "ADVERTENCIA", Entity = nameof(RoutineDefinition), EntityId = key,
                DurationMs = run.DurationMs
            });
            await db.SaveChangesAsync(cancellationToken);
            return run;
        }
        finally
        {
            try { await AppLockAsync(connection, "sp_releaseapplock", key, CancellationToken.None); }
            catch (DbException) { /* el cierre de la conexión lo libera igualmente */ }
            await connection.CloseAsync();
        }
    }

    private static async Task<bool> AppLockAsync(DbConnection connection, string procedure, string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = procedure;
        command.CommandType = CommandType.StoredProcedure;
        void Add(string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        Add("@Resource", $"docusearch:routine:{key}");
        Add("@LockOwner", "Session");
        if (procedure == "sp_getapplock")
        {
            Add("@LockMode", "Exclusive");
            Add("@LockTimeout", 0);
        }
        var result = command.CreateParameter();
        result.ParameterName = "@Result";
        result.Direction = ParameterDirection.ReturnValue;
        result.DbType = DbType.Int32;
        command.Parameters.Add(result);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return result.Value is int code && code >= 0;
    }

    private async Task<string> ScanAsync()
    {
        var quality = services.GetRequiredService<DocumentQualityService>();
        var index = await quality.InspectRagIndexForRoutineAsync();
        var now = clock.GetUtcNow();
        List<AuditEvent> events;
        await using (var db = await factory.CreateDbContextAsync())
            events = await db.Audit.AsNoTracking().Where(item => item.TimestampUTC >= now.AddHours(-24)).ToListAsync();
        var risk = AuditInspector.Analyze(events, now.AddHours(-24), now);
        var output = $"Índice RAG: {index.ReindexDocumentIds.Count} expediente(s) por reindexar de {index.IndexableDocuments} con texto " +
            $"({index.PageCoverage:P0} de páginas cubiertas). Bitácora 24 h: {risk.Alerts.Count} alerta(s), {risk.AccessDenied} acceso(s) denegado(s), " +
            $"{risk.FailedLogins} inicio(s) de sesión fallido(s).";
        if (index.ReindexDocumentIds.Count > 0 || risk.Alerts.Count > 0)
        {
            await using var db = await factory.CreateDbContextAsync();
            db.Audit.Add(new AuditEvent
            {
                UserName = "Planificador", UserRole = "system", Action = "routine.alert", Status = "ADVERTENCIA",
                Details = TextAnalysis.Truncate(output + (risk.Alerts.Count > 0 ? " Alertas: " + string.Join("; ", risk.Alerts.Select(alert => alert.Title)) : ""), 2000),
                Entity = nameof(RoutineDefinition), EntityId = RoutineSchedule.Scan
            });
            await db.SaveChangesAsync();
        }
        return output;
    }

    private async Task<string> DailySummaryAsync()
    {
        var now = clock.GetUtcNow();
        var from = now.AddHours(-24);
        await using var db = await factory.CreateDbContextAsync();
        var counts = await db.Audit.AsNoTracking().Where(item => item.TimestampUTC >= from)
            .GroupBy(item => item.Action).Select(group => new { group.Key, Count = group.Count() }).ToListAsync();
        int Count(params string[] actions) => counts.Where(item => actions.Contains(item.Key)).Sum(item => item.Count);
        var pending = await db.Approvals.CountAsync(item => item.Status == ApprovalStatus.Pending && item.ExpiresUtc > now);
        var output = $"Últimas 24 h: {Count("UPLOAD")} carga(s), {Count("DELETE")} eliminación(es), " +
            $"{Count("RAG_QUERY", "AGENT_QUERY")} consulta(s) al asistente, {Count("SEARCH")} búsqueda(s), " +
            $"{Count("LOGIN")} inicio(s) de sesión, {Count("ACCESS_DENIED")} acceso(s) denegado(s), {pending} aprobación(es) pendiente(s).";
        db.Audit.Add(new AuditEvent
        {
            UserName = "Planificador", UserRole = "system", Action = "routine.daily_summary", Details = output,
            Entity = nameof(RoutineDefinition), EntityId = RoutineSchedule.DailySummary
        });
        await db.SaveChangesAsync();
        return output;
    }

    private async Task<string> CleanupAsync()
    {
        var now = clock.GetUtcNow();
        var retention = Math.Max(1, configuration.GetValue("Observability:RetentionDays", 90));
        var historyDays = Math.Max(1, options.Value.RoutineHistoryDays);
        await using var db = await factory.CreateDbContextAsync();
        var traces = await db.AgentTraces.Where(trace => trace.StartedUtc < now.AddDays(-retention)).ExecuteDeleteAsync();
        var runs = await db.RoutineRuns.Where(run => run.StartedUtc < now.AddDays(-historyDays)).ExecuteDeleteAsync();
        var expired = await services.GetRequiredService<ApprovalService>().ExpireAsync();
        return $"Trazas > {retention} días: {traces} purgada(s). Aprobaciones vencidas: {expired}. Historial de rutinas > {historyDays} días: {runs} eliminado(s). La bitácora se conserva íntegra.";
    }
}

/// <summary>Planificador en proceso. Solo arranca con Governance:SchedulerEnabled=true.</summary>
public sealed class GovernanceScheduler(IServiceScopeFactory scopes, IOptions<GovernanceOptions> options,
    ILogger<GovernanceScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.SchedulerEnabled)
        {
            logger.LogInformation("Planificador de rutinas desactivado (Governance:SchedulerEnabled=false).");
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(15, options.Value.SchedulerTickSeconds)));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var ran = await scope.ServiceProvider.GetRequiredService<RoutineService>().RunDueAsync(stoppingToken);
                if (ran > 0) logger.LogInformation("Planificador: {Count} rutina(s) ejecutada(s).", ran);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Tick del planificador fallido ({Error}).", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
