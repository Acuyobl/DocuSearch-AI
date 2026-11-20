using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuSearch.Services;

/// <summary>
/// Calcula y guarda embeddings por página. Se ejecuta fuera de las transacciones de
/// documentos; si el modelo no está disponible, el documento sigue siendo buscable
/// por el índice léxico.
/// </summary>
public sealed class EmbeddingIndexer(
    EmbeddingClient client,
    IDbContextFactory<AppDbContext> factory,
    ILogger<EmbeddingIndexer> logger)
{
    public bool Enabled => client.Enabled;

    public async Task<int> IndexDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return 0;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null) return 0;

        var pages = document.Pages.Where(page => !string.IsNullOrWhiteSpace(page.ExtractedText)).ToList();
        var existing = (await db.PageEmbeddings.Where(embedding => embedding.DocumentId == documentId).ToListAsync(cancellationToken))
            .ToDictionary(embedding => embedding.PageNumber);
        var keep = new HashSet<int>();
        var indexed = 0;

        foreach (var page in pages)
        {
            var source = $"{document.Title}\n{document.Category}\n{string.Join(' ', document.Keywords)}\n{page.ExtractedText}";
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{client.Model}\n{source}")));
            keep.Add(page.PageNumber);
            if (existing.TryGetValue(page.PageNumber, out var current) &&
                current.SourceHash == hash && current.Model == client.Model && current.Dimensions > 0)
                continue;

            float[]? vector;
            try
            {
                vector = await client.TryEmbedAsync(source, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("No se pudo calcular el embedding del expediente {Code}, página {Page} ({Error}).",
                    document.InternalCode, page.PageNumber, ex.GetType().Name);
                vector = null;
            }

            if (vector is null)
            {
                if (current is not null) db.PageEmbeddings.Remove(current);
                continue;
            }

            if (current is null)
            {
                db.PageEmbeddings.Add(new PageEmbedding
                {
                    DocumentId = documentId,
                    PageNumber = page.PageNumber,
                    Model = client.Model,
                    Dimensions = vector.Length,
                    Vector = ToBytes(vector),
                    SourceHash = hash
                });
            }
            else
            {
                current.Model = client.Model;
                current.Dimensions = vector.Length;
                current.Vector = ToBytes(vector);
                current.SourceHash = hash;
                current.CreatedUtc = DateTimeOffset.UtcNow;
            }
            indexed++;
        }

        foreach (var stale in existing.Values.Where(embedding => !keep.Contains(embedding.PageNumber)))
            db.PageEmbeddings.Remove(stale);

        if (indexed > 0 || existing.Count != keep.Count)
            await db.SaveChangesAsync(cancellationToken);
        return indexed;
    }

    public async Task<int> ReindexAllAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled) return 0;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var documentIds = await db.Documents.AsNoTracking().Select(document => document.Id).ToListAsync(cancellationToken);
        var total = 0;
        foreach (var documentId in documentIds)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                total += await IndexDocumentAsync(documentId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("No se pudo indexar el expediente {DocumentId} ({Error}).", documentId, ex.GetType().Name);
            }
        }
        logger.LogInformation("Índice semántico actualizado: {Count} páginas en {Documents} expedientes.", total, documentIds.Count);
        return total;
    }

    public static byte[] ToBytes(float[] vector) => MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    public static float[] FromBytes(byte[] bytes)
    {
        var copy = new byte[bytes.Length];
        Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);
        return MemoryMarshal.Cast<byte, float>(copy.AsSpan()).ToArray();
    }
}
