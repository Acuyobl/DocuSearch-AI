using DocuSearch.Models;

namespace DocuSearch.Services;

/// <param name="Module">Clave del módulo del menú donde opera el agente.</param>
public sealed record AgentInfo(string Code, string Name, string Module, string ModuleLabel, string Description, bool UsesLlm);

/// <summary>
/// Agentes ya en funcionamiento en la aplicación. Es la única fuente para la marca del menú
/// lateral y para el panel de agentes del Dashboard; si se añade un agente, se registra aquí.
/// </summary>
public static class AgentCatalog
{
    public static readonly IReadOnlyList<AgentInfo> Active =
    [
        new("AG-04", "Redactor", "search", "Buscador", "Responde preguntas sobre los expedientes con citas a documento y página.", true),
        new("AG-03", "Recuperador", "search", "Buscador", "Recuperación híbrida por fragmentos (vectores + BM25) con reranking.", false),
        new("AG-02", "Verificador", "search", "Buscador", "Valida cada cita antes de mostrar una respuesta generada.", false),
        new("AG-01", "Catalogador", "management", "Documentos", "Propone la ficha de metadatos de un expediente bajo petición.", true),
        new("AG-05", "Auditor", "audit", "Auditoría", "Resume riesgos de la bitácora: accesos, integridad y cambios.", false),
        new("AG-06", "Observador", "observability", "Observabilidad", "Mide la calidad y la latencia del asistente.", false),
        new("AG-10", "Inspector", "quality", "Calidad", "Revisa calidad documental, duplicados e índice RAG.", false)
    ];

    public static IReadOnlyList<AgentInfo> ForModule(string module) =>
        Active.Where(agent => agent.Module == module).ToList();

    /// <summary>Mismo criterio de acceso que el menú lateral.</summary>
    public static bool CanOpen(AppUser user, string module) => module switch
    {
        "audit" or "observability" or "quality" => Permissions.CanAudit(user),
        "rbac" => Permissions.CanAdmin(user),
        _ => true
    };
}
