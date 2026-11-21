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
    public string ProviderId { get; set; } = "deepseek";
    public string ModelId { get; set; } = "deepseek-flash";
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>Permite un servidor OpenCode fuera de loopback (p. ej. host.docker.internal).</summary>
    public bool AllowNonLoopback { get; set; }
    /// <summary>Orquesta la generación con LangChain (Python) usando OpenCode como proveedor de modelo.</summary>
    public bool UseLangChain { get; set; }
    /// <summary>Python del entorno LangChain (se usa solo cuando UseLangChain=true).</summary>
    public string PythonExecutable { get; set; } = @"%LOCALAPPDATA%\DocuSearch\LangChain\.venv\Scripts\python.exe";
}

/// <summary>
/// Motor del asistente basado en el servidor HTTP de OpenCode (`opencode serve`).
/// Crea una sesión por consulta, envía pregunta + fuentes autorizadas a un agente sin
/// herramientas y valida la respuesta estructurada antes de mostrarla.
/// </summary>
public sealed class OpenCodeClient
{
    private static readonly string[] LocalProviders = ["lmstudio", "llamacpp"];
    private readonly HttpClient http;
    private readonly OpenCodeOptions settings;
    private readonly AgentResilience resilience;

    public bool Enabled => settings.Enabled;
    public string Agent => settings.Agent;
    public string Model => $"{settings.ProviderId}/{settings.ModelId}";
    public string Engine => UseLangChain ? $"LangChain · OpenCode · {Model}" : $"OpenCode · {Model}";
    public bool IsRemote => !LocalProviders.Contains(settings.ProviderId, StringComparer.OrdinalIgnoreCase);
    public bool UseLangChain => settings.UseLangChain;

