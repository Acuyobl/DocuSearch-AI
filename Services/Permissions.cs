using System.Security.Claims;
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
    // La sesión nace vacía: solo la establece el inicio de sesión autenticado a partir
    // de los claims de la cookie. La suite de comprobaciones crea su propia sesión con
    // SignIn(...) usando un ClaimsPrincipal, de modo que el comportamiento no depende
    // de la configuración Debug/Release.
    public string? UserId { get; internal set; }
    public string DisplayName { get; private set; } = "";
    public string Role { get; private set; } = "consultor";
    public string? LastDocumentCode { get; set; }
    public bool IsAuthenticated => !string.IsNullOrEmpty(UserId);

    public void SignIn(ClaimsPrincipal principal)
    {
        UserId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("La identidad autenticada no contiene identificador de usuario.");
        DisplayName = principal.FindFirstValue(ClaimTypes.Name) ?? "";
        Role = principal.FindFirstValue(ClaimTypes.Role) ?? "consultor";
        LastDocumentCode = null;
    }

    public void SignOut()
    {
        UserId = null;
        DisplayName = "";
        Role = "consultor";
        LastDocumentCode = null;
    }
}
