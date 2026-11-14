using System.Runtime.InteropServices;
using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuSearch.Services.Rag;

/// <summary>Implementación local del VectorStore sobre SQL Server (búsqueda exacta por coseno).</summary>
public sealed class SqlRagVectorStore(IDbContextFactory<AppDbContext> factory) : IRagVectorStore
{
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
            record.InternalCode = item.Chunk.InternalCode;
            record.SourceType = item.Chunk.SourceType;
            record.PageNumber = item.Chunk.PageNumber;
            record.ChunkIndex = item.Chunk.ChunkIndex;
            record.StartOffset = item.Chunk.StartOffset;
            record.EndOffset = item.Chunk.EndOffset;
            record.Text = item.Chunk.Text;
            record.Hash = item.Chunk.Hash;
            record.Model = "";
            record.Dimensions = item.Vector.Length;
            record.Vector = ToBytes(item.Vector);
            record.CreatedUtc = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RagScoredChunk>> QueryAsync(float[] vector, IReadOnlyCollection<string> documentIds,
        int topK, CancellationToken cancellationToken = default)
    {
        if (documentIds.Count == 0) return [];
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var records = await db.RagChunks.AsNoTracking()
            .Where(record => documentIds.Contains(record.DocumentId))
            .ToListAsync(cancellationToken);
        return records.Select(record => new RagScoredChunk(ToChunk(record), Cosine(vector, FromBytes(record.Vector))))
            .OrderByDescending(item => item.Score)
            .Take(Math.Clamp(topK, 1, 200))
            .ToList();
    }

    public async Task DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.RagChunks.Where(record => record.DocumentId == documentId).ExecuteDeleteAsync(cancellationToken);
    }

    private static RagChunk ToChunk(RagChunkRecord record) => new(record.Id, record.DocumentId,
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