    public OpenCodeClient(HttpClient http, IOptions<OpenCodeOptions> options, AgentResilience? resilience = null)
    {
        settings = options.Value;
        this.resilience = resilience ?? new AgentResilience(Options.Create(new ResilienceOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentResilience>.Instance);
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

    /// <summary>Comprueba que el servidor OpenCode 2 responde y, si se indicó un agente, que existe.</summary>
    public async Task CheckAsync(string? agent = null)
    {
        if (!Enabled) return;
        var expectedAgent = string.IsNullOrWhiteSpace(agent) ? settings.Agent : agent;
        await EnsureV2Async(CancellationToken.None);
        if (settings.UseLangChain) await CheckLangChainAsync();
        if (string.IsNullOrWhiteSpace(expectedAgent)) return;
        var v2Agents = await GetAsync<V2AgentList>("api/agent", CancellationToken.None);
        if ((v2Agents.Data ?? []).All(candidate => !string.Equals(candidate.Id, expectedAgent, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"El servidor OpenCode 2 no tiene el agente «{expectedAgent}». Revísalo en opencode.json.");
    }

    /// <summary>Valida que el servidor habla la API de OpenCode 2 (/api/info).</summary>
    private async Task EnsureV2Async(CancellationToken cancellationToken)
    {
        try
        {
            using var info = await http.GetAsync("api/info", cancellationToken);
            if (info.StatusCode == HttpStatusCode.Unauthorized)
                throw new InvalidOperationException(DescribeHttpFailure(info.StatusCode));
            if (!info.IsSuccessStatusCode)
                throw new InvalidOperationException("El servidor no respondió como OpenCode 2. Inicia la versión 2 de OpenCode y revisa OpenCode:BaseUrl.");
            var text = await info.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    (document.RootElement.TryGetProperty("version", out _) || document.RootElement.TryGetProperty("urls", out _)))
                    return;
            }
            catch (JsonException) { }

            // Algunas versiones del servidor devuelven la UI web en /api/info. La API v2
            // sigue disponible en /api/agent; se valida ahí antes de declarar incompatible.
            using var agents = await http.GetAsync("api/agent", cancellationToken);
            if (!agents.IsSuccessStatusCode)
                throw new InvalidOperationException("El servidor no respondió como OpenCode 2. Inicia la versión 2 de OpenCode y revisa OpenCode:BaseUrl.");
            try
            {
                using var agentDocument = JsonDocument.Parse(await agents.Content.ReadAsStringAsync(cancellationToken));
                if (agentDocument.RootElement.ValueKind == JsonValueKind.Object &&
                    agentDocument.RootElement.TryGetProperty("data", out var agentData) &&
                    agentData.ValueKind == JsonValueKind.Array)
                    return;
            }
            catch (JsonException) { }
            throw new InvalidOperationException("El servidor respondió pero no es compatible con OpenCode 2. Revisa la versión del CLI/servidor OpenCode y OpenCode:BaseUrl.");
        }
        catch (HttpRequestException ex)
        {
            throw new TransientAgentException($"No se pudo conectar con el servidor OpenCode 2 en {settings.BaseUrl}. Inicia «opencode serve» y revisa OpenCode:Password.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientAgentException($"OpenCode 2 superó el tiempo de espera de {settings.TimeoutSeconds} segundos.", ex);
        }
    }

    /// <summary>Catálogo de proveedores/modelos de OpenCode 2 (para el selector de motores).</summary>
    public async Task<OpenCodeCatalog> FetchCatalogAsync()
    {
        if (!Enabled) throw new InvalidOperationException("OpenCode está desactivado en la configuración.");
        await EnsureV2Async(CancellationToken.None);
        var providers = await GetAsync<V2ProviderList>("api/provider", CancellationToken.None);
        var models = await GetAsync<V2ModelList>("api/model", CancellationToken.None);
        var providerInfos = providers.Data ?? [];
        var modelInfos = models.Data ?? [];
        var connected = providerInfos
            .Where(provider => !string.IsNullOrWhiteSpace(provider.Id))
            .Select(provider => provider.Id!)
            .ToArray();
        var result = providerInfos
            .Where(provider => !string.IsNullOrWhiteSpace(provider.Id))
            .Select(provider => new OpenCodeProvider(
                provider.Id!,
                string.IsNullOrWhiteSpace(provider.Name) ? provider.Id! : provider.Name!,
                provider.Settings?.BaseUrl,
                modelInfos
                    .Where(model => string.Equals(model.ProviderID, provider.Id, StringComparison.OrdinalIgnoreCase) && model.Enabled != false)
                    .Select(model =>
                    {
                        var cost = model.Cost?.FirstOrDefault();
                        return new OpenCodeModel(
                            model.ModelID ?? model.Id ?? "",
                            string.IsNullOrWhiteSpace(model.Name) ? (model.ModelID ?? model.Id ?? "") : model.Name!,
                            cost?.Input ?? 0,
                            cost?.Output ?? 0,
                            model.Status);
                    })
                    .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                    .ToList()))
            .ToList();
        if (result.Count == 0)
            throw new InvalidOperationException("OpenCode 2 no devolvió proveedores activos.");
        return new OpenCodeCatalog(result, connected);
    }

    public Task<AgentAnswer> GenerateAsync(string query, IReadOnlyList<SearchHit> sources) =>
        GenerateAsync(query, sources, settings.ProviderId, settings.ModelId, settings.Agent);

    public Task<AgentAnswer> GenerateAsync(string query, IReadOnlyList<SearchHit> sources, string? providerId, string? modelId, string? agent,
        AnswerStyle style = AnswerStyle.Default) =>
        resilience.ExecuteAsync("opencode.generate",
            cancellationToken => GenerateOnceAsync(query, sources, providerId, modelId, agent, style, cancellationToken));

    private async Task<AgentAnswer> GenerateOnceAsync(string query, IReadOnlyList<SearchHit> sources,
        string? providerId, string? modelId, string? agent, AnswerStyle style, CancellationToken cancellationToken)
    {
        if (!Enabled) throw new InvalidOperationException("La generación con OpenCode está desactivada.");
        var provider = string.IsNullOrWhiteSpace(providerId) ? settings.ProviderId : providerId;
        var model = string.IsNullOrWhiteSpace(modelId) ? settings.ModelId : modelId;
        var agentName = string.IsNullOrWhiteSpace(agent) ? settings.Agent : agent;
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000)
            throw new InvalidOperationException("La consulta debe tener entre 1 y 2000 caracteres.");
        if (sources.Count == 0) throw new InvalidOperationException("No hay fuentes autorizadas para consultar al modelo.");

        var selected = sources.DistinctBy(source => (source.Document.Id, source.PageNumber)).Take(6).ToList();
        await CheckAsync(agentName);
        if (settings.UseLangChain)
            return await GenerateWithLangChainAsync(query, selected, provider, model, agentName, style, cancellationToken);

        return await GenerateV2Async(query, selected, provider, model, agentName, style, cancellationToken);
    }

    private AgentAnswer ParseAnswer(string text, IReadOnlyList<SearchHit> selected, string provider, string model, TokenUsage? tokens)
    {
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
        if (string.IsNullOrWhiteSpace(generated.Answer) || generated.Answer.Length > 10000)
            throw new InvalidOperationException("OpenCode devolvió contenido vacío o citas fuera de las fuentes autorizadas. La respuesta se ha bloqueado.");
        if (!AgentVerifier.TryNormalizeCitations(generated.Citations, selected.Count, out var citations, out _))
            throw new InvalidOperationException("OpenCode devolvió contenido vacío o citas fuera de las fuentes autorizadas. La respuesta se ha bloqueado.");
        if (tokens is { Input: < 0 } or { Output: < 0 })
            throw new InvalidOperationException("OpenCode devolvió contadores de tokens inválidos.");
        return new(generated.Answer.Trim(), citations.Select(id => selected[id - 1]).ToList(),
            $"OpenCode · {provider}/{model}", tokens?.Input, tokens?.Output);
    }

    /// <summary>
    /// Generación contra la API de OpenCode 2: crea sesión con permisos denegados (sin herramientas),
    /// envía el prompt, espera el mensaje del asistente y valida las citas en C#.
    /// </summary>
    private async Task<AgentAnswer> GenerateV2Async(string query, IReadOnlyList<SearchHit> selected,
        string provider, string model, string agentName, AnswerStyle style, CancellationToken cancellationToken)
    {
        var context = selected.Select((source, index) => new
        {
            id = index + 1,
            code = source.Document.InternalCode,
            title = source.Document.Title,
            page = source.PageNumber,
            text = TextAnalysis.Truncate(source.Snippet, 1000)
        }).ToArray();
        var payload = JsonSerializer.Serialize(new { question = query, sources = context }, Catalog.Json);
        var body = new Dictionary<string, object?>
        {
            ["title"] = $"DocuSearch AI · {DateTimeOffset.UtcNow:u}",
            ["model"] = new { id = model, providerID = provider },
            ["permissions"] = new[] { new { action = "*", resource = "*", effect = "deny" } }
        };
        if (!string.IsNullOrWhiteSpace(agentName)) body["agent"] = agentName;

        var created = await PostAsync<V2SessionCreated>("api/session", body, cancellationToken);
        var sessionId = created?.Data?.Id;
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new InvalidOperationException("OpenCode 2 no devolvió el identificador de sesión.");
        try
        {
            await PostAsync<V2PromptAck>($"api/session/{Uri.EscapeDataString(sessionId)}/prompt",
                new { text = AssistantPrompt.For(style) + "\n\n" + payload }, cancellationToken);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(settings.TimeoutSeconds);
            string? answerText = null;
            TokenUsage? tokens = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(500, cancellationToken);
                var messages = await GetAsync<V2MessageList>(
                    $"api/session/{Uri.EscapeDataString(sessionId)}/message?order=asc&limit=100", cancellationToken);
                var assistant = (messages.Data ?? [])
                    .Where(message => string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase))
                    .LastOrDefault();
                if (assistant is null) continue;
                var text = string.Concat((assistant.Content ?? [])
                    .Where(part => string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(part.Text))
                    .Select(part => part.Text!));
                if (!string.IsNullOrWhiteSpace(text)) answerText = text;
                if (assistant.Tokens is { } usage) tokens = new TokenUsage(usage.Input, usage.Output);
                var finished = !string.IsNullOrWhiteSpace(assistant.Finish) || assistant.Time?.Completed is not null;
                if (finished && assistant.Error is { } error && !string.IsNullOrWhiteSpace(error.Message))
                    throw new InvalidOperationException("OpenCode 2 devolvió un error: " + error.Message);
                if (!string.IsNullOrWhiteSpace(answerText) && finished) break;
            }
            if (string.IsNullOrWhiteSpace(answerText))
                throw new AgentUnavailableException($"OpenCode no terminó la consulta en {settings.TimeoutSeconds} segundos.");
            return ParseAnswer(answerText, selected, provider, model, tokens);
        }
        finally
        {
            await DeleteSessionAsync(sessionId, cancellationToken);
        }
    }

