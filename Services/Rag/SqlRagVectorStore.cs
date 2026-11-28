using System.Runtime.InteropServices;
using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services.Rag;

/// <summary>Implementación local del VectorStore sobre SQL Server (búsqueda exacta por coseno).</summary>
public sealed class SqlRagVectorStore(
    IDbContextFactory<AppDbContext> factory,
    IOptions<EmbeddingOptions>? embeddingOptions = null,
    ILogger<SqlRagVectorStore>? logger = null,
    RagVectorCache? cache = null) : IRagVectorStore, IRagChunkScorer
{
    private readonly string expectedModel = embeddingOptions?.Value.Model ?? "";

    public string Name => "sql";

    public async Task UpsertAsync(IReadOnlyList<RagChunkVector> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0) return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var ids = chunks.Select(chunk => chunk.Chunk.Id).ToList();
        var existing = await db.RagChunks.Where(record => ids.Contains(record.Id)).ToDictionaryAsync(record => record.Id, cancellationToken);
        foreach (var item in chunks)
        {
            if (!existing.TryGetValue(item.Chunk.Id, out var record))
            {
                record = new RagChunkRecord { Id = item.Chunk.Id, DocumentId = item.Chunk.DocumentId };
                db.RagChunks.Add(record);
            }
            Apply(record, item);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReplaceDocumentAsync(string documentId, IReadOnlyList<RagChunkVector> chunks,
        CancellationToken cancellationToken = default)
    {
        if (chunks.Any(item => item.Chunk.DocumentId != documentId))
            throw new ArgumentException("Todos los fragmentos deben pertenecer al documento que se reemplaza.", nameof(chunks));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.RagChunks.Where(record => record.DocumentId == documentId).ExecuteDeleteAsync(cancellationToken);
        foreach (var item in chunks)
        {
            var record = new RagChunkRecord { Id = item.Chunk.Id, DocumentId = documentId };
            Apply(record, item);
            db.RagChunks.Add(record);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RagScoredChunk>> QueryAsync(float[] vector, IReadOnlyCollection<string> documentIds,
        int topK, CancellationToken cancellationToken = default)
    {
        if (documentIds.Count == 0) return [];
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (cache is not null) return await QueryCachedAsync(db, cache, vector, documentIds, topK, cancellationToken);
        var records = await db.RagChunks.AsNoTracking()
            .Where(record => documentIds.Contains(record.DocumentId))
            .ToListAsync(cancellationToken);
        var compatible = records.Where(record => IsCompatible(record, vector.Length, expectedModel)).ToList();
        WarnIncompatible(logger, records.Count - compatible.Count, vector.Length, expectedModel);
        return compatible.Select(record => new RagScoredChunk(ToChunk(record), Cosine(vector, FromBytes(record.Vector))))
            .OrderByDescending(item => item.Score)
            .Take(Math.Clamp(topK, 1, 200))
            .ToList();
    }

    /// <summary>
    /// Puntúa contra los vectores en memoria y lee de SQL solo el texto de los <paramref name="topK"/>
    /// ganadores. Mismo filtro de compatibilidad y misma similitud (coseno) que la ruta sin caché.
    /// </summary>
    private async Task<IReadOnlyList<RagScoredChunk>> QueryCachedAsync(AppDbContext db, RagVectorCache vectors,
        float[] vector, IReadOnlyCollection<string> documentIds, int topK, CancellationToken cancellationToken)
    {
        var byDocument = await vectors.GetAsync(db, cancellationToken);
        var query = RagVectorCache.Normalize(vector);
        var candidates = documentIds.Distinct(StringComparer.Ordinal).SelectMany(id => byDocument[id]).ToList();
        var compatible = candidates.Where(entry => IsCompatible(entry.Dimensions, entry.Model, vector.Length, expectedModel)).ToList();
        WarnIncompatible(logger, candidates.Count - compatible.Count, vector.Length, expectedModel);
        var winners = compatible
            .Select(entry => (entry.ChunkId, Score: RagVectorCache.Dot(query, entry.Unit)))
            .OrderByDescending(item => item.Score)
            .Take(Math.Clamp(topK, 1, 200))
            .ToList();
        if (winners.Count == 0) return [];
        var ids = winners.Select(item => item.ChunkId).ToList();
        // Sin la columna Vector: solo el texto y los metadatos de los ganadores.
        var records = await db.RagChunks.AsNoTracking()
            .Where(record => ids.Contains(record.Id))
            .Select(record => new RagChunkRecord
            {
                Id = record.Id, DocumentId = record.DocumentId, InternalCode = record.InternalCode,
                SourceType = record.SourceType, PageNumber = record.PageNumber, ChunkIndex = record.ChunkIndex,
                StartOffset = record.StartOffset, EndOffset = record.EndOffset, Text = record.Text, Hash = record.Hash
            })
            .ToDictionaryAsync(record => record.Id, StringComparer.Ordinal, cancellationToken);
        // Un fragmento borrado entre la huella y esta lectura simplemente no se devuelve.
        return winners
            .Where(item => records.ContainsKey(item.ChunkId))
            .Select(item => new RagScoredChunk(ToChunk(records[item.ChunkId]), item.Score))
            .ToList();
    }

    /// <summary>Coseno de la consulta con los fragmentos indicados; los incompatibles o inexistentes no aparecen.</summary>
    public async Task<IReadOnlyDictionary<string, double>> ScoreChunksAsync(float[] vector, IReadOnlyCollection<string> chunkIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (chunkIds.Count == 0) return result;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (cache is not null)
        {
            var byId = await cache.GetByIdAsync(db, cancellationToken);
            var query = RagVectorCache.Normalize(vector);
            foreach (var id in chunkIds)
                if (byId.TryGetValue(id, out var entry) && IsCompatible(entry.Dimensions, entry.Model, vector.Length, expectedModel))
                    result[id] = RagVectorCache.Dot(query, entry.Unit);
            return result;
        }
        var ids = chunkIds.ToList();
        var rows = await db.RagChunks.AsNoTracking().Where(record => ids.Contains(record.Id))
            .Select(record => new { record.Id, record.Model, record.Dimensions, record.Vector })
            .ToListAsync(cancellationToken);
        foreach (var row in rows.Where(row => IsCompatible(row.Dimensions, row.Model, vector.Length, expectedModel)))
            result[row.Id] = Cosine(vector, FromBytes(row.Vector));
        return result;
    }

    public async Task DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.RagChunks.Where(record => record.DocumentId == documentId).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Un fragmento es comparable con la consulta si tiene las mismas dimensiones y el
    /// mismo modelo. Los registros anteriores sin modelo («») se aceptan hasta reindexar.
    /// </summary>
    public static bool IsCompatible(RagChunkRecord record, int dimensions, string expectedModel) =>
        IsCompatible(record.Dimensions, record.Model, dimensions, expectedModel);

    internal static bool IsCompatible(int recordDimensions, string recordModel, int dimensions, string expectedModel) =>
        recordDimensions == dimensions &&
        (recordModel.Length == 0 || expectedModel.Length == 0 ||
         string.Equals(recordModel, expectedModel, StringComparison.OrdinalIgnoreCase));

    internal static void WarnIncompatible(ILogger? logger, int skipped, int dimensions, string expectedModel)
    {
        if (skipped > 0)
            logger?.LogWarning("RAG: {Skipped} fragmentos ignorados por modelo o dimensiones distintos " +
                "(se esperaba {Model}, {Dimensions} dims). Ejecuta --rag-reindex.", skipped, expectedModel, dimensions);
    }

    private static void Apply(RagChunkRecord record, RagChunkVector item)
    {
        record.InternalCode = item.Chunk.InternalCode;
        record.SourceType = item.Chunk.SourceType;
        record.PageNumber = item.Chunk.PageNumber;
        record.ChunkIndex = item.Chunk.ChunkIndex;
        record.StartOffset = item.Chunk.StartOffset;
        record.EndOffset = item.Chunk.EndOffset;
        record.Text = item.Chunk.Text;
        record.Hash = item.Chunk.Hash;
        record.Model = item.Model;
        record.Dimensions = item.Vector.Length;
        record.Vector = ToBytes(item.Vector);
        record.CreatedUtc = DateTimeOffset.UtcNow;
    }

    internal static RagChunk ToChunk(RagChunkRecord record) => new(record.Id, record.DocumentId,
        record.InternalCode, record.SourceType, record.PageNumber, record.ChunkIndex,
        record.StartOffset, record.EndOffset, record.Text, record.Hash);

    public static byte[] ToBytes(float[] vector) => MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    public static float[] FromBytes(byte[] bytes)
    {
        var copy = new byte[bytes.Length];
        Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);
        return MemoryMarshal.Cast<byte, float>(copy.AsSpan()).ToArray();
    }

    private static double Cosine(float[] left, float[] right)
    {
        if (left.Length == 0 || left.Length != right.Length) return 0;
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }
        return leftNorm == 0 || rightNorm == 0 ? 0 : dot / Math.Sqrt(leftNorm * rightNorm);
    }
}
