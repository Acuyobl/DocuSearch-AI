using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services.Rag;

/// <summary>Retrieval híbrido: vectorial + BM25 sobre los candidatos recuperados.</summary>
public sealed class HybridRagRetriever(
    IRagVectorStore vectorStore,
    IOptions<RagOptions> options,
    IRagReranker reranker) : IRagRetriever
{
    private readonly RagOptions settings = options.Value;

    public async Task<IReadOnlyList<RagHit>> RetrieveAsync(string query, float[] queryVector,
        IReadOnlyCollection<string> documentIds, CancellationToken cancellationToken = default)
    {
        var candidates = await vectorStore.QueryAsync(queryVector, documentIds,
            Math.Clamp(settings.CandidateK, 5, 100), cancellationToken);
        if (candidates.Count == 0) return [];
        var terms = Terms(query);
        var documents = candidates.Select(candidate => candidate.Chunk.Text).ToList();
        var averageLength = documents.Average(text => Math.Max(1, Terms(text).Length));
        var lexical = candidates.Select(candidate => Bm25(terms, candidate.Chunk.Text, averageLength)).ToList();
        var maxLexical = lexical.Count == 0 ? 0 : lexical.Max();
        var hits = candidates.Select((candidate, index) =>
        {
            var lexicalScore = maxLexical <= 0 ? 0 : lexical[index] / maxLexical;
            var score = settings.VectorWeight * candidate.Score + settings.LexicalWeight * lexicalScore;
            return new RagHit(candidate.Chunk, score);
        }).OrderByDescending(hit => hit.Score).Take(Math.Clamp(settings.TopK, 1, 50)).ToList();
        if (settings.Rerank && hits.Count > 1)
            hits = (await reranker.RerankAsync(query, hits, cancellationToken)).Take(Math.Clamp(settings.TopK, 1, 50)).ToList();
        return hits;
    }

    private static string[] Terms(string text) => Regex.Matches(TextAnalysis.Normalize(text), @"[\p{L}\d]{2,}")
        .Select(match => match.Value).Distinct().ToArray();

    private static double Bm25(IReadOnlyList<string> queryTerms, string text, double averageLength)
    {
        if (queryTerms.Count == 0) return 0;
        var tokens = Regex.Matches(TextAnalysis.Normalize(text), @"[\p{L}\d]{2,}").Select(match => match.Value).ToList();
        if (tokens.Count == 0) return 0;
        const double k1 = 1.2, b = 0.75;
        var frequencies = tokens.GroupBy(token => token).ToDictionary(group => group.Key, group => group.Count());
        var score = 0d;
        foreach (var term in queryTerms)
        {
            if (!frequencies.TryGetValue(term, out var frequency)) continue;
            score += Math.Log(1 + (1 / (frequency + 0.5))) +
                     frequency * (k1 + 1) / (frequency + k1 * (1 - b + b * tokens.Count / Math.Max(1, averageLength)));
        }
        return score;
    }
}

/// <summary>Reranker semántico basado en bge-m3 para reordenar los candidatos.</summary>
public sealed class SemanticRagReranker(IRagEmbedder embedder) : IRagReranker
{
    public async Task<IReadOnlyList<RagHit>> RerankAsync(string query, IReadOnlyList<RagHit> candidates,
        CancellationToken cancellationToken = default)
    {
        if (!embedder.Enabled || candidates.Count <= 1) return candidates;
        var queryVector = await embedder.EmbedAsync(query, cancellationToken);
        var scores = new List<RagHit>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vector = await embedder.EmbedAsync(query + "\n" + candidate.Chunk.Text, cancellationToken);
            scores.Add(new RagHit(candidate.Chunk, Cosine(queryVector, vector)));
        }
        return scores.OrderByDescending(hit => hit.Score).ToList();
    }

    private static double Cosine(float[] left, float[] right)
    {
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }
        return leftNorm == 0 || rightNorm == 0 ? 0 : dot / Math.Sqrt(leftNorm * rightNorm);
    }
}
