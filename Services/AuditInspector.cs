using DocuSearch.Models;

namespace DocuSearch.Services;

public sealed record AuditRiskAlert(string Severity, string Title, string Detail);

public sealed record AuditActionCount(string Action, int Count);

public sealed record AuditRiskReport(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int TotalEvents,
    int FailedLogins,
    int AccessDenied,
    int IntegrityMismatches,
    int AdminChanges,
    int Downloads,
    int Deletions,
    IReadOnlyList<AuditRiskAlert> Alerts,
    IReadOnlyList<AuditActionCount> TopActions);

/// <summary>
/// AG-05 Auditor: resumen determinista de riesgos sobre la bitácora. No usa LLM,
/// no expone contenido documental y no escribe ni modifica eventos.
/// </summary>
public static class AuditInspector
{
    public const int FailedLoginThreshold = 5;
    public const int AccessDeniedThreshold = 3;

    private static readonly HashSet<string> AdminActions = new(StringComparer.Ordinal)
    {
        "UPDATE_USER", "DELETE_USER", "UPDATE_DEPARTMENT"
    };

    public static AuditRiskReport Analyze(
        IEnumerable<AuditEvent> events,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        var rows = events
            .Where(entry => entry.TimestampUTC >= fromUtc && entry.TimestampUTC <= toUtc)
            .ToList();

        var failedLogins = rows.Count(entry => entry.Action == "LOGIN_FAILED");
        var accessDenied = rows.Count(entry => entry.Action == "ACCESS_DENIED");
        var deniedEvents = rows.Count(entry => entry.Status == "DENEGADO");
        var integrity = rows.Count(entry => entry.Action == "INTEGRITY_MISMATCH");
        var adminChanges = rows.Count(entry => AdminActions.Contains(entry.Action));
        var downloads = rows.Count(entry => entry.Action == "DOWNLOAD");
        var deletions = rows.Count(entry => entry.Action == "DELETE");

        var alerts = new List<AuditRiskAlert>();
        if (integrity > 0)
            alerts.Add(new("error", "Integridad alterada",
                $"{integrity} evento(s) INTEGRITY_MISMATCH en el periodo. Revisa los PDF y la entrega."));
        if (failedLogins >= FailedLoginThreshold)
            alerts.Add(new("warning", "Intentos de inicio de sesión fallidos",
                $"{failedLogins} intentos fallidos. Verifica bloqueo por IP y revisa los correos implicados."));
        if (accessDenied >= AccessDeniedThreshold)
            alerts.Add(new("warning", "Accesos denegados",
                $"{accessDenied} denegaciones en el periodo. Confirma que corresponden a pruebas o a un perfil mal configurado."));
        if (adminChanges > 0)
            alerts.Add(new("info", "Cambios administrativos",
                $"{adminChanges} cambio(s) de usuarios o departamentos. Revisa la segregación de funciones."));
        if (rows.Any(entry => entry.Status == "DENEGADO") && deniedEvents > accessDenied)
            alerts.Add(new("warning", "Eventos denegados",
                "Hay eventos con estado DENEGADO que no son ACCESS_DENIED; revisa la bitácora filtrada."));

        var topActions = rows
            .GroupBy(entry => entry.Action, StringComparer.Ordinal)
            .Select(group => new AuditActionCount(group.Key, group.Count()))
            .OrderByDescending(action => action.Count)
            .ThenBy(action => action.Action, StringComparer.Ordinal)
            .Take(5)
            .ToList();

        return new AuditRiskReport(fromUtc, toUtc, rows.Count, failedLogins, accessDenied, integrity,
            adminChanges, downloads, deletions, alerts, topActions);
    }
}
