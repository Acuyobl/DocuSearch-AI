using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services;

/// <summary>La acción quedó en cola de aprobación: no se aplicó nada (equivale a HTTP 202).</summary>
public sealed class ApprovalPendingException(ApprovalRequest request, bool existing)
    : Exception(existing
        ? $"Ya hay una solicitud pendiente para «{SensitiveAction.Label(request.Action)}» sobre {request.EntityLabel}. Se aplicará cuando la apruebe auditoría."
        : $"Solicitud enviada a aprobación: «{SensitiveAction.Label(request.Action)}» sobre {request.EntityLabel}. No se aplicará hasta que la apruebe otra persona (auditoría).")
{
    public ApprovalRequest Request { get; } = request;
}

/// <summary>La solicitud ya fue resuelta o venció (equivale a HTTP 409).</summary>
public sealed class ApprovalConflictException(string message) : Exception(message);

/// <summary>Ejecución de una acción aprobada: se aplica con los permisos de quien la pidió.</summary>
public sealed record ApprovalExecution(string ApprovalId, string RequesterId, string ApproverName);

/// <summary>
/// Contexto ambiente de una aplicación aprobada. Mientras está activo, los servicios resuelven
/// el actor como el solicitante (revalidado) y no vuelven a pedir aprobación.
/// </summary>
public static class ApprovalContext
{
    private static readonly AsyncLocal<ApprovalExecution?> current = new();
    public static ApprovalExecution? Current => current.Value;

