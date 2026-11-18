using DocuSearch.Models;

namespace DocuSearch.Services;

/// <summary>Fragmentos de una página agregados por modelo y dimensiones (sin texto ni vectores).</summary>
public sealed record RagPageStat(string DocumentId, int PageNumber, string Model, int Dimensions, int Chunks,
    DateTimeOffset LastIndexedUtc);

public sealed record RagIndexReport(
    DateTimeOffset GeneratedUtc,
    string ExpectedModel,
    int ExpectedDimensions,
    int TotalDocuments,
    int IndexableDocuments,
    int IndexedDocuments,
    int TotalChunks,
    int TextPages,
    int CoveredPages,
    IReadOnlyList<InspectionFinding> Findings,
    IReadOnlyList<string> ReindexDocumentIds,
    string Recommendation)
{
    public double PageCoverage => TextPages == 0 ? 1 : (double)CoveredPages / TextPages;
}

/// <summary>Comparación entre los fragmentos de SQL y los vectores «rag:» de Pinecone.</summary>
public sealed record RagPineconeDrift(bool Applicable, string Message, int Expected = 0, int Present = 0,
    int Missing = 0, int Orphans = 0);

/// <summary>
/// AG-10 Inspector · salud del índice RAG: comprueba que cada expediente con texto esté
/// indexado por fragmentos con el modelo actual y que el índice no haya quedado desfasado.
/// Determinista, sin LLM ni red, y sin leer texto de fragmentos ni vectores.
/// </summary>
public static class RagIndexInspector
{
    public const int MaxListedPages = 10;

