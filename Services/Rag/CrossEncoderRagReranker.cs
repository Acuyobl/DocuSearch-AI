using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services.Rag;

/// <summary>
/// Reranker cross-encoder real. Llama a un microservicio local FastEmbed/ONNX;
/// si no está disponible, cae al reranker semántico anterior para no romper la app.
/// </summary>
public sealed class CrossEncoderRagReranker(
    IOptions<RagOptions> options,
    IHttpClientFactory httpFactory,
    SemanticRagReranker fallback,
    ILogger<CrossEncoderRagReranker> logger) : IRagReranker
{
    private readonly RagOptions settings = options.Value;

    public async Task<IReadOnlyList<RagHit>> RerankAsync(string query, IReadOnlyList<RagHit> candidates,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count <= 1) return candidates;
        var limited = candidates.Take(Math.Clamp(settings.RerankCandidates, 2, 30)).ToList();
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
            return limited.Select((hit, index) => new RagHit(hit.Chunk, scores[index]))
                .OrderByDescending(hit => hit.Score).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Cross-encoder no disponible ({Error}); se usa reranker semántico.", ex.GetType().Name);
            return await fallback.RerankAsync(query, candidates, cancellationToken);
        }
    }

    private sealed record RerankResponse(double[]? Scores);
}