    internal static IDisposable Begin(ApprovalExecution execution)
    {
        var previous = current.Value;
        current.Value = execution;
        return new Restore(() => current.Value = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

/// <summary>Datos de una edición de metadatos pendiente (sin texto de páginas).</summary>
public sealed record DocumentEditPayload(string Title, string Author, string Department, string Category, string Version,
    string Confidentiality, string ExecutiveSummary, string[] Keywords, string[] Tags);

/// <summary>Reglas puras del doble control.</summary>
public static class ApprovalRules
{
    public static bool Requires(GovernanceOptions options, string action) =>
        options.ApprovalsEnabled && options.SensitiveActions.Contains(action, StringComparer.Ordinal);

    /// <summary>Doble control: decide otra persona, con rol de auditoría o administración general.</summary>
    public static bool CanDecide(AppUser user) => user.Role is "auditor" or "admin_general";

    /// <summary>Devuelve por qué no se puede decidir, o null si se puede.</summary>
    public static string? DecisionError(AppUser decider, ApprovalRequest request, DateTimeOffset now)
    {
        if (!CanDecide(decider)) return "Solo auditoría o administración general pueden decidir solicitudes.";
        if (string.Equals(decider.Id, request.RequestedById, StringComparison.Ordinal))
            return "No puedes decidir tu propia solicitud: el doble control exige a otra persona.";
        if (request.Status != ApprovalStatus.Pending) return $"La solicitud ya está {request.Status}.";
        if (request.ExpiresUtc <= now) return "La solicitud venció sin decisión.";
        return null;
    }

    /// <summary>Bajar de nivel expone el documento a más personas; subirlo no requiere aprobación.</summary>
    public static bool IsDowngrade(string from, string to)
    {
        var before = Array.IndexOf(Catalog.Levels, from);
        var after = Array.IndexOf(Catalog.Levels, to);
        return before >= 0 && after >= 0 && after < before;
    }
}

/// <summary>Cola de aprobaciones de doble paso con auditoría de cada transición.</summary>
public sealed class ApprovalService(
    IDbContextFactory<AppDbContext> factory,
    LocalSession session,
    IOptions<GovernanceOptions> options,
    IServiceProvider services,
    TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    public GovernanceOptions Settings => options.Value;

    public bool Requires(string action) => ApprovalContext.Current is null && ApprovalRules.Requires(options.Value, action);

    private async Task<AppUser> ActorAsync(AppDbContext db)
    {
        if (!session.IsAuthenticated) throw new UnauthorizedAccessException("No hay una sesión autenticada.");
        return await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == session.UserId && user.Status == "Activo")
            ?? throw new UnauthorizedAccessException("El usuario autenticado no existe o está inactivo.");
    }

    private static void Audit(AppDbContext db, AppUser actor, string action, ApprovalRequest request, string details, string status = "EXITOSO") =>
        db.Audit.Add(new AuditEvent
        {
            UserName = actor.Name, UserRole = actor.Role, Action = action,
            DocumentCode = request.EntityType == nameof(DocumentItem) ? request.EntityLabel.Split(' ')[0] : "SYSTEM",
            Details = TextAnalysis.Truncate(details, 2000), Status = status,
            Entity = nameof(ApprovalRequest), EntityId = request.Id
        });

    /// <summary>
    /// Registra la solicitud (o devuelve la pendiente equivalente) y lanza
    /// <see cref="ApprovalPendingException"/>: quien llama no debe aplicar nada.
    /// </summary>
    public async Task RequestAsync(string action, string entityType, string entityId, string entityLabel,
        object payload, string summary)
    {
        await using var db = await factory.CreateDbContextAsync();
        var actor = await ActorAsync(db);
        var now = clock.GetUtcNow();
        var existing = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(item =>
            item.Action == action && item.EntityId == entityId && item.Status == ApprovalStatus.Pending && item.ExpiresUtc > now);
        if (existing is not null) throw new ApprovalPendingException(existing, existing: true);

        var request = new ApprovalRequest
        {
            Action = action, EntityType = entityType, EntityId = entityId,
            EntityLabel = TextAnalysis.Truncate(entityLabel, 290),
            PayloadJson = JsonSerializer.Serialize(payload, Catalog.Json),
            Summary = TextAnalysis.Truncate(summary, 490),
            RequestedById = actor.Id, RequestedByName = actor.Name, RequestedByRole = actor.Role,
            RequestedUtc = now, ExpiresUtc = now.AddHours(Math.Max(1, options.Value.ApprovalExpiryHours))
        };
        db.Approvals.Add(request);
        Audit(db, actor, "approval.requested", request, $"{SensitiveAction.Label(action)}: {request.EntityLabel}. {request.Summary}");
        await db.SaveChangesAsync();
        throw new ApprovalPendingException(request, existing: false);
    }

    /// <summary>Auditoría y administración ven toda la cola; el resto, solo sus solicitudes.</summary>
    public async Task<List<ApprovalRequest>> ListAsync(int take = 200)
    {
        await using var db = await factory.CreateDbContextAsync();
        var actor = await ActorAsync(db);
        await ExpireCoreAsync(db, actor.Name, actor.Role);
        var query = db.Approvals.AsNoTracking();
        if (!Permissions.CanAudit(actor)) query = query.Where(item => item.RequestedById == actor.Id);
        return await query.OrderByDescending(item => item.Status == ApprovalStatus.Pending)
            .ThenByDescending(item => item.RequestedUtc).Take(Math.Clamp(take, 1, 500)).ToListAsync();
    }

    public async Task<int> PendingCountAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var actor = await ActorAsync(db);
        var now = clock.GetUtcNow();
        var query = db.Approvals.Where(item => item.Status == ApprovalStatus.Pending && item.ExpiresUtc > now);
        if (!Permissions.CanAudit(actor)) query = query.Where(item => item.RequestedById == actor.Id);
        return await query.CountAsync();
    }

    public async Task RejectAsync(string id, string? note)
    {
        await using var db = await factory.CreateDbContextAsync();
        var actor = await ActorAsync(db);
        var request = await LoadForDecisionAsync(db, actor, id);
        var claimed = await Claim(db, id, ApprovalStatus.Rejected, actor, note);
        if (claimed == 0) throw new ApprovalConflictException("La solicitud ya fue resuelta por otra persona.");
        Audit(db, actor, "approval.rejected", request, $"Rechazada: {SensitiveAction.Label(request.Action)} sobre {request.EntityLabel}. {note}");
        await db.SaveChangesAsync();
    }

    /// <summary>El solicitante puede retirar su propia solicitud; nunca puede aprobarla.</summary>
    public async Task CancelAsync(string id, string? note)
    {
        await using var db = await factory.CreateDbContextAsync();
        var actor = await ActorAsync(db);
        var request = await db.Approvals.SingleOrDefaultAsync(item => item.Id == id)
            ?? throw new ApprovalConflictException("La solicitud no existe.");
        if (!string.Equals(request.RequestedById, actor.Id, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Solo el solicitante puede retirar su propia solicitud.");
        if (request.Status != ApprovalStatus.Pending)
            throw new ApprovalConflictException("La solicitud ya fue resuelta.");
        var claimed = await Claim(db, id, ApprovalStatus.Rejected, actor, note);
        if (claimed == 0) throw new ApprovalConflictException("La solicitud ya fue resuelta por otra persona.");
        Audit(db, actor, "approval.cancelled", request, $"Retirada por el solicitante: {SensitiveAction.Label(request.Action)} sobre {request.EntityLabel}. {note}");
        await db.SaveChangesAsync();
    }

    public async Task ApproveAsync(string id, string? note)
    {
        ApprovalRequest request;
        AppUser actor;
        await using (var db = await factory.CreateDbContextAsync())
        {
            actor = await ActorAsync(db);
            request = await LoadForDecisionAsync(db, actor, id);
            var claimed = await Claim(db, id, ApprovalStatus.Approved, actor, note);
            if (claimed == 0) throw new ApprovalConflictException("La solicitud ya fue resuelta por otra persona.");
            Audit(db, actor, "approval.approved", request, $"Aprobada: {SensitiveAction.Label(request.Action)} sobre {request.EntityLabel}. {note}");
            await db.SaveChangesAsync();
        }

        try
        {
            using (ApprovalContext.Begin(new ApprovalExecution(request.Id, request.RequestedById, actor.Name)))
                await ExecuteAsync(request);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await using var db = await factory.CreateDbContextAsync();
            await db.Approvals.Where(item => item.Id == id).ExecuteUpdateAsync(setter => setter
                .SetProperty(item => item.Status, ApprovalStatus.Failed)
                .SetProperty(item => item.DecisionNote, TextAnalysis.Truncate($"No se pudo aplicar: {ex.Message}", 990)));
            Audit(db, actor, "approval.failed", request, $"Aprobada pero no aplicada ({ex.GetType().Name}): {ex.Message}", "ADVERTENCIA");
            await db.SaveChangesAsync();
            throw new InvalidOperationException($"La solicitud se aprobó, pero no se pudo aplicar: {ex.Message}", ex);
        }
    }

    /// <summary>Marca como vencidas las solicitudes sin decisión pasado su plazo.</summary>
    public async Task<int> ExpireAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await ExpireCoreAsync(db, "Sistema", "system");
    }

    private async Task<int> ExpireCoreAsync(AppDbContext db, string actorName, string actorRole)
    {
        var now = clock.GetUtcNow();
        var expired = await db.Approvals
            .Where(item => item.Status == ApprovalStatus.Pending && item.ExpiresUtc <= now)
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(item => item.Status, ApprovalStatus.Expired)
                .SetProperty(item => item.DecidedUtc, now));
        if (expired > 0)
        {
            db.Audit.Add(new AuditEvent
            {
                UserName = actorName, UserRole = actorRole, Action = "approval.expired",
                Details = $"{expired} solicitud(es) vencida(s) sin decisión.", Status = "ADVERTENCIA",
                Entity = nameof(ApprovalRequest)
            });
            await db.SaveChangesAsync();
        }
        return expired;
    }

    private async Task<ApprovalRequest> LoadForDecisionAsync(AppDbContext db, AppUser actor, string id)
    {
        var request = await db.Approvals.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id)
            ?? throw new ValidationException("La solicitud no existe.");
        var error = ApprovalRules.DecisionError(actor, request, clock.GetUtcNow());
        if (error is null) return request;
        if (!ApprovalRules.CanDecide(actor)) throw new UnauthorizedAccessException(error);
        if (request.Status != ApprovalStatus.Pending || request.ExpiresUtc <= clock.GetUtcNow())
            throw new ApprovalConflictException(error);
        throw new ValidationException(error);
    }

