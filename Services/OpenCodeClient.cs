using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocuSearch.Models;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services;

public sealed class OpenCodeOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://127.0.0.1:4097";
    public string Username { get; set; } = "opencode";
    public string Password { get; set; } = "";
    /// <summary>Agente de OpenCode. Debe tener las herramientas desactivadas para uso documental.</summary>
    public string Agent { get; set; } = "";
    public string ProviderId { get; set; } = "ollama";
    public string ModelId { get; set; } = "qwen3:4b";
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>Permite un servidor OpenCode fuera de loopback (p. ej. host.docker.internal).</summary>
    public bool AllowNonLoopback { get; set; }
}

/// <summary>
/// Motor del asistente basado en el servidor HTTP de OpenCode (`opencode serve`).
/// Crea una sesión por consulta, envía pregunta + fuentes autorizadas a un agente sin
/// herramientas y valida la respuesta estructurada antes de mostrarla.
/// </summary>
public sealed class OpenCodeClient
{
    private static readonly string[] LocalProviders = ["ollama", "lmstudio", "llamacpp"];
    private readonly HttpClient http;
    private readonly OpenCodeOptions settings;
    private readonly object toolGate = new();
    private string[]? toolIds;
    private DateTimeOffset toolIdsExpires;

    public bool Enabled => settings.Enabled;
    public string Agent => settings.Agent;
    public string Model => $"{settings.ProviderId}/{settings.ModelId}";
    public string Engine => $"OpenCode · {Model}";
    public bool IsRemote => !LocalProviders.Contains(settings.ProviderId, StringComparer.OrdinalIgnoreCase);

