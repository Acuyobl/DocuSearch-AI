using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuSearch.Services.Rag;

/// <summary>
/// Segundo VectorStore: mantiene el texto canónico en SQL Server y los vectores
/// de fragmento en Pinecone. La consulta trae vecinos de Pinecone y recupera el
/// texto/metadatos desde SQL para citar sin enviar contenido al servicio externo.
/// </summary>
public sealed class PineconeRagVectorStore(
    PineconeClient client,
    SqlRagVectorStore sqlStore,
    IDbContextFactory<AppDbContext> factory) : IRagVectorStore
{
    public string Name => "pinecone";

    public async Task UpsertAsync(IReadOnlyList<RagChunkVector> chunks, CancellationToken cancellationToken = default)
    {
        await sqlStore.UpsertAsync(chunks, cancellationToken);
        var vectors = chunks.Select(item => new PineconeVector(
            $"rag:{item.Chunk.Id}",
            item.Vector,
            new Dictionary<string, object>
            {
                ["kind"] = "ragchunk",
                ["chunkId"] = item.Chunk.Id,
                ["documentId"] = item.Chunk.DocumentId,
                ["code"] = item.Chunk.InternalCode,
                ["page"] = item.Chunk.PageNumber,
                ["chunkIndex"] = item.Chunk.ChunkIndex
            })).ToList();
        await client.UpsertAsync(vectors, cancellationToken);
    }

    public async Task<IReadOnlyList<RagScoredChunk>> QueryAsync(float[] vector, IReadOnlyCollection<string> documentIds,
        int topK, CancellationToken cancellationToken = default)
    {
        var matches = await client.QueryChunksAsync(vector, documentIds, topK, cancellationToken);
        if (matches.Count == 0) return [];
        var ids = matches.Select(match => match.ChunkId).ToHashSet(StringComparer.Ordinal);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var records = await db.RagChunks.AsNoTracking()
            .Where(record => ids.Contains(record.Id))
            .ToListAsync(cancellationToken);
        var byId = records.ToDictionary(record => record.Id, StringComparer.Ordinal);
        return matches
            .Where(match => byId.ContainsKey(match.ChunkId))
            .Select(match => new RagScoredChunk(ToChunk(byId[match.ChunkId]), match.Score))
            .OrderByDescending(item => item.Score)
            .Take(Math.Clamp(topK, 1, 200))
            .ToList();
    }

    public Task DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default) =>
        sqlStore.DeleteDocumentAsync(documentId, cancellationToken);

    /// <summary>Sincroniza los fragmentos ya existentes en SQL sin recalcular embeddings.</summary>
    public async Task<int> SyncExistingAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var records = await db.RagChunks.AsNoTracking().ToListAsync(cancellationToken);
        var vectors = records.Select(record => new PineconeVector(
            $"rag:{record.Id}",
            SqlRagVectorStore.FromBytes(record.Vector),
            new Dictionary<string, object>
            {
                ["kind"] = "ragchunk",
                ["chunkId"] = record.Id,
                ["documentId"] = record.DocumentId,
                ["code"] = record.InternalCode,
                ["page"] = record.PageNumber,
                ["chunkIndex"] = record.ChunkIndex
            })).ToList();
        return await client.UpsertAsync(vectors, cancellationToken);
    }

    private static RagChunk ToChunk(RagChunkRecord record) => new(record.Id, record.DocumentId,
        record.InternalCode, record.SourceType, record.PageNumber, record.ChunkIndex,
        record.StartOffset, record.EndOffset, record.Text, record.Hash);
}
