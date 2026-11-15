using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services;

public sealed class PineconeOptions
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = "";
    public string IndexName { get; set; } = "docusearch-rag";
    public string Namespace { get; set; } = "docusearch";
    public int Dimension { get; set; } = 1024;
    public int TopK { get; set; } = 12;
    public double MinScore { get; set; } = 0.35;
    public int TimeoutSeconds { get; set; } = 30;
    public string ApiVersion { get; set; } = "2024-07";
}

public sealed record PineconeVector(string Id, float[] Values, IReadOnlyDictionary<string, object> Metadata);

public sealed record PineconeMatch(string Id, double Score, string DocumentId, int PageNumber);

public sealed record PineconeChunkMatch(string ChunkId, double Score, string DocumentId, int PageNumber, int ChunkIndex);

/// <summary>
/// Cliente REST mínimo para Pinecone. La API key se toma de configuración/entorno
/// y nunca se registra. El índice no se crea automáticamente: debe existir con la
/// dimensión y la métrica configuradas.
/// </summary>
public sealed class PineconeClient(HttpClient http, IOptions<PineconeOptions> options, ILogger<PineconeClient> logger)
{
    private readonly PineconeOptions settings = options.Value;
    private readonly SemaphoreSlim hostGate = new(1, 1);
    private string? host;

    public bool Enabled => settings.Enabled && !string.IsNullOrWhiteSpace(settings.ApiKey);
    public int TopK => Math.Clamp(settings.TopK, 1, 100);
    public double MinScore => Math.Clamp(settings.MinScore, 0, 1);
    public string IndexName => settings.IndexName;

    public async Task<int> UpsertAsync(IReadOnlyList<PineconeVector> vectors, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return 0;
        var valid = vectors.Where(vector => vector.Values.Length == settings.Dimension).ToList();
        var upserted = 0;
        foreach (var chunk in valid.Chunk(100))
        {
            var payload = new Dictionary<string, object?>
            {
                ["vectors"] = chunk.Select(vector => new Dictionary<string, object?>
                {
                    ["id"] = vector.Id,
                    ["values"] = vector.Values,
                    ["metadata"] = vector.Metadata
                }).ToArray(),
                ["namespace"] = settings.Namespace
            };
            using var response = await SendDataAsync(HttpMethod.Post, "/vectors/upsert", payload, cancellationToken);
            if (response.RootElement.TryGetProperty("upsertedCount", out var count)) upserted += count.GetInt32();
        }
        logger.LogInformation("Pinecone: {Count} vectores sincronizados en {Index}.", upserted, settings.IndexName);
        return upserted;
    }

    public async Task<IReadOnlyList<PineconeMatch>> QueryAsync(float[] queryVector,
        IReadOnlyCollection<string> documentIds, CancellationToken cancellationToken = default)    {
        if (!Enabled || documentIds.Count == 0) return [];
        var filter = new Dictionary<string, object?>
        {
            ["documentId"] = new Dictionary<string, object?> { ["$in"] = documentIds.Distinct().ToArray() }
        };
        var payload = new Dictionary<string, object?>
        {
            ["vector"] = queryVector,
            ["topK"] = TopK,
            ["includeMetadata"] = true,
            ["namespace"] = settings.Namespace,
            ["filter"] = filter
        };
        using var response = await SendDataAsync(HttpMethod.Post, "/query", payload, cancellationToken);
        if (!response.RootElement.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<PineconeMatch>();
        foreach (var match in matches.EnumerateArray())
        {
            var score = match.TryGetProperty("score", out var scoreElement) ? scoreElement.GetDouble() : 0;
            if (score < MinScore) continue;
            if (!match.TryGetProperty("metadata", out var metadata) ||
                !metadata.TryGetProperty("documentId", out var documentId) ||
                !metadata.TryGetProperty("page", out var page)) continue;
            var id = documentId.GetString();
            if (string.IsNullOrWhiteSpace(id) || !page.TryGetInt32(out var pageNumber)) continue;
            result.Add(new PineconeMatch(match.TryGetProperty("id", out var matchId) ? matchId.GetString() ?? "" : "",
                score, id, pageNumber));
        }
        return result;
    }

    /// <summary>Consulta específica de fragmentos RAG (metadata.kind = ragchunk).</summary>
    public async Task<IReadOnlyList<PineconeChunkMatch>> QueryChunksAsync(float[] queryVector,
        IReadOnlyCollection<string> documentIds, int topK, CancellationToken cancellationToken = default)
    {
        if (!Enabled || documentIds.Count == 0) return [];
        var filter = new Dictionary<string, object?>
        {
            ["kind"] = "ragchunk",
            ["documentId"] = new Dictionary<string, object?> { ["$in"] = documentIds.Distinct().ToArray() }
        };
        var payload = new Dictionary<string, object?>
        {
            ["vector"] = queryVector,
            ["topK"] = Math.Clamp(topK, 1, 100),
            ["includeMetadata"] = true,
            ["namespace"] = settings.Namespace,
            ["filter"] = filter
        };
        using var response = await SendDataAsync(HttpMethod.Post, "/query", payload, cancellationToken);
        if (!response.RootElement.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<PineconeChunkMatch>();
        foreach (var match in matches.EnumerateArray())
        {
            var score = match.TryGetProperty("score", out var scoreElement) ? scoreElement.GetDouble() : 0;
            if (score < MinScore) continue;
            if (!match.TryGetProperty("metadata", out var metadata) ||
                !metadata.TryGetProperty("chunkId", out var chunkId) ||
                !metadata.TryGetProperty("documentId", out var documentId) ||
                !metadata.TryGetProperty("page", out var page) ||
                !metadata.TryGetProperty("chunkIndex", out var chunkIndex)) continue;
            var id = chunkId.GetString();
            var document = documentId.GetString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(document) ||
                !page.TryGetInt32(out var pageNumber) || !chunkIndex.TryGetInt32(out var index)) continue;
            result.Add(new PineconeChunkMatch(id, score, document, pageNumber, index));
        }
        return result;
    }

    /// <summary>
    /// Lista los IDs con un prefijo dado (p. ej. «rag:{documentId}:»). Requiere índice
    /// serverless: los índices por pods no admiten /vectors/list.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListIdsAsync(string prefix, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return [];
        var ids = new List<string>();
        string? next = null;
        do
        {
            var query = $"/vectors/list?namespace={Uri.EscapeDataString(settings.Namespace)}" +
                $"&prefix={Uri.EscapeDataString(prefix)}&limit=100" +
                (next is null ? "" : $"&paginationToken={Uri.EscapeDataString(next)}");
            using var response = await SendDataAsync(HttpMethod.Get, query, null, cancellationToken);
            var root = response.RootElement;
            if (root.TryGetProperty("vectors", out var vectors) && vectors.ValueKind == JsonValueKind.Array)
                ids.AddRange(vectors.EnumerateArray()
                    .Select(vector => vector.TryGetProperty("id", out var id) ? id.GetString() : null)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Select(id => id!));
            next = root.TryGetProperty("pagination", out var pagination) &&
                   pagination.TryGetProperty("next", out var nextElement)
                ? nextElement.GetString()
                : null;
        } while (!string.IsNullOrEmpty(next));
        return ids;
    }

