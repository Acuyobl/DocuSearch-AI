using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services.Rag;

/// <summary>
/// Reranker cross-encoder real. Llama a un microservicio local FastEmbed/ONNX;
/// si no está disponible, cae al reranker semántico anterior para no romper la app.
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
        if (string.IsNullOrWhiteSpace(settings.RerankerUrl))
            return await fallback.RerankAsync(query, limited, cancellationToken);
        try
        {
            using var http = httpFactory.CreateClient("rag-reranker");
            var payload = new
            {
                query,
                documents = limited.Select(hit => TextAnalysis.Truncate(hit.Chunk.Text, 2000)).ToArray()
            };
            using var response = await http.PostAsJsonAsync("rerank", payload, cancellationToken);
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
            logger.LogWarning("Cross-encoder no disponible ({Error}); se usa reranker semántico.", ex.GetType().Name);
            return await fallback.RerankAsync(query, limited, cancellationToken);
        }
    }

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
