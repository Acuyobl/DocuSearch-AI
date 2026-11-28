using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuSearch.Services;

/// <summary>Filtros de la bitácora (UI y API).</summary>
public sealed record AuditFilter(string? Action = null, string? Entity = null, string? Actor = null, string? Status = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, string? Text = null);

public static class AuditQuery
{
    public const int MaxPageSize = 200;

    public static IQueryable<AuditEvent> Apply(IQueryable<AuditEvent> events, AuditFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Action)) events = events.Where(item => item.Action == filter.Action);
        if (!string.IsNullOrWhiteSpace(filter.Entity)) events = events.Where(item => item.Entity == filter.Entity);
        if (!string.IsNullOrWhiteSpace(filter.Actor)) events = events.Where(item => item.UserName.Contains(filter.Actor));
        if (!string.IsNullOrWhiteSpace(filter.Status)) events = events.Where(item => item.Status == filter.Status);
        if (filter.From is { } from) events = events.Where(item => item.TimestampUTC >= from);
        if (filter.To is { } to) events = events.Where(item => item.TimestampUTC <= to);
        if (!string.IsNullOrWhiteSpace(filter.Text))
            events = events.Where(item => item.Details.Contains(filter.Text) || item.DocumentCode.Contains(filter.Text) ||
                item.UserName.Contains(filter.Text) || (item.EntityId != null && item.EntityId.Contains(filter.Text)));
        return events;
    }
}

/// <summary>Endpoints de solo lectura de gobernanza; mismos permisos que las vistas (auditoría).</summary>
public static class GovernanceApi
{
    public static void MapGovernanceApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireSession();

        api.MapGet("/audit", async (IDbContextFactory<AppDbContext> factory,
            string? action, string? entity, string? actor, string? status, DateTimeOffset? from, DateTimeOffset? to,
            string? q, int? page, int? pageSize) =>
        {
            await using var db = await factory.CreateDbContextAsync();
            var size = Math.Clamp(pageSize ?? 50, 1, AuditQuery.MaxPageSize);
            var number = Math.Max(1, page ?? 1);
            var query = AuditQuery.Apply(db.Audit.AsNoTracking(), new AuditFilter(action, entity, actor, status, from, to, q));
            var total = await query.CountAsync();
            var items = await query.OrderByDescending(item => item.TimestampUTC).Skip((number - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { total, page = number, pageSize = size, items });
        }).RequireAuthorization(AccessPolicies.Auditor);

        api.MapGet("/audit/summary", async (IDbContextFactory<AppDbContext> factory, int? days) =>
        {
            await using var db = await factory.CreateDbContextAsync();
            var to = DateTimeOffset.UtcNow;
            var from = to.AddDays(-Math.Clamp(days ?? 7, 1, 90));
            var events = await db.Audit.AsNoTracking().Where(item => item.TimestampUTC >= from).ToListAsync();
            var risk = AuditInspector.Analyze(events, from, to);
            return Results.Ok(new
            {
                from, to, total = events.Count,
                byStatus = events.GroupBy(item => item.Status).ToDictionary(group => group.Key, group => group.Count()),
                byEntity = events.Where(item => item.Entity != null).GroupBy(item => item.Entity!).ToDictionary(group => group.Key, group => group.Count()),
                topActions = risk.TopActions,
                alerts = risk.Alerts
            });
        }).RequireAuthorization(AccessPolicies.Auditor);

        // Aprobaciones, rutinas y consumo de IA comprueban los permisos en su propio servicio.
        api.MapGet("/approvals", async (ApprovalService approvals) =>
        {
            try { return Results.Ok(await approvals.ListAsync()); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });

        api.MapGet("/routines", async (RoutineService routines) =>
        {
            try { return Results.Ok(await routines.ListAsync()); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });

        // Consumo de IA (ai:usage_read). Usuario siempre desde la cookie; los importes van en micro-dólares enteros.
        api.MapGet("/ai/usage", async (AiGovernanceService governance, string? period) =>
        {
            try { return Results.Ok(await governance.GetUsageAsync(period)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (FormatException) { return Results.BadRequest(new { error = "El periodo debe tener el formato YYYY-MM." }); }
        });

        api.MapGet("/ai/budget", async (AiGovernanceService governance) =>
        {
            try
            {
                var usage = await governance.GetUsageAsync();
                return Results.Ok(new { period = usage.Period, policy = usage.Policy, spentMicroUsd = usage.SpentMicroUsd, usage.PercentUsed, incidents = usage.Incidents });
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });
    }
}
