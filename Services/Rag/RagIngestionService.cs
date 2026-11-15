using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services.Rag;

/// <summary>Orquesta Loader → Chunker → Embedder → VectorStore.</summary>
public sealed class RagIngestionService(
    IOptions<RagOptions> options,
    FixedRagChunker fixedChunker,
    SemanticRagChunker semanticChunker,
    IRagEmbedder embedder,
    IRagVectorStore vectorStore,
    IDbContextFactory<AppDbContext> factory,
    ILogger<RagIngestionService> logger,
    IOptions<EmbeddingOptions>? embeddingOptions = null)
{
    private readonly RagOptions settings = options.Value;
    private readonly int maxEmbeddingChars = Math.Max(100, embeddingOptions?.Value.MaxChars ?? 8000);

    public async Task<(int Documents, int Chunks)> IngestAsync(IRagLoader loader,
        CancellationToken cancellationToken = default)
    {
        if (!embedder.Enabled) throw new InvalidOperationException("Los embeddings están desactivados; no se puede ingerir.");
        return await IngestAsync(await loader.LoadAsync(cancellationToken), cancellationToken);
    }

    /// <summary>Ingiere documentos ya cargados (evita leer dos veces la fuente).</summary>
    public async Task<(int Documents, int Chunks)> IngestAsync(IReadOnlyList<RagDocument> documents,
        CancellationToken cancellationToken = default)
    {
        if (!embedder.Enabled) throw new InvalidOperationException("Los embeddings están desactivados; no se puede ingerir.");
        var totalChunks = 0;
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalChunks += await IngestDocumentAsync(document, cancellationToken);
        }
        logger.LogInformation("RAG: ingesta completa, {Documents} documentos y {Chunks} fragmentos en {Store}.",
            documents.Count, totalChunks, vectorStore.Name);
        return (documents.Count, totalChunks);
    }

    /// <summary>Indexa un expediente ya persistido (usado por carga/edición/reproceso).</summary>
    public async Task<int> IngestDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null) return 0;
        var pages = document.Pages.Select(page => new RagPage(page.PageNumber, page.ExtractedText)).ToList();
        var ragDocument = new RagDocument(document.Id, "sql", document.Title,
            string.Join("\n", pages.Select(page => page.Text)), pages,
            new Dictionary<string, string>
            {
                ["code"] = document.InternalCode,
                ["source"] = "sql:Documents",
                ["date"] = document.UploadDate.ToString("O"),
                ["department"] = document.Department,
                ["confidentiality"] = document.Confidentiality
            });
        return await IngestDocumentAsync(ragDocument, cancellationToken);
    }

    private async Task<int> IngestDocumentAsync(RagDocument document, CancellationToken cancellationToken)
    {
        if (!embedder.Enabled) throw new InvalidOperationException("Los embeddings están desactivados; no se puede ingerir.");
        var chunks = string.Equals(settings.ChunkingStrategy, "semantic", StringComparison.OrdinalIgnoreCase)
            ? await semanticChunker.ChunkAsync(document, cancellationToken)
            : await fixedChunker.ChunkAsync(document, cancellationToken);
        if (chunks.Count == 0)
        {
            // Sin texto (p. ej. reproceso de un escaneado): se eliminan los fragmentos anteriores.
            await vectorStore.ReplaceDocumentAsync(document.SourceId, [], cancellationToken);
            return 0;
        }
        var truncated = chunks.Count(chunk => chunk.Text.Length > maxEmbeddingChars);
        if (truncated > 0)
            logger.LogWarning("RAG: {Truncated} de {Chunks} fragmentos de {Source} superan Embeddings:MaxChars ({MaxChars}); " +
                "su vector no cubrirá el texto completo. Sube MaxChars o baja Rag:MaxTokens.",
                truncated, chunks.Count, document.SourceId, maxEmbeddingChars);
        var vectors = await embedder.EmbedBatchAsync(chunks.Select(chunk => chunk.Text).ToList(), cancellationToken);
        if (vectors.Count != chunks.Count)
            throw new InvalidOperationException($"El embedder devolvió {vectors.Count} vectores para {chunks.Count} fragmentos.");
        var items = chunks.Zip(vectors, (chunk, vector) => new RagChunkVector(chunk, vector, embedder.Model)).ToList();
        await vectorStore.ReplaceDocumentAsync(document.SourceId, items, cancellationToken);
        logger.LogInformation("RAG: {Chunks} fragmentos indexados para {Source} ({Type}).",
            items.Count, document.SourceId, document.SourceType);
        return items.Count;
    }

    /// <summary>
    /// Elimina los fragmentos de un expediente borrado. En SQL ya los quita la FK en
    /// cascada; esto cubre además los vectores externos (Pinecone).
    /// </summary>
    public Task RemoveDocumentAsync(string documentId, CancellationToken cancellationToken = default) =>
        vectorStore.DeleteDocumentAsync(documentId, cancellationToken);
}
