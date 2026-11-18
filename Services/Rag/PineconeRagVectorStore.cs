using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services.Rag;

/// <summary>
/// Segundo VectorStore: mantiene el texto canónico en SQL Server y los vectores
/// de fragmento en Pinecone. La consulta trae vecinos de Pinecone y recupera el
/// texto/metadatos desde SQL para citar sin enviar contenido al servicio externo.
/// SQL es la fuente de verdad: si Pinecone falla, se registra y se corrige con
/// --rag-pinecone-sync.
/// </summary>
public sealed class PineconeRagVectorStore(
    PineconeClient client,
    SqlRagVectorStore sqlStore,
    IDbContextFactory<AppDbContext> factory,
    ILogger<PineconeRagVectorStore> logger,
    IOptions<EmbeddingOptions>? embeddingOptions = null) : IRagVectorStore
{
    public const string IdPrefix = "rag:";
    private readonly string expectedModel = embeddingOptions?.Value.Model ?? "";

    public string Name => "pinecone";

    public static string VectorId(string chunkId) => IdPrefix + chunkId;
    public static string DocumentPrefix(string documentId) => $"{IdPrefix}{documentId}:";

    public async Task UpsertAsync(IReadOnlyList<RagChunkVector> chunks, CancellationToken cancellationToken = default)
    {
        await sqlStore.UpsertAsync(chunks, cancellationToken);
        await client.UpsertAsync(chunks.Select(ToVector).ToList(), cancellationToken);
    }

    public async Task ReplaceDocumentAsync(string documentId, IReadOnlyList<RagChunkVector> chunks,
        CancellationToken cancellationToken = default)
    {
        await sqlStore.ReplaceDocumentAsync(documentId, chunks, cancellationToken);
        try
        {
            // Primero se escriben los vectores nuevos y después se borran solo los que sobran,
            // para que el documento no quede sin vectores entre ambas operaciones.
            await client.UpsertAsync(chunks.Select(ToVector).ToList(), cancellationToken);
            var current = chunks.Select(item => VectorId(item.Chunk.Id)).ToHashSet(StringComparer.Ordinal);
            var stale = (await client.ListIdsAsync(DocumentPrefix(documentId), cancellationToken))
                .Where(id => !current.Contains(id)).ToList();
            await client.DeleteIdsAsync(stale, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Pinecone: fragmentos de {DocumentId} actualizados en SQL pero no en Pinecone ({Error}). " +
                "Ejecuta --rag-pinecone-sync.", documentId, ex.GetType().Name);
        }
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
        var byId = records
            .Where(record => SqlRagVectorStore.IsCompatible(record, vector.Length, expectedModel))
            .ToDictionary(record => record.Id, StringComparer.Ordinal);
        SqlRagVectorStore.WarnIncompatible(logger, records.Count - byId.Count, vector.Length, expectedModel);
        return matches
            .Where(match => byId.ContainsKey(match.ChunkId))
            .Select(match => new RagScoredChunk(SqlRagVectorStore.ToChunk(byId[match.ChunkId]), match.Score))
            .OrderByDescending(item => item.Score)
            .Take(Math.Clamp(topK, 1, 200))
            .ToList();
    }

    public async Task DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await sqlStore.DeleteDocumentAsync(documentId, cancellationToken);
        try
        {
            await client.DeleteByPrefixAsync(DocumentPrefix(documentId), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Pinecone: no se pudieron borrar los vectores de {DocumentId} ({Error}). " +
                "Ejecuta --rag-pinecone-sync.", documentId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Sincroniza los fragmentos ya existentes en SQL sin recalcular embeddings y
    /// elimina de Pinecone los vectores «rag:» que ya no existen en SQL.
    /// </summary>
    public async Task<int> SyncExistingAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var records = await db.RagChunks.AsNoTracking().ToListAsync(cancellationToken);
        var vectors = records.Select(record => ToVector(new RagChunkVector(
            SqlRagVectorStore.ToChunk(record), SqlRagVectorStore.FromBytes(record.Vector), record.Model))).ToList();
        var upserted = await client.UpsertAsync(vectors, cancellationToken);
        var known = vectors.Select(vector => vector.Id).ToHashSet(StringComparer.Ordinal);
        var orphans = (await client.ListIdsAsync(IdPrefix, cancellationToken)).Where(id => !known.Contains(id)).ToList();
        await client.DeleteIdsAsync(orphans, cancellationToken);
        logger.LogInformation("Pinecone RAG: {Upserted} vectores sincronizados y {Orphans} huérfanos eliminados.",
            upserted, orphans.Count);
        return upserted;
    }

    private static PineconeVector ToVector(RagChunkVector item) => new(
        VectorId(item.Chunk.Id),
        item.Vector,
        new Dictionary<string, object>
        {
            ["kind"] = "ragchunk",
            ["chunkId"] = item.Chunk.Id,
            ["documentId"] = item.Chunk.DocumentId,
            ["code"] = item.Chunk.InternalCode,
            ["page"] = item.Chunk.PageNumber,
            ["chunkIndex"] = item.Chunk.ChunkIndex,
            ["model"] = item.Model
        });
}