    private async Task<AgentAnswer> GenerateWithLangChainAsync(string query, IReadOnlyList<SearchHit> selected,
        string provider, string model, string agentName, AnswerStyle style, CancellationToken cancellationToken)
    {
        var sources = selected.Select((source, index) => new
        {
            id = index + 1,
            code = source.Document.InternalCode,
            title = source.Document.Title,
            page = source.PageNumber,
            text = TextAnalysis.Truncate(source.Snippet, 1000)
        }).ToArray();
        var request = new
        {
            base_url = settings.BaseUrl,
            username = settings.Username,
            password = settings.Password,
            provider_id = provider,
            model_id = model,
            agent = agentName,
            timeout = settings.TimeoutSeconds,
            question = query,
            sources,
            system = AssistantPrompt.For(style)
        };
        var raw = await LangChainRunner.RunAsync(settings.PythonExecutable, "opencode_worker.py", settings.TimeoutSeconds, request);
        LangChainAnswer generated;
        try
        {
            generated = JsonSerializer.Deserialize<LangChainAnswer>(raw, Catalog.Json)
                ?? throw new InvalidOperationException("LangChain devolvió una respuesta vacía.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("LangChain devolvió una respuesta sin el formato de citas requerido.", ex);
        }
        if (string.IsNullOrWhiteSpace(generated.Answer) || generated.Answer.Length > 10000)
            throw new InvalidOperationException("LangChain devolvió contenido vacío o citas fuera de las fuentes autorizadas. La respuesta se ha bloqueado.");
        if (!AgentVerifier.TryNormalizeCitations(generated.Citations, selected.Count, out var citations, out _))
            throw new InvalidOperationException("LangChain devolvió contenido vacío o citas fuera de las fuentes autorizadas. La respuesta se ha bloqueado.");
        if (generated.InputTokens is < 0 || generated.OutputTokens is < 0)
            throw new InvalidOperationException("LangChain devolvió contadores de tokens inválidos.");
        return new(generated.Answer.Trim(), citations.Select(id => selected[id - 1]).ToList(),
            $"LangChain · OpenCode · {provider}/{model}", generated.InputTokens, generated.OutputTokens);
    }

    private async Task CheckLangChainAsync()
    {
        var raw = await LangChainRunner.RunAsync(settings.PythonExecutable, "opencode_worker.py", settings.TimeoutSeconds);
        using var check = JsonDocument.Parse(raw);
        if (check.RootElement.GetProperty("status").GetString() != "ready" ||
            check.RootElement.GetProperty("framework").GetString() != "LangChain")
            throw new InvalidOperationException("El worker de LangChain/OpenCode no confirmó su disponibilidad. Comprueba Python, las dependencias y el servidor OpenCode.");
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.DeleteAsync($"api/session/{Uri.EscapeDataString(sessionId)}", cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // La limpieza de la sesión es best-effort; no debe ocultar la respuesta.
        }
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.GetAsync(path, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw HttpFailure(response.StatusCode);
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("OpenCode respondió sin datos.");
        }
        catch (HttpRequestException ex)
        {
            throw new TransientAgentException($"No se pudo conectar con el servidor OpenCode en {settings.BaseUrl}. Inicia «opencode serve» y revisa OpenCode:Password.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientAgentException($"OpenCode superó el tiempo de espera de {settings.TimeoutSeconds} segundos.", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("El servidor OpenCode no devolvió una respuesta JSON válida.", ex);
        }
    }

    private async Task<T?> PostAsync<T>(string path, object payload, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(path, payload, Catalog.Json, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw HttpFailure(response.StatusCode);
            return await response.Content.ReadFromJsonAsync<T>(Catalog.Json, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new TransientAgentException($"No se pudo conectar con el servidor OpenCode en {settings.BaseUrl}. Inicia «opencode serve» y revisa OpenCode:Password.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientAgentException($"OpenCode superó el tiempo de espera de {settings.TimeoutSeconds} segundos. El modelo remoto puede necesitar más margen.", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("El servidor OpenCode no devolvió una respuesta JSON válida.", ex);
        }
    }

    private Exception HttpFailure(HttpStatusCode status)
    {
        var message = DescribeHttpFailure(status);
        return status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500
            ? new TransientAgentException(message)
            : new InvalidOperationException(message);
    }

    private string DescribeHttpFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "OpenCode rechazó las credenciales (HTTP 401). Configura OpenCode:Password con el valor de OPENCODE_SERVER_PASSWORD.",
        HttpStatusCode.NotFound => $"OpenCode no encontró el recurso solicitado (HTTP 404). Revisa el agente «{settings.Agent}» en opencode.json.",
        _ => $"OpenCode respondió HTTP {(int)status}. Revisa el servidor y su configuración."
    };

    private sealed record TokenUsage(long? Input, long? Output);
    private sealed record GeneratedAnswer(string? Answer, int[]? Citations);
    private sealed record LangChainAnswer(string? Answer, int[]? Citations,
        [property: JsonPropertyName("input_tokens")] int? InputTokens,
        [property: JsonPropertyName("output_tokens")] int? OutputTokens);

    // --- OpenCode 2 (/api/...) -------------------------------------------------
    private sealed record V2ServerInfo(string? Version);
    private sealed record V2AgentList(V2Agent[]? Data);
    private sealed record V2Agent(string? Id);
    private sealed record V2SessionCreated(V2SessionData? Data);
    private sealed record V2SessionData(string? Id);
    private sealed record V2PromptAck(object? Data);
    private sealed record V2ProviderList(V2ProviderInfo[]? Data);
    private sealed record V2ProviderInfo(string? Id, string? Name, V2ProviderSettings? Settings);
    private sealed record V2ProviderSettings([property: JsonPropertyName("baseURL")] string? BaseUrl);
    private sealed record V2ModelList(V2ModelInfo[]? Data);
    private sealed record V2ModelInfo(string? Id, string? ModelID, string? ProviderID, string? Name,
        V2CostInfo[]? Cost, string? Status, bool? Enabled);
    private sealed record V2CostInfo(decimal? Input, decimal? Output);
    private sealed record V2MessageList(V2Message[]? Data);
    private sealed record V2Message(string? Type, V2MessagePart[]? Content, V2TokensInfo? Tokens,
        string? Finish, V2MessageTime? Time, V2MessageError? Error);
    private sealed record V2MessagePart(string? Type, string? Text);
    private sealed record V2TokensInfo(long? Input, long? Output);
    private sealed record V2MessageTime(long? Completed);
    private sealed record V2MessageError(string? Message);
}

/// <summary>Modelo descubierto en el servidor OpenCode local.</summary>
public sealed record OpenCodeModel(string Id, string Name, decimal InputCost, decimal OutputCost, string? Status);

/// <summary>Proveedor descubierto en el servidor OpenCode local.</summary>
public sealed record OpenCodeProvider(string Id, string Name, string? BaseUrl, IReadOnlyList<OpenCodeModel> Models);

/// <summary>Catálogo completo: proveedores con sus modelos y los que están conectados.</summary>
public sealed record OpenCodeCatalog(IReadOnlyList<OpenCodeProvider> Providers, IReadOnlyList<string> Connected);