    /// <summary>Borra vectores por ID en lotes de 1000 (límite de la API).</summary>
    public async Task<int> DeleteIdsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        if (!Enabled || ids.Count == 0) return 0;
        foreach (var batch in ids.Distinct(StringComparer.Ordinal).Chunk(1000))
        {
            var payload = new Dictionary<string, object?> { ["ids"] = batch, ["namespace"] = settings.Namespace };
            using var _ = await SendDataAsync(HttpMethod.Post, "/vectors/delete", payload, cancellationToken);
        }
        logger.LogInformation("Pinecone: {Count} vectores eliminados de {Index}.", ids.Count, settings.IndexName);
        return ids.Count;
    }

    /// <summary>Borra todos los vectores cuyo ID empieza por el prefijo.</summary>
    public async Task<int> DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return 0;
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("El prefijo no puede estar vacío: borraría el namespace completo.", nameof(prefix));
        return await DeleteIdsAsync(await ListIdsAsync(prefix, cancellationToken), cancellationToken);
    }

    private async Task<JsonDocument> SendDataAsync(HttpMethod method, string path, object? payload,
        CancellationToken cancellationToken)
    {
        var indexHost = await EnsureReadyAsync(cancellationToken);
        using var request = new HttpRequestMessage(method, $"https://{indexHost}{path}");
        if (payload is not null) request.Content = JsonContent.Create(payload);
        AddHeaders(request);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Pinecone respondió HTTP {(int)response.StatusCode} en {path}.");
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    private async Task<string> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
            throw new InvalidOperationException("Pinecone está desactivado o no tiene ApiKey.");
        if (host is not null) return host;

        await hostGate.WaitAsync(cancellationToken);
        try
        {
            if (host is not null) return host;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"indexes/{Uri.EscapeDataString(settings.IndexName)}");
            AddHeaders(request);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"No se pudo leer el índice Pinecone «{settings.IndexName}»: HTTP {(int)response.StatusCode}.");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            var dimension = root.TryGetProperty("dimension", out var dimensionElement) ? dimensionElement.GetInt32() : 0;
            var metric = root.TryGetProperty("metric", out var metricElement) ? metricElement.GetString() : null;
            if (dimension != settings.Dimension)
                throw new InvalidOperationException($"El índice Pinecone tiene {dimension} dimensiones; se esperaban {settings.Dimension}.");
            if (!string.Equals(metric, "cosine", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"El índice Pinecone usa métrica «{metric}»; se esperaba cosine.");
            if (root.TryGetProperty("status", out var status) &&
                status.TryGetProperty("ready", out var ready) && !ready.GetBoolean())
                throw new InvalidOperationException("El índice Pinecone todavía no está listo.");
            host = root.TryGetProperty("host", out var hostElement) ? hostElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(host))
                throw new InvalidOperationException("Pinecone no devolvió el host del índice.");
            logger.LogInformation("Pinecone: índice {Index} listo ({Dimension} dims, cosine).", settings.IndexName, dimension);
            return host!;
        }
        finally
        {
            hostGate.Release();
        }
    }

    private void AddHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Api-Key", settings.ApiKey);
        request.Headers.TryAddWithoutValidation("X-Pinecone-API-Version", settings.ApiVersion);
        request.Headers.Accept.ParseAdd("application/json");
    }
}