    public OpenCodeClient(HttpClient http, IOptions<OpenCodeOptions> options)
    {
        settings = options.Value;
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") ||
            endpoint.AbsolutePath is not ("" or "/") ||
            endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            throw new InvalidOperationException("OpenCode:BaseUrl debe ser una URL como http://127.0.0.1:4097, sin credenciales ni rutas.");

        var host = endpoint.DnsSafeHost;
        var loopback = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
        if (!loopback)
        {
            if (!settings.AllowNonLoopback)
                throw new InvalidOperationException("OpenCode:BaseUrl no es loopback. Activa OpenCode:AllowNonLoopback solo para un contenedor o proxy local controlado.");
            if (string.IsNullOrWhiteSpace(settings.Password))
                throw new InvalidOperationException("Un servidor OpenCode fuera de loopback exige OpenCode:Password (se envía con autenticación básica).");
        }
        if (string.IsNullOrWhiteSpace(settings.ProviderId) || string.IsNullOrWhiteSpace(settings.ModelId))
            throw new InvalidOperationException("Configura OpenCode:ProviderId y OpenCode:ModelId.");
        if (settings.TimeoutSeconds is < 5 or > 600)
            throw new InvalidOperationException("OpenCode:TimeoutSeconds debe estar entre 5 y 600.");

        this.http = http;
        http.BaseAddress = endpoint;
        http.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        http.MaxResponseContentBufferSize = 64 * 1024 * 1024;
        if (!string.IsNullOrWhiteSpace(settings.Password))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}"));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
    }

    /// <summary>Comprueba que el servidor responde y, si se indicó un agente, que existe.</summary>
    public async Task CheckAsync(string? agent = null)
    {
        if (!Enabled) return;
        var expectedAgent = string.IsNullOrWhiteSpace(agent) ? settings.Agent : agent;
        var health = await GetAsync<HealthResponse>("global/health");
        if (!health.Healthy) throw new InvalidOperationException("El servidor OpenCode no se declara saludable.");
        if (string.IsNullOrWhiteSpace(expectedAgent)) return;
        var agents = await GetAsync<AgentInfo[]>("agent");
        if (agents.All(candidate => !string.Equals(candidate.Name, expectedAgent, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"El servidor OpenCode no tiene el agente «{expectedAgent}». Revísalo en opencode.json.");
    }

    /// <summary>Catálogo de proveedores/modelos del OpenCode local (para el selector de motores).</summary>
    public async Task<OpenCodeCatalog> FetchCatalogAsync()
    {
        if (!Enabled) throw new InvalidOperationException("OpenCode está desactivado en la configuración.");
        var response = await GetAsync<ProviderListResponse>("provider");
        var providers = (response.All ?? [])
            .Where(provider => !string.IsNullOrWhiteSpace(provider.Id))
            .Select(provider => new OpenCodeProvider(
                provider.Id!,
                string.IsNullOrWhiteSpace(provider.Name) ? provider.Id! : provider.Name!,
                provider.Options?.BaseUrl,
                (provider.Models ?? []).Values
                    .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                    .Select(model => new OpenCodeModel(
                        model.Id!,
                        string.IsNullOrWhiteSpace(model.Name) ? model.Id! : model.Name!,
                        model.Cost?.Input ?? 0,
                        model.Cost?.Output ?? 0,
                        model.Status))
                    .ToList()))
            .ToList();
        return new OpenCodeCatalog(providers, response.Connected ?? []);
    }

    public Task<AgentAnswer> GenerateAsync(string query, IReadOnlyList<SearchHit> sources) =>
        GenerateAsync(query, sources, settings.ProviderId, settings.ModelId, settings.Agent);

    public async Task<AgentAnswer> GenerateAsync(string query, IReadOnlyList<SearchHit> sources, string? providerId, string? modelId, string? agent)
    {
        if (!Enabled) throw new InvalidOperationException("La generación con OpenCode está desactivada.");
        var provider = string.IsNullOrWhiteSpace(providerId) ? settings.ProviderId : providerId;
        var model = string.IsNullOrWhiteSpace(modelId) ? settings.ModelId : modelId;
        var agentName = string.IsNullOrWhiteSpace(agent) ? settings.Agent : agent;
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000)
            throw new InvalidOperationException("La consulta debe tener entre 1 y 2000 caracteres.");
        if (sources.Count == 0) throw new InvalidOperationException("No hay fuentes autorizadas para consultar al modelo.");

        await CheckAsync(agentName);
        var selected = sources.DistinctBy(source => (source.Document.Id, source.PageNumber)).Take(6).ToList();
        var context = selected.Select((source, index) => new
        {
            id = index + 1,
            code = source.Document.InternalCode,
            title = source.Document.Title,
            page = source.PageNumber,
            text = TextAnalysis.Truncate(source.Snippet, 1000)
        }).ToArray();
        var payload = JsonSerializer.Serialize(new { question = query, sources = context }, Catalog.Json);

        var session = await PostAsync<SessionInfo>("session", new { title = $"DocuSearch AI · {DateTimeOffset.UtcNow:u}" });
        if (session?.Id is not { Length: > 0 } sessionId)
            throw new InvalidOperationException("OpenCode no devolvió el identificador de sesión.");
        try
        {
            var tools = await GetDisabledToolsAsync();
            var message = new Dictionary<string, object?>
            {
                ["model"] = new { providerID = provider, modelID = model },
                ["system"] = AssistantPrompt.System,
                ["tools"] = tools,
                ["parts"] = new[] { new { type = "text", text = payload } }
            };
            if (!string.IsNullOrWhiteSpace(agentName)) message["agent"] = agentName;
            var response = await PostAsync<MessageResponse>($"session/{Uri.EscapeDataString(sessionId)}/message", message);
            var text = string.Concat((response?.Parts ?? [])
                .Where(part => part.Type == "text" && !string.IsNullOrWhiteSpace(part.Text))
                .Select(part => part.Text!));
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("OpenCode devolvió una respuesta vacía.");

            var json = ExtractJsonObject(text)
                ?? throw new InvalidOperationException("OpenCode devolvió una respuesta sin el formato de citas requerido. No se ha mostrado como válida.");
            GeneratedAnswer generated;
            try
            {
                generated = JsonSerializer.Deserialize<GeneratedAnswer>(json, Catalog.Json)
                    ?? throw new InvalidOperationException("OpenCode devolvió una respuesta vacía.");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("OpenCode devolvió una respuesta sin el formato de citas requerido. No se ha mostrado como válida.", ex);
            }
            if (string.IsNullOrWhiteSpace(generated.Answer) || generated.Answer.Length > 10000 ||
                generated.Citations is not { Length: > 0 } || generated.Citations.Any(id => id < 1 || id > selected.Count))
                throw new InvalidOperationException("OpenCode devolvió contenido vacío o citas fuera de las fuentes autorizadas. La respuesta se ha bloqueado.");

            var tokens = response?.Info?.Tokens;
            if (tokens is { Input: < 0 } or { Output: < 0 })
                throw new InvalidOperationException("OpenCode devolvió contadores de tokens inválidos.");
            return new(generated.Answer.Trim(), generated.Citations.Distinct().Select(id => selected[id - 1]).ToList(),
                $"OpenCode · {provider}/{model}", tokens?.Input, tokens?.Output);
        }
        finally
        {
            await DeleteSessionAsync(sessionId);
        }
    }

    private async Task<Dictionary<string, bool>> GetDisabledToolsAsync()
    {
        string[]? ids;
        lock (toolGate)
            ids = toolIds is not null && DateTimeOffset.UtcNow < toolIdsExpires ? toolIds : null;
        if (ids is null)
        {
            ids = await GetAsync<string[]>("experimental/tool/ids");
            lock (toolGate)
            {
                toolIds = ids;
                toolIdsExpires = DateTimeOffset.UtcNow.AddMinutes(5);
            }
        }
        return ids.ToDictionary(id => id, _ => false);
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private async Task DeleteSessionAsync(string sessionId)
    {
        try
        {
            using var response = await http.DeleteAsync($"session/{Uri.EscapeDataString(sessionId)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // La limpieza de la sesión es best-effort; no debe ocultar la respuesta.
        }
    }

    private async Task<T> GetAsync<T>(string path)
    {
        try
        {
            using var response = await http.GetAsync(path);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(DescribeHttpFailure(response.StatusCode));
            return await response.Content.ReadFromJsonAsync<T>()
                ?? throw new InvalidOperationException("OpenCode respondió sin datos.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"No se pudo conectar con el servidor OpenCode en {settings.BaseUrl}. Inicia «opencode serve» y revisa OpenCode:Password.", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new InvalidOperationException($"OpenCode superó el tiempo de espera de {settings.TimeoutSeconds} segundos.", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("El servidor OpenCode no devolvió una respuesta JSON válida.", ex);
        }
    }

    private async Task<T?> PostAsync<T>(string path, object payload)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(path, payload, Catalog.Json);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(DescribeHttpFailure(response.StatusCode));
            return await response.Content.ReadFromJsonAsync<T>(Catalog.Json);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"No se pudo conectar con el servidor OpenCode en {settings.BaseUrl}. Inicia «opencode serve» y revisa OpenCode:Password.", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new InvalidOperationException($"OpenCode superó el tiempo de espera de {settings.TimeoutSeconds} segundos. El modelo remoto puede necesitar más margen.", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("El servidor OpenCode no devolvió una respuesta JSON válida.", ex);
        }
    }

    private string DescribeHttpFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "OpenCode rechazó las credenciales (HTTP 401). Configura OpenCode:Password con el valor de OPENCODE_SERVER_PASSWORD.",
        HttpStatusCode.NotFound => $"OpenCode no encontró el recurso solicitado (HTTP 404). Revisa el agente «{settings.Agent}» en opencode.json.",
        _ => $"OpenCode respondió HTTP {(int)status}. Revisa el servidor y su configuración."
    };

    private sealed record HealthResponse(bool Healthy, string? Version);
    private sealed record AgentInfo(string Name);
    private sealed record SessionInfo(string Id);
    private sealed record TokenUsage(long? Input, long? Output);
    private sealed record MessageInfo([property: JsonPropertyName("modelID")] string? ModelId, [property: JsonPropertyName("providerID")] string? ProviderId, TokenUsage? Tokens);
    private sealed record MessagePart(string? Type, string? Text);
    private sealed record MessageResponse(MessageInfo? Info, MessagePart[]? Parts);
    private sealed record GeneratedAnswer(string? Answer, int[]? Citations);
    private sealed record ProviderListResponse(ProviderResponse[]? All, string[]? Connected, Dictionary<string, string>? Default);
    private sealed record ProviderResponse(string? Id, string? Name, string? Source, ProviderOptions? Options, Dictionary<string, ModelResponse>? Models);
    private sealed record ProviderOptions([property: JsonPropertyName("baseURL")] string? BaseUrl);
    private sealed record ModelResponse(string? Id, string? Name, ModelCost? Cost, string? Status);
    private sealed record ModelCost(decimal? Input, decimal? Output);
}

/// <summary>Modelo descubierto en el servidor OpenCode local.</summary>
public sealed record OpenCodeModel(string Id, string Name, decimal InputCost, decimal OutputCost, string? Status);

/// <summary>Proveedor descubierto en el servidor OpenCode local.</summary>
public sealed record OpenCodeProvider(string Id, string Name, string? BaseUrl, IReadOnlyList<OpenCodeModel> Models);

/// <summary>Catálogo completo: proveedores con sus modelos y los que están conectados.</summary>
public sealed record OpenCodeCatalog(IReadOnlyList<OpenCodeProvider> Providers, IReadOnlyList<string> Connected);
