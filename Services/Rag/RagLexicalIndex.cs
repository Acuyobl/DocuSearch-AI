using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuSearch.Services.Rag;

/// <summary>
/// Índice BM25 en memoria sobre <b>todos</b> los fragmentos (singleton por proceso). Aporta a la búsqueda
/// híbrida los candidatos que coinciden literalmente (códigos, cifras, nombres propios) aunque el vector
/// no los sitúe entre los primeros, y un IDF calculado sobre el corpus completo.
/// </summary>
/// <remarks>Se recarga con la misma huella que <see cref="RagVectorCache"/> (filas y último CreatedUtc).</remarks>
public sealed class RagLexicalIndex(ILogger<RagLexicalIndex>? logger = null)
{
    private const double K1 = 1.2, B = 0.75;

    private sealed record Entry(RagChunk Chunk, Dictionary<string, int> Frequencies, int Length);

    private sealed record State(long Count, DateTimeOffset? LastWrite, Dictionary<string, Entry> ById,
        ILookup<string, Entry> ByDocument, Dictionary<string, int> DocumentFrequency, double AverageLength);

    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile State? state;

    public int Reloads { get; private set; }

    /// <summary>Los <paramref name="topK"/> fragmentos de <paramref name="documentIds"/> con mayor BM25 (&gt; 0).</summary>
    public async Task<IReadOnlyList<RagScoredChunk>> SearchAsync(AppDbContext db, string query,
        IReadOnlyCollection<string> documentIds, int topK, CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(db, cancellationToken);
        var terms = QueryTerms(query);
        if (terms.Count == 0) return [];
        return documentIds.Distinct(StringComparer.Ordinal)
            .SelectMany(id => current.ByDocument[id])
            .Select(entry => new RagScoredChunk(entry.Chunk, Score(current, terms, entry)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .Take(Math.Clamp(topK, 1, 200))
            .ToList();
    }

    /// <summary>BM25 de fragmentos concretos (p. ej. los candidatos vectoriales) con el IDF del corpus.</summary>
    public async Task<IReadOnlyDictionary<string, double>> ScoreAsync(AppDbContext db, string query,
        IEnumerable<string> chunkIds, CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(db, cancellationToken);
        var terms = QueryTerms(query);
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var id in chunkIds)
            result[id] = terms.Count > 0 && current.ById.TryGetValue(id, out var entry) ? Score(current, terms, entry) : 0;
        return result;
    }

    private static List<string> QueryTerms(string query) =>
        HybridRagRetriever.Tokens(query).Distinct(StringComparer.Ordinal).ToList();

    private static double Score(State current, IReadOnlyList<string> terms, Entry entry)
    {
        var total = current.ById.Count;
        double score = 0;
        foreach (var term in terms)
        {
            if (!entry.Frequencies.TryGetValue(term, out var frequency)) continue;
            var df = current.DocumentFrequency.GetValueOrDefault(term);
            var idf = Math.Log(1 + (total - df + 0.5) / (df + 0.5));
            score += idf * frequency * (K1 + 1) /
                (frequency + K1 * (1 - B + B * entry.Length / current.AverageLength));
        }
        return score;
    }

    private async Task<State> GetAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var (count, lastWrite) = await RagVectorCache.FingerprintAsync(db, cancellationToken);
        if (state is { } current && current.Count == count && current.LastWrite == lastWrite) return current;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (state is { } fresh && fresh.Count == count && fresh.LastWrite == lastWrite) return fresh;
            var records = await db.RagChunks.AsNoTracking()
                .Select(record => new RagChunkRecord
                {
                    Id = record.Id, DocumentId = record.DocumentId, InternalCode = record.InternalCode,
                    SourceType = record.SourceType, PageNumber = record.PageNumber, ChunkIndex = record.ChunkIndex,
                    StartOffset = record.StartOffset, EndOffset = record.EndOffset, Text = record.Text, Hash = record.Hash
                })
                .ToListAsync(cancellationToken);
            var entries = records.Select(record =>
            {
                var tokens = HybridRagRetriever.Tokens(record.Text);
                var frequencies = tokens.GroupBy(token => token, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                return new Entry(SqlRagVectorStore.ToChunk(record), frequencies, tokens.Count);
            }).ToList();
            var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in entries)
                foreach (var term in entry.Frequencies.Keys)
                    documentFrequency[term] = documentFrequency.GetValueOrDefault(term) + 1;
            var built = new State(count, lastWrite,
                entries.ToDictionary(entry => entry.Chunk.Id, StringComparer.Ordinal),
                entries.ToLookup(entry => entry.Chunk.DocumentId, StringComparer.Ordinal),
                documentFrequency,
                Math.Max(1, entries.Count == 0 ? 1 : entries.Average(entry => entry.Length)));
            state = built;
            Reloads++;
            logger?.LogInformation("RAG: índice léxico recargado con {Count} fragmentos.", entries.Count);
            return built;
        }
        finally
        {
            gate.Release();
        }
    }
}
