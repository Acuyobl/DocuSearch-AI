using System.Text.Encodings.Web;
using System.Text.Json;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DocuSearch.Services;

public sealed class GovernanceOptions
{
    /// <summary>Doble control para acciones sensibles. Apagado: el comportamiento no cambia.</summary>
    public bool ApprovalsEnabled { get; set; }
    /// <summary>Acciones que exigen aprobación cuando <see cref="ApprovalsEnabled"/> está activo.</summary>
    public string[] SensitiveActions { get; set; } = SensitiveAction.All;
    public int ApprovalExpiryHours { get; set; } = 72;
    /// <summary>Planificador de rutinas en proceso. Apagado: las rutinas solo se ejecutan a mano.</summary>
    public bool SchedulerEnabled { get; set; }
    public int SchedulerTickSeconds { get; set; } = 60;
    public int RoutineHistoryDays { get; set; } = 90;
}

/// <summary>Cambio de una entidad detectado al guardar.</summary>
public sealed record AuditChange(string Entity, string EntityId, string Operation,
    IReadOnlyDictionary<string, string?>? Before, IReadOnlyDictionary<string, string?>? After);

public sealed record AuditActor(string Name, string Role, string? Ip);

/// <summary>
/// Reglas puras de la bitácora de gobernanza: qué entidades se siguen, qué campos se omiten y
/// cómo se completa la bitácora con el estado antes/después.
/// </summary>
public static class AuditTrail
{
    public const int MaxSnapshotChars = 4000;

    // Conserva tildes y eñes legibles; la bitácora siempre se muestra como texto (Razor y CSV escapan).
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public const string UninstrumentedPrefix = "DATA.";

    /// <summary>Entidades de negocio. Las derivadas (índices, trazas, historial) no se registran campo a campo.</summary>
    public static readonly IReadOnlySet<string> TrackedEntities = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(DocumentItem), nameof(AppUser), nameof(DepartmentInfo), nameof(ApprovalRequest), nameof(RoutineDefinition), nameof(AiBudgetPolicy)
    };

    /// <summary>Campos pesados o con contenido documental: nunca van a la bitácora.</summary>
    public static readonly IReadOnlySet<string> OmittedProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(DocumentItem.PagesJson), nameof(DocumentItem.NotesJson), nameof(DocumentFile.Bytes),
        nameof(ApprovalRequest.PayloadJson), "Vector", "Text"
    };

    public static string? Snapshot(IReadOnlyDictionary<string, string?>? values) =>
        values is null || values.Count == 0 ? null : Bound(JsonSerializer.Serialize(values, SnapshotJson));

    private static string Bound(string json) =>
        json.Length <= MaxSnapshotChars ? json : json[..(MaxSnapshotChars - 1)] + "…";

    private static string? Combine(IReadOnlyList<AuditChange> changes, Func<AuditChange, IReadOnlyDictionary<string, string?>?> select)
    {
        if (changes.Count == 1) return Snapshot(select(changes[0]));
        var combined = changes
            .Where(change => select(change) is { Count: > 0 })
            .ToDictionary(change => $"{change.Entity}:{change.EntityId}", change => select(change));
        return combined.Count == 0 ? null : Bound(JsonSerializer.Serialize(combined, SnapshotJson));
    }

    /// <summary>
    /// Si el guardado ya trae su evento de bitácora (operación instrumentada), lo completa con la
    /// entidad, el antes/después y la IP. Si no lo trae, devuelve eventos nuevos «DATA.*» para que
    /// ninguna mutación quede sin rastro.
    /// </summary>
    public static IReadOnlyList<AuditEvent> Apply(IReadOnlyList<AuditChange> changes,
        IReadOnlyList<AuditEvent> explicitEvents, AuditActor actor, DateTimeOffset now)
    {
        foreach (var item in explicitEvents) item.Ip ??= actor.Ip;
        if (changes.Count == 0) return [];
        if (explicitEvents.Count > 0)
        {
            var target = explicitEvents[0];
            target.Entity ??= changes[0].Entity;
            target.EntityId ??= changes[0].EntityId;
            target.Before ??= Combine(changes, change => change.Before);
            target.After ??= Combine(changes, change => change.After);
            return [];
        }
        return changes.Select(change => new AuditEvent
        {
            TimestampUTC = now,
            UserName = actor.Name,
            UserRole = actor.Role,
            Action = UninstrumentedPrefix + change.Operation,
            DocumentCode = Code(change) ?? "SYSTEM",
            Details = $"Cambio registrado automáticamente: {change.Operation} de {change.Entity} {change.EntityId}.",
            Status = "EXITOSO",
            Entity = change.Entity,
            EntityId = change.EntityId,
            Before = Snapshot(change.Before),
            After = Snapshot(change.After),
            Ip = actor.Ip
        }).ToList();
    }

    private static string? Code(AuditChange change) =>
        change.Entity == nameof(DocumentItem)
            ? (change.After?.GetValueOrDefault(nameof(DocumentItem.InternalCode)) ?? change.Before?.GetValueOrDefault(nameof(DocumentItem.InternalCode)))
            : null;
}

