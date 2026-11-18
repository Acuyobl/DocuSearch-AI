using System.Globalization;
using System.Text;
using System.Text.Json;
using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services.Rag;

/// <summary>Evento RAG_QUERY ya interpretado. <see cref="Query"/> es null si no se auditó el contenido.</summary>
public sealed record RagQueryEvent(
    DateTimeOffset TimestampUtc,
    string? Query,
    string QueryKey,
    string Engine,
    string Outcome,
    long LatencyMs,
    double? TopScore,
    IReadOnlyList<string> DocumentCodes);

public sealed record RagDailyCount(DateOnly Day, int Queries, int NoCoverage, int LowConfidence);
public sealed record RagEngineLatency(string Engine, int Queries, long P50Ms, long P95Ms);
public sealed record RagDocumentUsage(string DocumentCode, int Queries);
/// <param name="Question">Texto truncado; null cuando la bitácora no guarda contenido.</param>
public sealed record RagGapQuestion(string? Question, int Count, DateTimeOffset LastSeenUtc, string Reason);

public sealed record RagQualityReport(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int Queries,
    int Ignored,
    bool Truncated,
    IReadOnlyDictionary<string, int> Outcomes,
    int LowConfidence,
    IReadOnlyList<RagDailyCount> Daily,
    IReadOnlyList<RagEngineLatency> Latency,
    IReadOnlyList<RagDocumentUsage> TopDocuments,
    IReadOnlyList<string> NeverCited,
    int NeverCitedTotal,
    int GapQuestionsTotal,
    IReadOnlyList<RagGapQuestion> Gaps,
    bool QuestionsVisible)
{
    public int Count(string outcome) => Outcomes.TryGetValue(outcome, out var value) ? value : 0;
    public double Rate(string outcome) => Queries == 0 ? 0 : (double)Count(outcome) / Queries;
    public double LowConfidenceRate => Queries == 0 ? 0 : (double)LowConfidence / Queries;
}

/// <summary>
/// AG-06 Observador: métricas deterministas de calidad del asistente RAG a partir de los
/// eventos RAG_QUERY de la bitácora. Sin LLM, sin red y sin modificar datos.
/// </summary>
public static class RagObservatory
{
    /// <summary>Por debajo de esta puntuación de la mejor fuente la respuesta se considera de baja confianza.</summary>
    public const double LowConfidenceThreshold = 0.3;
    /// <summary>Resultado de eventos antiguos que no guardaban el campo outcome y no se pueden clasificar.</summary>
    public const string LegacyOutcome = "legacy";
    public const int MaxGapQuestions = 15;
    public const int MaxTopDocuments = 10;
    public const int MaxNeverCited = 20;
    public const int GapQuestionChars = 160;

    public static RagQualityReport Analyze(
        IReadOnlyList<AuditEvent> events,
        IReadOnlyCollection<string> readableCodes,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        bool showQuestions,
        bool truncated = false)
    {
        var readable = new HashSet<string>(readableCodes, StringComparer.OrdinalIgnoreCase);
        var parsed = new List<RagQueryEvent>();
        var ignored = 0;
        foreach (var item in events)
        {
            if (item.TimestampUTC < fromUtc || item.TimestampUTC > toUtc) continue;
            if (Parse(item) is { } value) parsed.Add(value);
            else ignored++;
        }

        var outcomes = parsed.GroupBy(item => item.Outcome)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var lowConfidence = parsed.Count(IsLowConfidence);

        var daily = parsed
            .GroupBy(item => DateOnly.FromDateTime(item.TimestampUtc.UtcDateTime))
            .OrderBy(group => group.Key)
            .Select(group => new RagDailyCount(group.Key, group.Count(),
                group.Count(item => item.Outcome == RagOutcome.NoCoverage), group.Count(IsLowConfidence)))
            .ToList();

        var latency = parsed
            .GroupBy(item => item.Engine, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var sorted = group.Select(item => item.LatencyMs).Order().ToList();
                return new RagEngineLatency(group.Key, sorted.Count, Percentile(sorted, .5), Percentile(sorted, .95));
            })
            .OrderByDescending(item => item.Queries)
            .ThenBy(item => item.Engine, StringComparer.Ordinal)
            .Take(8)
            .ToList();

        var citations = parsed
            .SelectMany(item => item.DocumentCodes.Distinct(StringComparer.OrdinalIgnoreCase))
            .Where(readable.Contains)
            .GroupBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var topDocuments = citations
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(MaxTopDocuments)
            .Select(pair => new RagDocumentUsage(pair.Key, pair.Value))
            .ToList();
        var neverCited = readable.Where(code => !citations.ContainsKey(code))
            .Order(StringComparer.Ordinal)
            .ToList();

