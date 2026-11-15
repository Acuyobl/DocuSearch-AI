using System.Text.Json;
using DocuSearch.Data;
using Microsoft.EntityFrameworkCore;

namespace DocuSearch.Services.Rag;

/// <summary>
/// Caso de evaluación. El acierto se mide por documento + página: el índice de
/// fragmento depende de la configuración de chunking y por eso es opcional.
/// </summary>
public sealed record RagEvalCase(string Id, string Question, string ExpectedDocumentId,
    string ExpectedCode, int ExpectedPage, int? ExpectedChunkIndex = null);

/// <param name="Cases">Casos evaluados (excluye los de documentos inexistentes).</param>
/// <param name="ContextRecall">Fracción de casos con la página esperada entre las fuentes.</param>
/// <param name="ContextPrecision">Fracción media de fuentes que son la página esperada.</param>
/// <param name="MeanReciprocalRank">1/posición de la primera fuente correcta, promediado.</param>
/// <param name="MissingCases">Casos cuyo documento esperado no existe en la base.</param>
/// <param name="LeakyCases">Preguntas que revelan la respuesta (código o texto literal).</param>
public sealed record RagEvalReport(int Cases, double ContextRecall, double ContextPrecision,
    double MeanReciprocalRank, double Faithfulness, double AnswerRelevancy,
    IReadOnlyList<string> FailedCases, IReadOnlyList<string> MissingCases, IReadOnlyList<string> LeakyCases);

public sealed record RagEvalExport(string Question, string Answer, string[] Contexts, string Expected);

