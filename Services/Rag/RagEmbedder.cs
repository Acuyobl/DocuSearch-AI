using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DocuSearch.Services.Rag;

/// <summary>Embedder bge-m3 con caché por hash de fragmento.</summary>
public sealed class BgeM3RagEmbedder(EmbeddingClient client, ILogger<BgeM3RagEmbedder> logger) : IRagEmbedder
{
    private readonly ConcurrentDictionary<string, float[]> cache = new(StringComparer.Ordinal);
    public bool Enabled => client.Enabled;
    public string Model => client.Model;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!Enabled) throw new InvalidOperationException("Los embeddings están desactivados.");
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        if (cache.TryGetValue(key, out var cached)) return cached;
        var vector = await client.EmbedAsync(text, cancellationToken);
        cache.TryAdd(key, vector);
        return vector;
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        var result = new List<float[]>(texts.Count);
        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(await EmbedAsync(text, cancellationToken));
        }
        logger.LogDebug("Embeddings de fragmentos: {Count} textos, caché {Cache}.", texts.Count, cache.Count);
        return result;
    }
}
