using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocuSearch.Models;

public static class Catalog
{
    public static readonly string[] Roles = ["admin_general", "admin_depto", "gestor_documental", "consultor", "auditor"];
    public static readonly string[] Levels = ["Público", "Uso Interno", "Confidencial", "Estrictamente Secreto"];
    public static readonly string[] Categories = ["Contrato", "Política & Norma", "Informe Financiero", "Reglamento", "Dictamen", "Acta de Reunión", "Manual Técnico"];
    public static string RoleLabel(string role) => role switch
    {
        "admin_general" => "Administrador General",
        "admin_depto" => "Admin Departamento",
        "gestor_documental" => "Gestor Documental",
        "consultor" => "Usuario Consultor",
        "auditor" => "Auditor de Seguridad",
        _ => throw new ValidationException("Rol desconocido.")
    };
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string NewId() => Guid.NewGuid().ToString("N");
}

public sealed class DocumentItem
{
    [Required, Key, MaxLength(100)] public string Id { get; set; } = Catalog.NewId();
    [Required, MaxLength(100)] public string InternalCode { get; set; } = "";
    [Required, MaxLength(300)] public string Title { get; set; } = "";
    [Required, MaxLength(200)] public string Author { get; set; } = "";
    [Required, MaxLength(120)] public string Department { get; set; } = "";
    [Required, MaxLength(80)] public string Category { get; set; } = "Contrato";
    public DateTimeOffset UploadDate { get; set; } = DateTimeOffset.UtcNow;
    [Required, MaxLength(40)] public string Version { get; set; } = "v1.0";
    [Required, MaxLength(40)] public string Confidentiality { get; set; } = "Uso Interno";
    public string OcrStatus { get; set; } = "Pendiente";
    public string Sha256Hash { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public int PageCount { get; set; }
    [MaxLength(10000)] public string ExecutiveSummary { get; set; } = "";
    public string[] Keywords { get; set; } = [];
    public string[] Tags { get; set; } = [];
    [JsonIgnore] public string PagesJson { get; set; } = "[]";
    [JsonIgnore] public string NotesJson { get; set; } = "[]";
    public bool HasOriginal { get; set; }
    public int ViewsCount { get; set; }
    public int DownloadsCount { get; set; }
    [NotMapped] public List<DocumentPage> Pages
    {
        get => JsonSerializer.Deserialize<List<DocumentPage>>(PagesJson, Catalog.Json)
            ?? throw new InvalidDataException("Páginas almacenadas inválidas.");
        set => PagesJson = JsonSerializer.Serialize(value, Catalog.Json);
    }
    [NotMapped] public List<DocumentNote> Notes
    {
        get => JsonSerializer.Deserialize<List<DocumentNote>>(NotesJson, Catalog.Json)
            ?? throw new InvalidDataException("Notas almacenadas inválidas.");
        set => NotesJson = JsonSerializer.Serialize(value, Catalog.Json);
    }
}

public sealed record DocumentPage(int PageNumber, string ExtractedText, string Summary, List<DocumentEntity> Entities);
public sealed record DocumentEntity(string Id, string Text, string Type, int PageNumber);
public sealed record DocumentNote(string Id, string Author, string Role, DateTimeOffset CreatedAt, string Text, int? PageNumber);

public sealed class DocumentFile
{
    [Key] public string DocumentId { get; set; } = "";
    public byte[] Bytes { get; set; } = [];
}

public sealed class AppUser
{
    [Required, Key, MaxLength(100)] public string Id { get; set; } = Catalog.NewId();
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [Required, EmailAddress, MaxLength(254)] public string Email { get; set; } = "";
    [Required] public string Role { get; set; } = "consultor";
    [Required, MaxLength(120)] public string Department { get; set; } = "";
    public string Status { get; set; } = "Activo";
}

public sealed class DepartmentInfo
{
    [Required, Key, MaxLength(100)] public string Id { get; set; } = Catalog.NewId();
    [Required, MaxLength(120)] public string Name { get; set; } = "";
    [Required, MaxLength(200)] public string Lead { get; set; } = "";
    [Range(1, 1048576)] public int AllocatedStorageMB { get; set; } = 1024;
}

public sealed class AuditEvent
{
    [Key] public string Id { get; set; } = Catalog.NewId();
    public DateTimeOffset TimestampUTC { get; set; } = DateTimeOffset.UtcNow;
    public string UserName { get; set; } = "";
    public string UserRole { get; set; } = "";
    public string Action { get; set; } = "";
    public string DocumentCode { get; set; } = "SYSTEM";
    public string Details { get; set; } = "";
    public string Status { get; set; } = "EXITOSO";
}

public sealed record AppSnapshot(AppUser Actor, List<DocumentItem> Documents, List<AppUser> Users,
    List<DepartmentInfo> Departments, List<AuditEvent> Audit);
public sealed record SearchHit(DocumentItem Document, int PageNumber, string Snippet, int Score);
public sealed record AgentAnswer(string Text, List<SearchHit> Sources, string Provider = "Extractivo local",
    long? InputTokens = null, long? OutputTokens = null);
public sealed class SeedData
{
    public List<DocumentItem> Documents { get; set; } = [];
    public List<AppUser> Users { get; set; } = [];
    public List<DepartmentInfo> Departments { get; set; } = [];
}
