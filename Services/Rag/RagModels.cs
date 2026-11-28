namespace DocuSearch.Services.Rag;

public sealed class RagOptions
{
    /// <summary>fixed | semantic</summary>
    public string ChunkingStrategy { get; set; } = "fixed";
    /// <summary>
    /// Palabras por fragmento. multilingual-e5-large trunca a ~512 tokens (≈ 350 palabras en español):
    /// con fragmentos más largos el final no entra en el vector y no se puede encontrar.
    /// </summary>
    public int MaxTokens { get; set; } = 300;
    public int OverlapTokens { get; set; } = 60;
    public double SemanticBreakThreshold { get; set; } = 0.55;
    public int CandidateK { get; set; } = 30;
    /// <summary>Fragmentos tras la fusión; luego se agrupan por página (máximo RagContextLimits.MaxSources).</summary>
    public int TopK { get; set; } = 16;
    public double VectorWeight { get; set; } = 0.65;
    public double LexicalWeight { get; set; } = 0.35;
    public bool Rerank { get; set; } = true;
    public int RerankCandidates { get; set; } = 16;
    /// <summary>URL del microservicio cross-encoder; vacío usa el reranker semántico local.</summary>
    public string RerankerUrl { get; set; } = "";
    public int RerankerTimeoutSeconds { get; set; } = 60;
    /// <summary>Sin cross-encoder, reordena con el reranker semántico (lento y sesgado a fragmentos cortos).</summary>
    public bool SemanticRerankFallback { get; set; }
    /// <summary>sql | pinecone</summary>
    public string VectorStore { get; set; } = "sql";
    /// <summary>
    /// Con VectorStore=sql, mantiene los vectores de fragmento en memoria (≈4 KB por fragmento) y
    /// solo lee de SQL el texto de los ganadores. Desactivar si el índice no cabe en memoria.
    /// </summary>
    public bool VectorCache { get; set; } = true;
    public bool AuditContent { get; set; } = true;
    /// <summary>El asistente usa el pipeline por fragmentos; si falla, cae al RAG anterior.</summary>
    public bool UseChunkPipeline { get; set; } = true;
}

public sealed record RagPage(int PageNumber, string Text);

public sealed record RagDocument(
    string SourceId,
    string SourceType,
    string Title,
    string Text,
    IReadOnlyList<RagPage> Pages,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record RagChunk(
    string Id,
    string DocumentId,
    string InternalCode,
    string SourceType,
    int PageNumber,
    int ChunkIndex,
    int StartOffset,
    int EndOffset,
    string Text,
    string Hash);

/// <param name="Model">Modelo de embeddings que generó el vector; se persiste para detectar cambios de modelo.</param>
public sealed record RagChunkVector(RagChunk Chunk, float[] Vector, string Model = "");

public sealed record RagScoredChunk(RagChunk Chunk, double Score);

public sealed record RagHit(RagChunk Chunk, double Score);

public sealed record RagQueryRequest(string Question);

public interface IRagLoader
{
    string SourceType { get; }
    Task<IReadOnlyList<RagDocument>> LoadAsync(CancellationToken cancellationToken = default);
}

public interface IRagChunker
{
    string Strategy { get; }
    Task<IReadOnlyList<RagChunk>> ChunkAsync(RagDocument document, CancellationToken cancellationToken = default);
}

public interface IRagEmbedder
{
    bool Enabled { get; }
    string Model { get; }
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
    /// <summary>Embedding de consulta; e5 distingue query de passage.</summary>
    Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default) =>
        EmbedAsync(text, cancellationToken);
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
    /// <summary>Embedding de un texto efímero que no merece ocupar la caché (p. ej. consulta + fragmento).</summary>
    Task<float[]> EmbedUncachedAsync(string text, CancellationToken cancellationToken = default) =>
        EmbedAsync(text, cancellationToken);
}

/// <summary>Similitud de la consulta con fragmentos concretos, para candidatos que llegaron por otra vía (BM25).</summary>
public interface IRagChunkScorer
{
    Task<IReadOnlyDictionary<string, double>> ScoreChunksAsync(float[] vector, IReadOnlyCollection<string> chunkIds,
        CancellationToken cancellationToken = default);
}

public interface IRagVectorStore
{
    string Name { get; }
    Task UpsertAsync(IReadOnlyList<RagChunkVector> chunks, CancellationToken cancellationToken = default);
    /// <summary>
    /// Sustituye de forma atómica todos los fragmentos de un documento. Con una lista
    /// vacía deja el documento sin fragmentos (p. ej. tras un reproceso sin texto).
    /// </summary>
    Task ReplaceDocumentAsync(string documentId, IReadOnlyList<RagChunkVector> chunks,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RagScoredChunk>> QueryAsync(float[] vector, IReadOnlyCollection<string> documentIds,
        int topK, CancellationToken cancellationToken = default);
    Task DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default);
}

public interface IRagRetriever
{
    Task<IReadOnlyList<RagHit>> RetrieveAsync(string query, float[] queryVector,
        IReadOnlyCollection<string> documentIds, CancellationToken cancellationToken = default);
}

public interface IRagReranker
{
    Task<IReadOnlyList<RagHit>> RerankAsync(string query, IReadOnlyList<RagHit> candidates,
        CancellationToken cancellationToken = default);
}
