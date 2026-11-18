using System.ComponentModel.DataAnnotations;

namespace DocuSearch.Models;

/// <summary>Estados de una solicitud de aprobación.</summary>
public static class ApprovalStatus
{
    public const string Pending = "pendiente";
    public const string Approved = "aprobada";
    public const string Rejected = "rechazada";
    public const string Expired = "vencida";
    /// <summary>Se aprobó, pero la acción falló al aplicarse (p. ej. el requisito ya no se cumple).</summary>
    public const string Failed = "fallida";
}

/// <summary>Acciones sensibles que pueden exigir doble control.</summary>
public static class SensitiveAction
{
    public const string DeleteDocument = "DELETE_DOCUMENT";
    public const string DowngradeConfidentiality = "DOWNGRADE_CONFIDENTIALITY";
    public const string ChangeUserRole = "USER_ROLE_CHANGE";
    public const string DeleteUser = "DELETE_USER";
    public const string Reset = "RESET";
    public const string PurgeTraces = "PURGE_TRACES";

    public static readonly string[] All = [DeleteDocument, DowngradeConfidentiality, ChangeUserRole, DeleteUser, Reset, PurgeTraces];

    public static string Label(string action) => action switch
    {
        DeleteDocument => "Eliminar expediente",
        DowngradeConfidentiality => "Bajar confidencialidad",
        ChangeUserRole => "Cambiar rol de usuario",
        DeleteUser => "Eliminar usuario",
        Reset => "Restablecer datos de ejemplo",
        PurgeTraces => "Purgar trazas del agente",
        _ => action
    };
}

/// <summary>Solicitud de doble control: nada se aplica hasta que otra persona la aprueba.</summary>
public sealed class ApprovalRequest
{
    [Key, MaxLength(32)] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [Required, MaxLength(40)] public string Action { get; set; } = "";
    [MaxLength(40)] public string EntityType { get; set; } = "";
    [MaxLength(100)] public string EntityId { get; set; } = "";
    [MaxLength(300)] public string EntityLabel { get; set; } = "";
    /// <summary>Datos necesarios para aplicar la acción al aprobarla (JSON acotado).</summary>
    public string PayloadJson { get; set; } = "{}";
    [MaxLength(500)] public string Summary { get; set; } = "";
    [MaxLength(100)] public string RequestedById { get; set; } = "";
    [MaxLength(200)] public string RequestedByName { get; set; } = "";
    [MaxLength(40)] public string RequestedByRole { get; set; } = "";
    public DateTimeOffset RequestedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresUtc { get; set; }
    [MaxLength(20)] public string Status { get; set; } = ApprovalStatus.Pending;
    [MaxLength(100)] public string? DecidedById { get; set; }
    [MaxLength(200)] public string? DecidedByName { get; set; }
    public DateTimeOffset? DecidedUtc { get; set; }
    [MaxLength(1000)] public string? DecisionNote { get; set; }
}

/// <summary>Rutina programada y su estado.</summary>
public sealed class RoutineDefinition
{
    [Key, MaxLength(40)] public string Key { get; set; } = "";
    [MaxLength(120)] public string Name { get; set; } = "";
    [MaxLength(400)] public string Description { get; set; } = "";
    public int IntervalMinutes { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastRunUtc { get; set; }
    public DateTimeOffset? NextRunUtc { get; set; }
    [MaxLength(10)] public string? LastStatus { get; set; }
}

/// <summary>Historial de ejecuciones de rutinas (salida acotada).</summary>
public sealed class RoutineRun
{
    [Key, MaxLength(32)] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [Required, MaxLength(40)] public string RoutineKey { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public long DurationMs { get; set; }
    [MaxLength(10)] public string Status { get; set; } = "OK";
    [MaxLength(2000)] public string Output { get; set; } = "";
    [MaxLength(200)] public string Trigger { get; set; } = "scheduler";
}
