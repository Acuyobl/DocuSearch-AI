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
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/audit", async (HttpContext context, LocalSession session, IDbContextFactory<AppDbContext> factory,
            string? action, string? entity, string? actor, string? status, DateTimeOffset? from, DateTimeOffset? to,
            string? q, int? page, int? pageSize) =>
        {
            await using var db = await factory.CreateDbContextAsync();
            if (await AuditorAsync(context, session, db) is null) return Results.Forbid();
            var size = Math.Clamp(pageSize ?? 50, 1, AuditQuery.MaxPageSize);
            var number = Math.Max(1, page ?? 1);
            var query = AuditQuery.Apply(db.Audit.AsNoTracking(), new AuditFilter(action, entity, actor, status, from, to, q));
            var total = await query.CountAsync();
            var items = await query.OrderByDescending(item => item.TimestampUTC).Skip((number - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { total, page = number, pageSize = size, items });
        });

        api.MapGet("/audit/summary", async (HttpContext context, LocalSession session, IDbContextFactory<AppDbContext> factory, int? days) =>
        {
            await using var db = await factory.CreateDbContextAsync();
            if (await AuditorAsync(context, session, db) is null) return Results.Forbid();
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
        });

        api.MapGet("/approvals", async (HttpContext context, LocalSession session, ApprovalService approvals) =>
        {
            session.SignIn(context.User);
            try { return Results.Ok(await approvals.ListAsync()); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });

        api.MapGet("/routines", async (HttpContext context, LocalSession session, RoutineService routines) =>
        {
            session.SignIn(context.User);
            try { return Results.Ok(await routines.ListAsync()); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });

        // Consumo de IA (ai:usage_read). Usuario siempre desde la cookie; los importes van en micro-dólares enteros.
        api.MapGet("/ai/usage", async (HttpContext context, LocalSession session, AiGovernanceService governance, string? period) =>
        {
            session.SignIn(context.User);
            try { return Results.Ok(await governance.GetUsageAsync(period)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (FormatException) { return Results.BadRequest(new { error = "El periodo debe tener el formato YYYY-MM." }); }
        });

        api.MapGet("/ai/budget", async (HttpContext context, LocalSession session, AiGovernanceService governance) =>
        {
            session.SignIn(context.User);
            try
            {
                var usage = await governance.GetUsageAsync();
                return Results.Ok(new { period = usage.Period, policy = usage.Policy, spentMicroUsd = usage.SpentMicroUsd, usage.PercentUsed, incidents = usage.Incidents });
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });
    }

    private static async Task<AppUser?> AuditorAsync(HttpContext context, LocalSession session, AppDbContext db)
    {
        session.SignIn(context.User);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == session.UserId && item.Status == "Activo");
        return user is not null && Permissions.CanAudit(user) ? user : null;
    }
}
