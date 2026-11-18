using System.ComponentModel.DataAnnotations;

namespace DocuSearch.Models;

/// <summary>
/// Una fila por llamada real a un motor generativo. Importes en micro-dólares enteros
/// (1 USD = 1 000 000); periodo = mes calendario UTC «YYYY-MM».
/// </summary>
public sealed class AiUsageLedger
{
    [Key, MaxLength(32)] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    [Required, MaxLength(7)] public string Period { get; set; } = "";
    [Required, MaxLength(60)] public string Feature { get; set; } = "";
    [MaxLength(80)] public string Provider { get; set; } = "";
    [MaxLength(200)] public string Model { get; set; } = "";
    [MaxLength(80)] public string PromptTemplateKey { get; set; } = "";
    [MaxLength(16)] public string PromptVersion { get; set; } = "";
    /// <summary>Hash de la petición (no su contenido): permite detectar repeticiones.</summary>
    [MaxLength(16)] public string RequestHash { get; set; } = "";
    public long TokensIn { get; set; }
    public long TokensOut { get; set; }
    /// <summary>El proveedor no informó tokens y se estimaron por longitud del texto.</summary>
    public bool TokensEstimated { get; set; }
    public long CostMicroUsd { get; set; }
    public long LatencyMs { get; set; }
    /// <summary>ok | error</summary>
    [MaxLength(10)] public string Status { get; set; } = "ok";
    [MaxLength(40)] public string? ErrorCode { get; set; }
    [MaxLength(100)] public string? UserId { get; set; }
    [MaxLength(40)] public string? EntityType { get; set; }
    [MaxLength(100)] public string? EntityId { get; set; }
}

/// <summary>Política de presupuesto de IA de la instalación (una sola fila, Id «default»).</summary>
public sealed class AiBudgetPolicy
{
    public const string DefaultId = "default";
    [Key, MaxLength(40)] public string Id { get; set; } = DefaultId;
    /// <summary>0 = sin límite de gasto: rige la cuota mensual de tokens.</summary>
    public long MonthlyBudgetMicroUsd { get; set; }
    /// <summary>Porcentaje del presupuesto que genera un aviso (WARNING).</summary>
    public int AlertPercent { get; set; } = 80;
    /// <summary>Al alcanzar el presupuesto se bloquean las llamadas (HARD_STOP).</summary>
    public bool HardStop { get; set; }
    /// <summary>Cuota mensual de tokens («plan»); 0 = sin cuota.</summary>
    public long MonthlyTokenQuota { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(200)] public string UpdatedBy { get; set; } = "";
}

/// <summary>Incidente de presupuesto; uno por (periodo, tipo), actualizado de forma idempotente.</summary>
public sealed class AiBudgetIncident
{
    public const string Warning = "WARNING";
    public const string HardStop = "HARD_STOP";
    public const string QuotaExceeded = "QUOTA_EXCEEDED";

    [Key, MaxLength(32)] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [Required, MaxLength(7)] public string Period { get; set; } = "";
    [Required, MaxLength(20)] public string Kind { get; set; } = "";
    public DateTimeOffset FirstUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastUtc { get; set; } = DateTimeOffset.UtcNow;
    public int Occurrences { get; set; } = 1;
    public long SpentMicroUsd { get; set; }
    public long LimitMicroUsd { get; set; }
    [MaxLength(60)] public string? LastFeature { get; set; }
    [MaxLength(400)] public string Detail { get; set; } = "";
}
