using System.Text.RegularExpressions;
using DocuSearch.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services.Rag;

/// <summary>
/// Retrieval híbrido. Con <see cref="RagLexicalIndex"/> une los mejores candidatos por vector y por BM25
/// sobre todo el corpus legible, y puntúa cada candidato con ambas señales (coseno y BM25 con IDF del
/// corpus): un fragmento que solo coincide literalmente (código, cifra, nombre) ya no se pierde.
/// Sin índice léxico, BM25 reordena solo los candidatos vectoriales (comportamiento anterior).
/// El reranker recibe un pool mayor que TopK y el corte final se hace al terminar.
/// </summary>
public sealed class HybridRagRetriever(
    IRagVectorStore vectorStore,
    IOptions<RagOptions> options,
    IRagReranker reranker,
    RagLexicalIndex? lexicalIndex = null,
    IDbContextFactory<AppDbContext>? factory = null) : IRagRetriever
{
    private readonly RagOptions settings = options.Value;

    public async Task<IReadOnlyList<RagHit>> RetrieveAsync(string query, float[] queryVector,
        IReadOnlyCollection<string> documentIds, CancellationToken cancellationToken = default)
    {
        var topK = Math.Clamp(settings.TopK, 1, 50);
        var candidateK = Math.Clamp(settings.CandidateK, 5, 100);
        var vectorHits = await vectorStore.QueryAsync(queryVector, documentIds, candidateK, cancellationToken);
        var pool = vectorHits.ToDictionary(hit => hit.Chunk.Id, hit => (hit.Chunk, Cosine: hit.Score), StringComparer.Ordinal);
        IReadOnlyDictionary<string, double> lexical;
        if (lexicalIndex is not null && factory is not null)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var lexicalHits = await lexicalIndex.SearchAsync(db, query, documentIds, candidateK, cancellationToken);
            var lexicalOnly = lexicalHits.Where(hit => !pool.ContainsKey(hit.Chunk.Id)).ToList();
            if (lexicalOnly.Count > 0)
            {
                var cosines = vectorStore is IRagChunkScorer scorer
                    ? await scorer.ScoreChunksAsync(queryVector, lexicalOnly.Select(hit => hit.Chunk.Id).ToList(), cancellationToken)
                    : new Dictionary<string, double>();
                foreach (var hit in lexicalOnly)
                    pool[hit.Chunk.Id] = (hit.Chunk, cosines.GetValueOrDefault(hit.Chunk.Id));
            }
            lexical = await lexicalIndex.ScoreAsync(db, query, pool.Keys.ToList(), cancellationToken);
        }
        else
        {
            var ids = pool.Keys.ToList();
            var scores = Bm25Scores(Tokens(query).Distinct().ToList(), ids.Select(id => pool[id].Chunk.Text).ToList());
            lexical = ids.Select((id, index) => (id, scores[index])).ToDictionary(item => item.id, item => item.Item2, StringComparer.Ordinal);
        }
        if (pool.Count == 0) return [];
        var maxLexical = lexical.Count == 0 ? 0 : lexical.Values.Max();
        var hits = pool.Values.Select(item =>
        {
            var lexicalScore = maxLexical <= 0 ? 0 : lexical.GetValueOrDefault(item.Chunk.Id) / maxLexical;
            var score = settings.VectorWeight * item.Cosine + settings.LexicalWeight * lexicalScore;
            return new RagHit(item.Chunk, score);
        }).OrderByDescending(hit => hit.Score).ToList();
        if (settings.Rerank && hits.Count > 1)
        {
            var size = RerankPoolSize(settings.RerankCandidates, topK);
            // El reranker puntúa en otra escala: solo se conserva el pool reordenado (nunca menor que TopK).
            hits = (await reranker.RerankAsync(query, hits.Take(size).ToList(), cancellationToken)).ToList();
        }
        // Descarta colas muy débiles frente al mejor resultado: el prompt debe recibir
        // fuentes relevantes, no relleno que confunda al modelo.
        if (hits.Count > 0)
        {
            var best = hits[0].Score;
            var floor = Math.Max(0.05, best * 0.30);
            hits = hits.Where(hit => hit.Score >= floor).ToList();
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
