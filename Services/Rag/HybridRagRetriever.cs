using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services.Rag;

/// <summary>
/// Retrieval híbrido: candidatos vectoriales reordenados con BM25 sobre ese mismo
/// conjunto. El reranker recibe un pool mayor que TopK y el corte final se hace al
/// terminar, para que pueda rescatar candidatos que la fusión dejó fuera.
/// </summary>
public sealed class HybridRagRetriever(
    IRagVectorStore vectorStore,
    IOptions<RagOptions> options,
    IRagReranker reranker) : IRagRetriever
{
    private readonly RagOptions settings = options.Value;

    public async Task<IReadOnlyList<RagHit>> RetrieveAsync(string query, float[] queryVector,
        IReadOnlyCollection<string> documentIds, CancellationToken cancellationToken = default)
    {
        var topK = Math.Clamp(settings.TopK, 1, 50);
        var candidates = await vectorStore.QueryAsync(queryVector, documentIds,
            Math.Clamp(settings.CandidateK, 5, 100), cancellationToken);
        if (candidates.Count == 0) return [];
        var lexical = Bm25Scores(Tokens(query).Distinct().ToList(),
            candidates.Select(candidate => candidate.Chunk.Text).ToList());
        var maxLexical = lexical.Max();
        var hits = candidates.Select((candidate, index) =>
        {
            var lexicalScore = maxLexical <= 0 ? 0 : lexical[index] / maxLexical;
            var score = settings.VectorWeight * candidate.Score + settings.LexicalWeight * lexicalScore;
            return new RagHit(candidate.Chunk, score);
        }).OrderByDescending(hit => hit.Score).ToList();
        if (settings.Rerank && hits.Count > 1)
        {
            var pool = RerankPoolSize(settings.RerankCandidates, topK);
            hits = (await reranker.RerankAsync(query, hits.Take(pool).ToList(), cancellationToken)).ToList();
        }
        return hits.Take(topK).ToList();
    }

    /// <summary>El pool de reranking nunca es menor que TopK ni mayor que el máximo del reranker.</summary>
    public static int RerankPoolSize(int rerankCandidates, int topK) =>
        Math.Clamp(Math.Max(rerankCandidates, topK), 2, CrossEncoderRagReranker.MaxCandidates);

    public static IReadOnlyList<string> Tokens(string text) =>
        Regex.Matches(TextAnalysis.Normalize(text), @"[\p{L}\d]{2,}").Select(match => match.Value).ToList();

    /// <summary>
    /// BM25 (k1=1.2, b=0.75) con IDF calculado sobre los textos recibidos:
    /// idf = ln(1 + (N − df + 0.5) / (df + 0.5)), donde df es el número de textos que
    /// contienen el término. Un término raro pesa más que uno presente en todos.
    /// </summary>
    public static IReadOnlyList<double> Bm25Scores(IReadOnlyList<string> queryTerms, IReadOnlyList<string> texts)
    {
        var scores = new double[texts.Count];
        if (queryTerms.Count == 0 || texts.Count == 0) return scores;
        const double k1 = 1.2, b = 0.75;
        var documents = texts.Select(text => Tokens(text)
            .GroupBy(token => token, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal)).ToList();
        var lengths = documents.Select(frequencies => frequencies.Values.Sum()).ToList();
        var averageLength = Math.Max(1, lengths.Average());
        foreach (var term in queryTerms.Distinct(StringComparer.Ordinal))
        {
            var df = documents.Count(frequencies => frequencies.ContainsKey(term));
            if (df == 0) continue;
            var idf = Math.Log(1 + (texts.Count - df + 0.5) / (df + 0.5));
            for (var index = 0; index < documents.Count; index++)
            {
                if (!documents[index].TryGetValue(term, out var frequency)) continue;
                scores[index] += idf * frequency * (k1 + 1) /
                    (frequency + k1 * (1 - b + b * lengths[index] / averageLength));
            }
        }
        return scores;
    }
}

/// <summary>Reranker semántico basado en Pinecone/e5 para reordenar los candidatos.</summary>
public sealed class SemanticRagReranker(IRagEmbedder embedder) : IRagReranker
{
    public async Task<IReadOnlyList<RagHit>> RerankAsync(string query, IReadOnlyList<RagHit> candidates,
        CancellationToken cancellationToken = default)
    {
        if (!embedder.Enabled || candidates.Count <= 1) return candidates;
        var queryVector = await embedder.EmbedQueryAsync(query, cancellationToken);
        var scores = new List<RagHit>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Combinación efímera: no se guarda en caché para no desplazar los embeddings reutilizables.
            var vector = await embedder.EmbedUncachedAsync(query + "\n" + candidate.Chunk.Text, cancellationToken);
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