/// <summary>
/// Evaluación de regresión del pipeline. Las métricas de retrieval son deterministas;
/// faithfulness y relevancy son proxies léxicos. Para métricas con LLM como juez se
/// usa el arnés Python eval/deepeval_eval.py sobre la exportación (--rag-eval-export).
/// </summary>
public sealed class RagEvaluationService(
    RagQueryService query,
    IDbContextFactory<AppDbContext> factory,
    ILogger<RagEvaluationService> logger)
{
    /// <summary>Palabras consecutivas compartidas con la referencia a partir de las cuales la pregunta la copia.</summary>
    public const int LeakRunLength = 6;

    public async Task<RagEvalReport> RunAsync(string questionsPath, bool generate,
        CancellationToken cancellationToken = default)
    {
        var cases = await ReadCasesAsync(questionsPath, cancellationToken);
        double recall = 0, precision = 0, reciprocalRank = 0, faithfulness = 0, relevancy = 0;
        var evaluated = 0;
        List<string> failed = [], missing = [], leaky = [];
        foreach (var item in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = await ReferenceAsync(item, cancellationToken);
            if (reference is null)
            {
                missing.Add(item.Id);
                continue;
            }
            if (LeakReason(item.Question, item.ExpectedCode, reference) is { } reason)
            {
                leaky.Add(item.Id);
                logger.LogWarning("RAG eval: la pregunta {Case} revela la respuesta ({Reason}).", item.Id, reason);
            }

            var response = await query.QueryAsync(item.Question, generate, cancellationToken);
            evaluated++;
            var rank = FirstMatchRank(response.Sources, item);
            recall += rank > 0 ? 1 : 0;
            reciprocalRank += rank > 0 ? 1d / rank : 0;
            precision += response.Sources.Count == 0 ? 0 :
                response.Sources.Count(source => Matches(source, item)) / (double)response.Sources.Count;
            var retrievedText = string.Join("\n", response.Sources.Select(source => source.Text));
            faithfulness += Ratio(TextAnalysis.Terms(response.Answer), TextAnalysis.Terms(retrievedText));
            relevancy += Ratio(TextAnalysis.Terms(item.Question), TextAnalysis.Terms(response.Answer));
            if (rank == 0) failed.Add(item.Id);
        }

        if (missing.Count > 0)
            logger.LogWarning("RAG eval: {Missing} casos sin documento en la base se excluyen: {Ids}.",
                missing.Count, string.Join(", ", missing));
        double Average(double total) => evaluated == 0 ? 0 : total / evaluated;
        var report = new RagEvalReport(evaluated, Average(recall), Average(precision), Average(reciprocalRank),
            Average(faithfulness), Average(relevancy), failed, missing, leaky);
        logger.LogInformation("RAG eval: {Cases} casos, recall={Recall:F3}, precision={Precision:F3}, mrr={Mrr:F3}, " +
            "faithfulness={Faithfulness:F3}, relevancy={Relevancy:F3}, fallos={Failures}, filtradas={Leaky}.",
            report.Cases, report.ContextRecall, report.ContextPrecision, report.MeanReciprocalRank,
            report.Faithfulness, report.AnswerRelevancy, failed.Count, leaky.Count);
        return report;
    }

    public async Task<int> ExportAsync(string questionsPath, string outputPath, bool generate,
        CancellationToken cancellationToken = default)
    {
        var cases = await ReadCasesAsync(questionsPath, cancellationToken);
        var exported = new List<RagEvalExport>();
        foreach (var item in cases)
        {
            var reference = await ReferenceAsync(item, cancellationToken);
            if (reference is null)
            {
                logger.LogWarning("RAG eval: {Case} omitido, su documento no existe.", item.Id);
                continue;
            }
            var response = await query.QueryAsync(item.Question, generate, cancellationToken);
            exported.Add(new RagEvalExport(item.Question, response.Answer,
                response.Sources.Select(source => source.Text).ToArray(), reference));
        }
        await File.WriteAllTextAsync(outputPath,
            JsonSerializer.Serialize(exported, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        return exported.Count;
    }

    /// <summary>
    /// Devuelve por qué una pregunta no mide recuperación real, o null si es válida:
    /// nombra el código del documento o copia ≥ <see cref="LeakRunLength"/> palabras seguidas de la referencia.
    /// </summary>
    public static string? LeakReason(string question, string expectedCode, string reference)
    {
        if (!string.IsNullOrWhiteSpace(expectedCode) &&
            question.Contains(expectedCode, StringComparison.OrdinalIgnoreCase))
            return "incluye el código del documento";
        var questionTokens = HybridRagRetriever.Tokens(question);
        var referenceTokens = HybridRagRetriever.Tokens(reference);
        if (questionTokens.Count < LeakRunLength || referenceTokens.Count < LeakRunLength) return null;
        var runs = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index + LeakRunLength <= referenceTokens.Count; index++)
            runs.Add(string.Join(' ', referenceTokens.Skip(index).Take(LeakRunLength)));
        for (var index = 0; index + LeakRunLength <= questionTokens.Count; index++)
            if (runs.Contains(string.Join(' ', questionTokens.Skip(index).Take(LeakRunLength))))
                return $"copia {LeakRunLength}+ palabras literales del texto esperado";
        return null;
    }

    /// <summary>Posición (1-based) de la primera fuente correcta, o 0 si no aparece.</summary>
    public static int FirstMatchRank(IReadOnlyList<RagCitation> sources, RagEvalCase item)
    {
        for (var index = 0; index < sources.Count; index++)
            if (Matches(sources[index], item)) return index + 1;
        return 0;
    }

    private static bool Matches(RagCitation source, RagEvalCase item) =>
        source.DocumentCode.Equals(item.ExpectedCode, StringComparison.OrdinalIgnoreCase) &&
        source.PageNumber == item.ExpectedPage &&
        (item.ExpectedChunkIndex is null || source.ChunkIndex == item.ExpectedChunkIndex);

    private static async Task<List<RagEvalCase>> ReadCasesAsync(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var cases = JsonSerializer.Deserialize<List<RagEvalCase>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Set de evaluación vacío o inválido.");
        if (cases.Count == 0) throw new InvalidDataException("El set de evaluación no tiene preguntas.");
        var duplicated = cases.GroupBy(item => item.Id).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        if (duplicated.Count > 0) throw new InvalidDataException($"IDs de caso repetidos: {string.Join(", ", duplicated)}.");
        return cases;
    }

    /// <summary>
    /// Texto de referencia: los fragmentos indexados de la página esperada (o del
    /// fragmento, si el caso lo fija). Null si el documento no existe; cadena vacía si
    /// existe pero aún no está indexado.
    /// </summary>
    private async Task<string?> ReferenceAsync(RagEvalCase item, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.Documents.AsNoTracking().AnyAsync(document => document.Id == item.ExpectedDocumentId, cancellationToken))
            return null;
        var chunks = db.RagChunks.AsNoTracking()
            .Where(chunk => chunk.DocumentId == item.ExpectedDocumentId && chunk.PageNumber == item.ExpectedPage);
        if (item.ExpectedChunkIndex is { } chunkIndex) chunks = chunks.Where(chunk => chunk.ChunkIndex == chunkIndex);
        var texts = await chunks.OrderBy(chunk => chunk.ChunkIndex).Select(chunk => chunk.Text).ToListAsync(cancellationToken);
        return string.Join("\n", texts);
    }

    private static double Ratio(IReadOnlyCollection<string> answer, IReadOnlyCollection<string> reference)
    {
        if (answer.Count == 0 || reference.Count == 0) return 0;
        var referenceSet = reference.ToHashSet(StringComparer.Ordinal);
        return answer.Count(referenceSet.Contains) / (double)answer.Count;
    }
}