        var gapGroups = parsed
            .Where(item => item.Outcome == RagOutcome.NoCoverage || IsLowConfidence(item))
            .GroupBy(item => item.QueryKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var latest = group.MaxBy(item => item.TimestampUtc)!;
                var text = showQuestions
                    ? group.OrderByDescending(item => item.TimestampUtc).Select(item => item.Query).FirstOrDefault(query => query is not null)
                    : null;
                return new RagGapQuestion(
                    text is null ? null : TextAnalysis.Truncate(text, GapQuestionChars),
                    group.Count(),
                    latest.TimestampUtc,
                    group.Any(item => item.Outcome == RagOutcome.NoCoverage) ? "sin cobertura" : "baja confianza");
            })
            .OrderByDescending(item => item.Count)
            .ThenByDescending(item => item.LastSeenUtc)
            .ToList();

        return new RagQualityReport(
            fromUtc, toUtc, parsed.Count, ignored, truncated, outcomes, lowConfidence, daily, latency,
            topDocuments, neverCited.Take(MaxNeverCited).ToList(), neverCited.Count,
            gapGroups.Count, gapGroups.Take(MaxGapQuestions).ToList(), showQuestions);
    }

    /// <summary>Respuesta con fuentes cuya mejor puntuación no alcanza el umbral.</summary>
    public static bool IsLowConfidence(RagQueryEvent item) =>
        item.Outcome != RagOutcome.NoCoverage && item.TopScore is { } score && score < LowConfidenceThreshold;

    /// <summary>Interpreta el JSON de un evento RAG_QUERY; devuelve null si está mal formado.</summary>
    public static RagQueryEvent? Parse(AuditEvent item)
    {
        if (!string.Equals(item.Action, "RAG_QUERY", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(item.Details))
            return null;
        try
        {
            using var json = JsonDocument.Parse(item.Details);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var query = ReadString(root, "query");
            var key = query is not null ? RagQueryService.QueryHash(query) : ReadString(root, "queryHash");
            if (string.IsNullOrWhiteSpace(key)) return null;
            var engine = ReadString(root, "engine") ?? "";

            var codes = new List<string>();
            double? top = null;
            if (root.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
            {
                foreach (var source in sources.EnumerateArray())
                {
                    if (source.ValueKind != JsonValueKind.Object) continue;
                    if (ReadString(source, "documentCode") is { Length: > 0 } code) codes.Add(code);
                    if (ReadNumber(source, "score") is { } score) top = top is null ? score : Math.Max(top.Value, score);
                }
            }
            top = ReadNumber(root, "topScore") ?? top;

            var outcome = ReadString(root, "outcome") ?? LegacyOutcomeFor(engine, codes.Count);
            var latency = ReadNumber(root, "latencyMs") is { } ms ? (long)Math.Max(0, ms) : 0;
            return new RagQueryEvent(item.TimestampUTC, query, key, engine.Length == 0 ? "(desconocido)" : engine,
                outcome, latency, top, codes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Normaliza una pregunta para agrupar repeticiones: minúsculas, sin tildes ni puntuación.</summary>
    public static string NormalizeQuestion(string question)
    {
        var decomposed = question.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character))
            {
                if (space && builder.Length > 0) builder.Append(' ');
                builder.Append(char.ToLowerInvariant(character));
                space = false;
            }
            else space = true;
        }
        return builder.ToString();
    }

    private static string LegacyOutcomeFor(string engine, int sources) =>
        string.Equals(engine, RagQueryService.NoCoverageEngine, StringComparison.Ordinal) || sources == 0
            ? RagOutcome.NoCoverage
            : engine.Contains("verificación AG-02", StringComparison.OrdinalIgnoreCase)
                ? RagOutcome.VerificationFailed
                : LegacyOutcome;

    private static long Percentile(List<long> sorted, double percentile) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1)];

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? ReadNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
        double.IsFinite(number) ? number : null;
}

/// <summary>Carga los eventos RAG_QUERY del periodo y arma el informe del Observador (solo auditoría).</summary>
public sealed class RagObservabilityService(IDbContextFactory<AppDbContext> factory, LocalSession session,
    IOptions<RagOptions> options)
{
    public const int MaxEvents = 5000;

    /// <summary>Indica si la configuración actual permite mostrar el texto de las preguntas.</summary>
    public bool AuditContent => options.Value.AuditContent;

    public async Task<RagQualityReport> LoadAsync(int days)
    {
        days = Math.Clamp(days, 1, 90);
        await using var db = await factory.CreateDbContextAsync();
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == session.UserId && user.Status == "Activo");
        if (actor is null || !Permissions.CanAudit(actor))
            throw new UnauthorizedAccessException("Solo administración general y auditoría pueden consultar la calidad del asistente.");

        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-days);
        var events = await db.Audit.AsNoTracking()
            .Where(item => item.Action == "RAG_QUERY" && item.TimestampUTC >= from)
            .OrderByDescending(item => item.TimestampUTC)
            .Take(MaxEvents + 1)
            .ToListAsync();
        var truncated = events.Count > MaxEvents;
        if (truncated) events.RemoveAt(events.Count - 1);

        var levels = Permissions.ReadableLevels(actor);
        var codes = await db.Documents.AsNoTracking()
            .Where(document => levels.Contains(document.Confidentiality) &&
                (actor.Role != "admin_depto" || document.Department == actor.Department))
            .Select(document => document.InternalCode)
            .ToListAsync();
        return RagObservatory.Analyze(events, codes, from, to, AuditContent, truncated);
    }
}
