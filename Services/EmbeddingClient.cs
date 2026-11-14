using System.Net.Http.Json;
using System.Text.Json;
using DocuSearch.Models;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services;

public sealed class EmbeddingOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "bge-m3";
    public int Dimensions { get; set; } = 1024;
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxChars { get; set; } = 2000;
}

/// <summary>
/// Cliente de embeddings locales contra Ollama (/api/embeddings). No envía documentos
/// al exterior: usa el servidor configurado, normalmente loopback.
/// </summary>
public sealed class EmbeddingClient(HttpClient http, IOptions<EmbeddingOptions> options, ILogger<EmbeddingClient> logger)
{
    private readonly EmbeddingOptions settings = options.Value;

    public bool Enabled => settings.Enabled;
    public string Model => settings.Model;

    public async Task<float[]?> TryEmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return await EmbedAsync(text, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            logger.LogWarning("Embeddings no disponibles ({Error}); se usará recuperación léxica.", ex.GetType().Name);
            return null;
        }
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!Enabled) throw new InvalidOperationException("Los embeddings están desactivados.");
        var prompt = TextAnalysis.Truncate(text.Trim(), Math.Max(100, settings.MaxChars));
        using var response = await http.PostAsJsonAsync("api/embeddings",
            new { model = settings.Model, prompt }, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Ollama respondió HTTP {(int)response.StatusCode} al calcular embeddings.");
        var payload = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: cancellationToken);
        if (payload?.Embedding is not { Length: > 0 } embedding)
            throw new InvalidOperationException("Ollama no devolvió un vector de embeddings.");
        if (settings.Dimensions > 0 && embedding.Length != settings.Dimensions)
            throw new InvalidOperationException($"El modelo devolvió {embedding.Length} dimensiones; se esperaban {settings.Dimensions}.");
        if (embedding.Any(value => !float.IsFinite(value)))
            throw new InvalidOperationException("El vector contiene valores no finitos.");

        // Se normaliza aquí para que el coseno sea un producto escalar.
        var magnitude = MathF.Sqrt(embedding.Sum(value => value * value));
        if (magnitude <= 0) throw new InvalidOperationException("El vector de embeddings tiene magnitud cero.");
        for (var index = 0; index < embedding.Length; index++) embedding[index] /= magnitude;
        return embedding;
    }

    private sealed record EmbeddingResponse(float[]? Embedding);
}
