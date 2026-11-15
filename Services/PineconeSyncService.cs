using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuSearch.Services;

/// <summary>
/// Sincroniza los vectores de página ya calculados en SQL Server hacia Pinecone.
/// No envía texto: solo id, vector y metadatos de autorización/cita.
/// </summary>
public sealed class PineconeSyncService(
    PineconeClient client,
    IDbContextFactory<AppDbContext> factory,
    ILogger<PineconeSyncService> logger)
{
    public bool Enabled => client.Enabled;

    /// <summary>Borra los vectores de página («{documentId}:{página}») de un expediente eliminado.</summary>
    public Task<int> DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default) =>
        Enabled ? client.DeleteByPrefixAsync($"{documentId}:", cancellationToken) : Task.FromResult(0);

    public async Task<int> SyncDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return 0;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null) return 0;
        var embeddings = await db.PageEmbeddings.AsNoTracking()
            .Where(embedding => embedding.DocumentId == documentId)
            .ToListAsync(cancellationToken);
        if (embeddings.Count == 0)
        {
            logger.LogInformation("Pinecone: {Code} no tiene embeddings locales; no se sincroniza.", document.InternalCode);
            return 0;
        }

        var vectors = embeddings.Select(embedding => new PineconeVector(
            $"{documentId}:{embedding.PageNumber}",
            EmbeddingIndexer.FromBytes(embedding.Vector),
            new Dictionary<string, object>
            {
                ["documentId"] = documentId,
                ["code"] = document.InternalCode,
                ["page"] = embedding.PageNumber,
                ["model"] = embedding.Model,
                ["confidentiality"] = document.Confidentiality,
                ["department"] = document.Department
            })).ToList();
        return await client.UpsertAsync(vectors, cancellationToken);
    }

    public async Task<int> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled) return 0;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var ids = await db.Documents.AsNoTracking().Select(document => document.Id).ToListAsync(cancellationToken);
        var total = 0;
        foreach (var id in ids)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                total += await SyncDocumentAsync(id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Pinecone: no se pudo sincronizar el expediente {DocumentId} ({Error}).", id, ex.GetType().Name);
            }
        }
        logger.LogInformation("Pinecone: sincronización completa, {Count} vectores en {Documents} expedientes.",
            total, ids.Count);
        return total;
    }
}
