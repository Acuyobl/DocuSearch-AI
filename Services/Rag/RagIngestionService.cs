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
    ILogger<RagIngestionService> logger)
{
    private readonly RagOptions settings = options.Value;

    public async Task<(int Documents, int Chunks)> IngestAsync(IRagLoader loader,
        CancellationToken cancellationToken = default)
    {
        if (!embedder.Enabled) throw new InvalidOperationException("Los embeddings están desactivados; no se puede ingerir.");
        var documents = await loader.LoadAsync(cancellationToken);
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
        if (chunks.Count == 0) return 0;
        var vectors = await embedder.EmbedBatchAsync(chunks.Select(chunk => chunk.Text).ToList(), cancellationToken);
        var items = chunks.Zip(vectors, (chunk, vector) => new RagChunkVector(chunk, vector)).ToList();
        await vectorStore.DeleteDocumentAsync(document.SourceId, cancellationToken);
        await vectorStore.UpsertAsync(items, cancellationToken);
        logger.LogInformation("RAG: {Chunks} fragmentos indexados para {Source} ({Type}).",
            items.Count, document.SourceId, document.SourceType);
        return items.Count;
    }
}
