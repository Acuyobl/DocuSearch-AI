using System.Security.Cryptography;
using System.Text;

namespace DocuSearch.Services.Rag;

/// <summary>Embedder bge-m3 con caché LRU acotada por hash de texto.</summary>
public sealed class BgeM3RagEmbedder(EmbeddingClient client, ILogger<BgeM3RagEmbedder> logger) : IRagEmbedder
{
    /// <summary>Entradas máximas en caché (~4 MB con vectores de 1024 floats).</summary>
    public const int CacheCapacity = 1000;

    private readonly LruCache<string, float[]> cache = new(CacheCapacity);
    public bool Enabled => client.Enabled;
    public string Model => client.Model;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!Enabled) throw new InvalidOperationException("Los embeddings están desactivados.");
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        if (cache.TryGet(key, out var cached)) return cached;
        var vector = await client.EmbedAsync(text, cancellationToken);
        cache.Set(key, vector);
        return vector;
    }

    public Task<float[]> EmbedUncachedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!Enabled) throw new InvalidOperationException("Los embeddings están desactivados.");
        return client.EmbedAsync(text, cancellationToken);
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

/// <summary>Caché LRU mínima y segura entre hilos.</summary>
public sealed class LruCache<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly int capacity = Math.Max(1, capacity);
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> map = new();
    private readonly LinkedList<(TKey Key, TValue Value)> order = new();
    private readonly Lock gate = new();

    public int Count { get { lock (gate) return map.Count; } }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (gate)
        {
            if (map.TryGetValue(key, out var node))
            {
                order.Remove(node);
                order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        lock (gate)
        {
            if (map.TryGetValue(key, out var existing))
            {
                order.Remove(existing);
                map.Remove(key);
            }
            else if (map.Count >= capacity && order.Last is { } oldest)
            {
                order.RemoveLast();
                map.Remove(oldest.Value.Key);
            }
            map[key] = order.AddFirst((key, value));
        }
    }
}