    /// <param name="lastTextChangeByCode">Última carga o reproceso correcto por código de expediente.</param>
    /// <param name="documentsWithPageIndex">Expedientes con embeddings por página; null omite esa comprobación.</param>
    public static RagIndexReport Analyze(
        IReadOnlyList<DocumentItem> documents,
        IReadOnlyList<RagPageStat> stats,
        IReadOnlyDictionary<string, DateTimeOffset>? lastTextChangeByCode,
        IReadOnlySet<string>? documentsWithPageIndex,
        string expectedModel,
        int expectedDimensions,
        bool embeddingsEnabled = true,
        DateTimeOffset? now = null)
    {
        var byDocument = stats.GroupBy(stat => stat.DocumentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var findings = new List<InspectionFinding>();
        var reindex = new List<string>();
        int indexable = 0, indexed = 0, totalChunks = 0, textPagesTotal = 0, coveredTotal = 0;

        foreach (var document in documents)
        {
            var textPages = document.Pages
                .Where(page => !string.IsNullOrWhiteSpace(page.ExtractedText))
                .Select(page => page.PageNumber)
                .ToHashSet();
            var docStats = byDocument.GetValueOrDefault(document.Id) ?? [];
            var chunks = docStats.Sum(stat => stat.Chunks);
            totalChunks += chunks;
            var needsReindex = false;

            if (textPages.Count == 0)
            {
                if (chunks > 0)
                {
                    findings.Add(DocumentInspector.Finding(document, "rag-stale", "warning",
                        $"Tiene {chunks} fragmentos indexados, pero el expediente ya no tiene texto extraído."));
                    reindex.Add(document.Id);
                }
                continue;
            }

            indexable++;
            textPagesTotal += textPages.Count;
            if (chunks == 0)
            {
                findings.Add(DocumentInspector.Finding(document, "rag-missing", "error",
                    $"Tiene {textPages.Count} página(s) con texto y ningún fragmento indexado: el asistente RAG no lo encuentra."));
                reindex.Add(document.Id);
                needsReindex = true;
            }
            else
            {
                indexed++;
                var covered = docStats.Select(stat => stat.PageNumber).Where(textPages.Contains).ToHashSet();
                coveredTotal += covered.Count;
                var missing = textPages.Where(page => !covered.Contains(page)).Order().ToList();
                if (missing.Count > 0)
                {
                    findings.Add(DocumentInspector.Finding(document, "rag-page-gap", "warning",
                        $"{missing.Count} página(s) con texto sin fragmentos: {PageList(missing)}."));
                    needsReindex = true;
                }

                var incompatible = docStats
                    .Where(stat => stat.Dimensions != expectedDimensions ||
                        (stat.Model.Length > 0 && !string.Equals(stat.Model, expectedModel, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                var unknown = docStats.Where(stat => stat.Model.Length == 0 && stat.Dimensions == expectedDimensions).Sum(stat => stat.Chunks);
                if (incompatible.Count > 0)
                {
                    var found = string.Join(", ", incompatible
                        .Select(stat => $"{(stat.Model.Length == 0 ? "sin modelo" : stat.Model)}/{stat.Dimensions}")
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                    findings.Add(DocumentInspector.Finding(document, "rag-model", "error",
                        $"{incompatible.Sum(stat => stat.Chunks)} fragmento(s) generados con {found}; se esperaba {expectedModel}/{expectedDimensions}. " +
                        "La búsqueda los ignora."));
                    needsReindex = true;
                }
                else if (unknown > 0)
                {
                    findings.Add(DocumentInspector.Finding(document, "rag-model", "info",
                        $"{unknown} fragmento(s) sin modelo registrado (indexados con una versión anterior)."));
                    needsReindex = true;
                }

                var lastIndexed = docStats.Max(stat => stat.LastIndexedUtc);
                if (lastTextChangeByCode is not null &&
                    lastTextChangeByCode.TryGetValue(document.InternalCode, out var changed) &&
                    changed > lastIndexed)
                {
                    findings.Add(DocumentInspector.Finding(document, "rag-stale", "warning",
                        $"El texto cambió el {changed:yyyy-MM-dd HH:mm} UTC y el índice es del {lastIndexed:yyyy-MM-dd HH:mm} UTC: el reindexado falló."));
                    needsReindex = true;
                }
            }

            if (documentsWithPageIndex is not null && !documentsWithPageIndex.Contains(document.Id))
            {
                findings.Add(DocumentInspector.Finding(document, "page-index-missing", "warning",
                    "Sin embeddings por página: la búsqueda semántica y el respaldo del asistente no lo cubren."));
                needsReindex = true;
            }

            if (needsReindex && !reindex.Contains(document.Id)) reindex.Add(document.Id);
        }

        findings = findings
            .OrderBy(finding => SeverityRank(finding.Severity))
            .ThenBy(finding => finding.DocumentCode, StringComparer.Ordinal)
            .ThenBy(finding => finding.Kind, StringComparer.Ordinal)
            .ToList();

        return new RagIndexReport(
            now ?? DateTimeOffset.UtcNow, expectedModel, expectedDimensions, documents.Count, indexable, indexed,
            totalChunks, textPagesTotal, coveredTotal, findings, reindex,
            Recommend(documents.Count, indexable, reindex.Count, embeddingsEnabled));
    }

    private static string Recommend(int documents, int indexable, int pending, bool embeddingsEnabled)
    {
        if (!embeddingsEnabled)
            return "Los embeddings están desactivados (Embeddings:Enabled): no se puede indexar hasta activarlos.";
        if (documents == 0) return "No hay expedientes que revisar.";
        if (pending == 0)
            return indexable == 0
                ? "Ningún expediente tiene texto extraído todavía; no hay nada que indexar."
                : "Índice correcto: todos los expedientes con texto están indexados con el modelo actual.";
        if (pending > 5 && pending * 2 > documents)
            return $"{pending} expedientes requieren reindexado: ejecuta --rag-reindex y --reindex-embeddings para reconstruir todo el índice.";
        return $"Reindexa {pending} expediente(s) con hallazgos.";
    }

    private static string PageList(IReadOnlyList<int> pages) =>
        string.Join(", ", pages.Take(MaxListedPages)) + (pages.Count > MaxListedPages ? $" y {pages.Count - MaxListedPages} más" : "");

    private static int SeverityRank(string severity) => severity switch
    {
        "error" => 0,
        "warning" => 1,
        _ => 2
    };
}
