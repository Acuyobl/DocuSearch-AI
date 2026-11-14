using Microsoft.Extensions.Options;

namespace DocuSearch.Services.Rag;

/// <summary>Orquesta Loader → Chunker → Embedder → VectorStore.</summary>
public sealed class RagIngestionService(
    IOptions<RagOptions> options,
    FixedRagChunker fixedChunker,
    SemanticRagChunker semanticChunker,
    IRagEmbedder embedder,
    IRagVectorStore vectorStore,
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
            var chunks = string.Equals(settings.ChunkingStrategy, "semantic", StringComparison.OrdinalIgnoreCase)
                ? await semanticChunker.ChunkAsync(document, cancellationToken)
                : await fixedChunker.ChunkAsync(document, cancellationToken);
            if (chunks.Count == 0) continue;
            var vectors = await embedder.EmbedBatchAsync(chunks.Select(chunk => chunk.Text).ToList(), cancellationToken);
            var items = chunks.Zip(vectors, (chunk, vector) => new RagChunkVector(chunk, vector)).ToList();
            await vectorStore.DeleteDocumentAsync(document.SourceId, cancellationToken);
            await vectorStore.UpsertAsync(items, cancellationToken);
            totalChunks += items.Count;
            logger.LogInformation("RAG: {Chunks} fragmentos indexados para {Source} ({Type}).",
                items.Count, document.SourceId, document.SourceType);
        }
        logger.LogInformation("RAG: ingesta completa, {Documents} documentos y {Chunks} fragmentos en {Store}.",
            documents.Count, totalChunks, vectorStore.Name);
        return (documents.Count, totalChunks);
    }
}