/// <summary>
/// Equivalente al middleware HTTP en Blazor Server: las mutaciones no pasan por endpoints sino
/// por el circuito, así que se interceptan al guardar en EF Core. Nunca hace fallar el guardado.
/// No ve ExecuteUpdate/ExecuteDelete masivos; esas operaciones se auditan de forma explícita.
/// </summary>
public sealed class GovernanceAuditInterceptor(LocalSession session, ILogger<GovernanceAuditInterceptor>? logger = null)
    : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Capture(DbContext? context)
    {
        if (context is null) return;
        try
        {
            context.ChangeTracker.DetectChanges();
            var entries = context.ChangeTracker.Entries()
                .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .ToList();
            var explicitEvents = entries
                .Where(entry => entry is { Entity: AuditEvent, State: EntityState.Added })
                .Select(entry => (AuditEvent)entry.Entity)
                .ToList();
            var changes = entries
                .Where(entry => AuditTrail.TrackedEntities.Contains(entry.Metadata.ClrType.Name))
                .Select(Describe)
                .Where(change => change is not null)
                .Select(change => change!)
                .ToList();
            var actor = session.IsAuthenticated
                ? new AuditActor(session.DisplayName, session.Role, session.Ip)
                : new AuditActor("Sistema", "system", null);
            foreach (var item in AuditTrail.Apply(changes, explicitEvents, actor, DateTimeOffset.UtcNow))
                context.Add(item);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning("La bitácora de gobernanza no pudo completar el evento ({Error}); el guardado continúa.", ex.GetType().Name);
        }
    }

    public static AuditChange? Describe(EntityEntry entry)
    {
        var key = string.Join(",", entry.Properties.Where(property => property.Metadata.IsPrimaryKey())
            .Select(property => Format(property.CurrentValue ?? property.OriginalValue)));
        var properties = entry.Properties.Where(property => !AuditTrail.OmittedProperties.Contains(property.Metadata.Name)).ToList();
        switch (entry.State)
        {
            case EntityState.Added:
                return new(entry.Metadata.ClrType.Name, key, "INSERT", null,
                    properties.ToDictionary(property => property.Metadata.Name, property => Format(property.CurrentValue)));
            case EntityState.Deleted:
                return new(entry.Metadata.ClrType.Name, key, "DELETE",
                    properties.ToDictionary(property => property.Metadata.Name, property => Format(property.OriginalValue)), null);
            default:
                var modified = properties
                    .Where(property => property.IsModified && !Equals(Format(property.OriginalValue), Format(property.CurrentValue)))
                    .ToList();
                // Solo cambiaron campos omitidos (p. ej. páginas o notas): se deja constancia sin valores.
                if (modified.Count == 0)
                    return entry.Properties.Any(property => property.IsModified)
                        ? new(entry.Metadata.ClrType.Name, key, "UPDATE", null, null)
                        : null;
                return new(entry.Metadata.ClrType.Name, key, "UPDATE",
                    modified.ToDictionary(property => property.Metadata.Name, property => Format(property.OriginalValue)),
                    modified.ToDictionary(property => property.Metadata.Name, property => Format(property.CurrentValue)));
        }
    }

    private static string? Format(object? value) => value switch
    {
        null => null,
        string text => text.Length > 300 ? text[..300] + "…" : text,
        string[] items => string.Join(", ", items),
        DateTimeOffset date => date.ToString("O"),
        byte[] bytes => $"({bytes.Length} bytes)",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
    };
}
