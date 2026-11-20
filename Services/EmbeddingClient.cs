using System.Net.Http.Json;
using System.Text.Json;
using DocuSearch.Models;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services;

public sealed class EmbeddingOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>pinecone</summary>
    public string Provider { get; set; } = "pinecone";
    public string BaseUrl { get; set; } = "https://api.pinecone.io";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "multilingual-e5-large";
    public int Dimensions { get; set; } = 1024;
    public int TimeoutSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 32;
    /// <summary>Tope de caracteres por texto; e5-large admite contexto suficiente para un fragmento.</summary>
    public int MaxChars { get; set; } = 8000;
}

/// <summary>
/// Embeddings con Pinecone Inference. No usa Ollama ni envía documentos a otro
/// proveedor: el texto se envía únicamente al endpoint de inferencia de Pinecone.
/// </summary>
public sealed class EmbeddingClient(HttpClient http, IOptions<EmbeddingOptions> options, ILogger<EmbeddingClient> logger)
{
    private readonly EmbeddingOptions settings = options.Value;

    public bool Enabled => settings.Enabled && !string.IsNullOrWhiteSpace(settings.ApiKey);
    public string Model => settings.Model;

    public async Task<float[]?> TryEmbedAsync(string text, bool isQuery = false,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return await EmbedAsync(text, isQuery, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            logger.LogWarning("Embeddings Pinecone no disponibles ({Error}); se usará recuperación léxica.", ex.GetType().Name);
            return null;
        }
    }

    public async Task<float[]> EmbedAsync(string text, bool isQuery = false,
        CancellationToken cancellationToken = default)
    {
        var vectors = await EmbedBatchAsync([text], isQuery, cancellationToken);
        return vectors[0];
    }

    /// <summary>Calcula embeddings por lote; Pinecone acepta varios textos por llamada.</summary>
    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, bool isQuery = false,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled) throw new InvalidOperationException("Los embeddings de Pinecone están desactivados o falta Embeddings:ApiKey.");
        var result = new List<float[]>(texts.Count);
        var size = Math.Clamp(settings.BatchSize, 1, 96);
        for (var offset = 0; offset < texts.Count; offset += size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = texts.Skip(offset).Take(size)
                .Select(text => new { text = TextAnalysis.Truncate(text.Trim(), Math.Max(100, settings.MaxChars)) })
                .ToArray();
            using var request = new HttpRequestMessage(HttpMethod.Post, "embed")
            {
                Content = JsonContent.Create(new
                {
                    model = settings.Model,
                    inputs = batch,
                    parameters = new { input_type = isQuery ? "query" : "passage" }
                })
            };
            request.Headers.TryAddWithoutValidation("Api-Key", settings.ApiKey);
            request.Headers.TryAddWithoutValidation("X-Pinecone-API-Version", "2024-07");
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Pinecone respondió HTTP {(int)response.StatusCode} al calcular embeddings.");
            var payload = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: cancellationToken);
            if (payload?.Data is not { Count: > 0 } data || data.Count != batch.Length)
                throw new InvalidOperationException("Pinecone no devolvió todos los vectores de embeddings.");
            foreach (var item in data)
            {
                if (item.Values is not { Length: > 0 } embedding)
                    throw new InvalidOperationException("Pinecone devolvió un vector vacío.");
                if (settings.Dimensions > 0 && embedding.Length != settings.Dimensions)
                    throw new InvalidOperationException($"El modelo devolvió {embedding.Length} dimensiones; se esperaban {settings.Dimensions}.");
                if (embedding.Any(value => !float.IsFinite(value)))
                    throw new InvalidOperationException("El vector contiene valores no finitos.");
                var magnitude = MathF.Sqrt(embedding.Sum(value => value * value));
                if (magnitude <= 0) throw new InvalidOperationException("El vector de embeddings tiene magnitud cero.");
                for (var index = 0; index < embedding.Length; index++) embedding[index] /= magnitude;
                result.Add(embedding);
            }
        }
        return result;
    }

    private sealed record EmbeddingResponse(List<EmbeddingItem>? Data);
    private sealed record EmbeddingItem(float[]? Values);
}