    /// <summary>Transición atómica desde «pendiente»: si otra persona ya decidió, no cambia ninguna fila.</summary>
    private Task<int> Claim(AppDbContext db, string id, string status, AppUser actor, string? note)
    {
        var now = clock.GetUtcNow();
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : TextAnalysis.Truncate(note.Trim(), 990);
        return db.Approvals
            .Where(item => item.Id == id && item.Status == ApprovalStatus.Pending && item.ExpiresUtc > now)
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(item => item.Status, status)
                .SetProperty(item => item.DecidedById, actor.Id)
                .SetProperty(item => item.DecidedByName, actor.Name)
                .SetProperty(item => item.DecidedUtc, now)
                .SetProperty(item => item.DecisionNote, trimmed));
    }

    /// <summary>Aplica la acción con los servicios normales, que revalidan los permisos del solicitante.</summary>
    private async Task ExecuteAsync(ApprovalRequest request)
    {
        var documents = services.GetRequiredService<DocumentService>();
        switch (request.Action)
        {
            case SensitiveAction.DeleteDocument:
                await documents.DeleteAsync(request.EntityId);
                break;
            case SensitiveAction.DowngradeConfidentiality:
                var edit = JsonSerializer.Deserialize<DocumentEditPayload>(request.PayloadJson, Catalog.Json)
                    ?? throw new InvalidDataException("Datos de la edición inválidos.");
                await documents.EditAsync(new DocumentItem
                {
                    Id = request.EntityId, Title = edit.Title, Author = edit.Author, Department = edit.Department,
                    Category = edit.Category, Version = edit.Version, Confidentiality = edit.Confidentiality,
                    ExecutiveSummary = edit.ExecutiveSummary, Keywords = edit.Keywords, Tags = edit.Tags
                });
                break;
            case SensitiveAction.ChangeUserRole:
            case SensitiveAction.DeleteUser:
                var user = JsonSerializer.Deserialize<AppUser>(request.PayloadJson, Catalog.Json)
                    ?? throw new InvalidDataException("Datos del usuario inválidos.");
                await documents.SaveUserAsync(user, delete: request.Action == SensitiveAction.DeleteUser);
                break;
            case SensitiveAction.Reset:
                await documents.ResetAsync("RESTABLECER");
                break;
            case SensitiveAction.PurgeTraces:
                using (var payload = JsonDocument.Parse(request.PayloadJson))
                    await services.GetRequiredService<AgentObservability>().PurgeAsync(payload.RootElement.GetProperty("days").GetInt32());
                break;
            default:
                throw new InvalidOperationException($"Acción no soportada: {request.Action}.");
        }
    }
}
