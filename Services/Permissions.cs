using DocuSearch.Models;

namespace DocuSearch.Services;

public static class Permissions
{
    public static bool CanWrite(AppUser user) => user.Role is "admin_general" or "admin_depto" or "gestor_documental";
    public static bool CanAdmin(AppUser user) => user.Role == "admin_general";
    public static bool CanAudit(AppUser user) => user.Role is "admin_general" or "auditor";
    public static string[] ReadableLevels(AppUser user) => user.Role switch
    {
        "admin_general" or "auditor" => Catalog.Levels,
        "admin_depto" or "gestor_documental" => Catalog.Levels[..3],
        "consultor" => Catalog.Levels[..2],
        _ => []
    };
    public static bool CanRead(AppUser user, DocumentItem document) =>
        ReadableLevels(user).Contains(document.Confidentiality) &&
        (user.Role != "admin_depto" || user.Department == document.Department);
    public static bool CanDownload(AppUser user, DocumentItem document) => CanRead(user, document) &&
        (document.Confidentiality != "Estrictamente Secreto" || CanAdmin(user)) &&
        (document.Confidentiality != "Confidencial" || CanWrite(user));
    public static bool CanAssign(AppUser user, string department, string level) =>
        CanWrite(user) && ReadableLevels(user).Contains(level) &&
        (user.Role != "admin_depto" || user.Department == department);
}

public sealed class LocalSession
{
    public string UserId { get; set; } = "usr-1";
    public string? LastDocumentCode { get; set; }
}
