using System.Text.Json;
using DocuSearch.Data;
using Microsoft.EntityFrameworkCore;

namespace DocuSearch.Services.Rag;

public sealed record RagEvalCase(string Id, string Question, string ExpectedDocumentId,
    string ExpectedCode, int ExpectedPage, int ExpectedChunkIndex);

public sealed record RagEvalReport(int Cases, double ContextRecall, double ContextPrecision,
    double Faithfulness, double AnswerRelevancy, IReadOnlyList<string> FailedCases);

public sealed record RagEvalExport(string Question, string Answer, string[] Contexts, string Expected);

/// <summary>
/// Evaluación de regresión del pipeline. Las métricas de retrieval son deterministas;
/// faithfulness y relevancy son proxies léxicos. Para RAGAS/DeepEval se usa el
/// arnés Python de eval/ragas_eval.py.
/// </summary>
public sealed class RagEvaluationService(
    RagQueryService query,
    IDbContextFactory<AppDbContext> factory,
    ILogger<RagEvaluationService> logger)
{
    public async Task<RagEvalReport> RunAsync(string questionsPath, bool generate,
        CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(questionsPath, cancellationToken);
        var cases = JsonSerializer.Deserialize<List<RagEvalCase>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Set de evaluación vacío o inválido.");
        if (cases.Count == 0) throw new InvalidDataException("El set de evaluación no tiene preguntas.");

        double recall = 0, precision = 0, faithfulness = 0, relevancy = 0;
        var failed = new List<string>();
        foreach (var item in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await query.QueryAsync(item.Question, generate, cancellationToken);
            var expected = response.Sources.Any(source =>
                source.DocumentCode.Equals(item.ExpectedCode, StringComparison.OrdinalIgnoreCase) &&
                source.PageNumber == item.ExpectedPage && source.ChunkIndex == item.ExpectedChunkIndex);
            recall += expected ? 1 : 0;
            precision += response.Sources.Count == 0 ? 0 :
                response.Sources.Count(source => source.DocumentCode.Equals(item.ExpectedCode, StringComparison.OrdinalIgnoreCase) &&
                    source.PageNumber == item.ExpectedPage) / (double)response.Sources.Count;
            var reference = await ReferenceAsync(item, cancellationToken);
            var retrievedText = string.Join("\n", response.Sources.Select(source => source.Text));
            faithfulness += Ratio(TextAnalysis.Terms(response.Answer), TextAnalysis.Terms(retrievedText));
            relevancy += Ratio(TextAnalysis.Terms(item.Question), TextAnalysis.Terms(response.Answer));
            if (!expected) failed.Add(item.Id);
        }

        var report = new RagEvalReport(cases.Count, recall / cases.Count, precision / cases.Count,
            faithfulness / cases.Count, relevancy / cases.Count, failed);
        logger.LogInformation("RAG eval: {Cases} casos, recall={Recall:F3}, precision={Precision:F3}, " +
            "faithfulness={Faithfulness:F3}, relevancy={Relevancy:F3}, fallos={Failures}.",
            report.Cases, report.ContextRecall, report.ContextPrecision, report.Faithfulness,
            report.AnswerRelevancy, report.FailedCases.Count);
        return report;
    }

    public async Task<int> ExportAsync(string questionsPath, string outputPath, bool generate,
        CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(questionsPath, cancellationToken);
        var cases = JsonSerializer.Deserialize<List<RagEvalCase>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Set de evaluación vacío o inválido.");
        var exported = new List<RagEvalExport>();
        foreach (var item in cases)
        {
            var response = await query.QueryAsync(item.Question, generate, cancellationToken);
            exported.Add(new RagEvalExport(item.Question, response.Answer,
                response.Sources.Select(source => source.Text).ToArray(),
                await ReferenceAsync(item, cancellationToken)));
        }
        await File.WriteAllTextAsync(outputPath,
            JsonSerializer.Serialize(exported, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        return exported.Count;
    }

    private async Task<string> ReferenceAsync(RagEvalCase item, CancellationToken cancellationToken)    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.RagChunks.AsNoTracking()
            .Where(chunk => chunk.DocumentId == item.ExpectedDocumentId && chunk.ChunkIndex == item.ExpectedChunkIndex)
            .Select(chunk => chunk.Text)
            .FirstOrDefaultAsync(cancellationToken) ?? "";
    }

    private static double Ratio(IReadOnlyCollection<string> answer, IReadOnlyCollection<string> reference)
    {
        if (answer.Count == 0 || reference.Count == 0) return 0;
        var referenceSet = reference.ToHashSet(StringComparer.Ordinal);
        return answer.Count(referenceSet.Contains) / (double)answer.Count;
    }
}
