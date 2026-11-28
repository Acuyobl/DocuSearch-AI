using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services.Rag;

/// <summary>
/// Reranker cross-encoder real. Llama a un microservicio local FastEmbed/ONNX. Si no está configurado o
/// falla, conserva el orden híbrido; el reranker semántico (pregunta+fragmento) solo se usa con
/// Rag:SemanticRerankFallback=true, porque favorece fragmentos cortos y cuesta una llamada por candidato.
/// Las puntuaciones devueltas están siempre en [0, 1].
/// </summary>
public sealed class CrossEncoderRagReranker(
    IOptions<RagOptions> options,
    IHttpClientFactory httpFactory,
    SemanticRagReranker fallback,
    ILogger<CrossEncoderRagReranker> logger) : IRagReranker
{
    /// <summary>Máximo de candidatos por llamada al cross-encoder.</summary>
    public const int MaxCandidates = 30;

    private readonly RagOptions settings = options.Value;

    public async Task<IReadOnlyList<RagHit>> RerankAsync(string query, IReadOnlyList<RagHit> candidates,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count <= 1) return candidates;
        // El retriever ya decide el tamaño del pool; aquí solo se aplica el tope del servicio.
        var limited = candidates.Take(MaxCandidates).ToList();
        if (string.IsNullOrWhiteSpace(settings.RerankerUrl)) return await FallbackAsync(query, limited, cancellationToken);
        try
        {
            using var http = httpFactory.CreateClient("rag-reranker");
            var payload = new
            {
                query,
                documents = limited.Select(hit => TextAnalysis.Truncate(hit.Chunk.Text, 2000)).ToArray()
            };
            // El cliente con nombre no tiene BaseAddress: con la ruta relativa «rerank» la petición fallaba
            // siempre y se usaba el respaldo sin avisar. Se construye la URL absoluta desde Rag:RerankerUrl.
            // Cuerpo con Content-Length: PostAsJsonAsync envía «chunked» y el servicio Python
            // (BaseHTTPRequestHandler) lee Content-Length, recibía 0 bytes y respondía 400.
            using var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(Endpoint(settings.RerankerUrl), content, cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<RerankResponse>(cancellationToken: cancellationToken);
            if (result?.Scores is not { Length: > 0 } scores || scores.Length != limited.Count)
                throw new InvalidOperationException("El reranker devolvió una cantidad de scores inválida.");
            var normalized = NormalizeScores(scores);
            return limited.Select((hit, index) => new RagHit(hit.Chunk, normalized[index]))
                .OrderByDescending(hit => hit.Score).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Cross-encoder no disponible ({Error}); se conserva el orden híbrido.", ex.GetType().Name);
            return await FallbackAsync(query, limited, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<RagHit>> FallbackAsync(string query, IReadOnlyList<RagHit> candidates,
        CancellationToken cancellationToken) =>
        settings.SemanticRerankFallback ? await fallback.RerankAsync(query, candidates, cancellationToken) : candidates;

    /// <summary>URL absoluta de POST /rerank a partir de la URL base configurada (con o sin barra final).</summary>
    public static Uri Endpoint(string baseUrl) => new(new Uri(baseUrl.TrimEnd('/') + "/"), "rerank");

    /// <summary>
    /// Si el servicio devuelve logits (algún valor fuera de [0, 1]), se aplica la
    /// sigmoide a todos para conservar el orden y obtener una relevancia en [0, 1].
    /// </summary>
    public static double[] NormalizeScores(IReadOnlyList<double> scores)
    {
        var finite = scores.Select(score => double.IsFinite(score) ? score : double.NegativeInfinity).ToArray();
        return finite.All(score => score is >= 0 and <= 1)
            ? finite
            : finite.Select(score => 1 / (1 + Math.Exp(-score))).ToArray();
    }

    private sealed record RerankResponse(double[]? Scores);
}
