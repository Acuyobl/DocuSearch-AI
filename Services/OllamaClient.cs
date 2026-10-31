using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocuSearch.Models;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services;

public sealed class OllamaOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "qwen2.5-coder:7b";
    public int TimeoutSeconds { get; set; } = 240;
    public bool UseLangChain { get; set; }
    public string PythonExecutable { get; set; } = @"%LOCALAPPDATA%\DocuSearch\LangChain\.venv\Scripts\python.exe";
}

public sealed class OllamaClient
{
    private readonly HttpClient http;
    private readonly OllamaOptions settings;
    public bool Enabled => settings.Enabled;
    public string Model => settings.Model;
    public bool UseLangChain => settings.UseLangChain;
    public string Provider => settings.UseLangChain ? "LangChain · Ollama local" : "Ollama local";

    public OllamaClient(HttpClient http, IOptions<OllamaOptions> options)
    {
        settings = options.Value;
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") ||
            !IPAddress.TryParse(endpoint.DnsSafeHost, out var address) || !IPAddress.IsLoopback(address) ||
            endpoint.AbsolutePath != "/" || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            throw new InvalidOperationException("Ollama:BaseUrl debe ser una dirección IP de loopback, por ejemplo http://127.0.0.1:11434. No se permiten servidores externos.");
        if (string.IsNullOrWhiteSpace(settings.Model) || settings.Model.Contains("cloud", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Configura un modelo Ollama descargado localmente; los modelos cloud no están permitidos.");
        if (settings.TimeoutSeconds is < 1 or > 600)
            throw new InvalidOperationException("Ollama:TimeoutSeconds debe estar entre 1 y 600.");
        this.http = http;
        http.BaseAddress = endpoint;
        http.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        http.MaxResponseContentBufferSize = 2 * 1024 * 1024;
    }

    /// <summary>Comprobación ligera para el catálogo: ¿responde Ollama en su endpoint?</summary>
    public async Task<bool> IsReachableAsync()
    {
        if (!Enabled) return false;
        try
        {
            using var response = await http.GetAsync("api/tags");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task CheckAsync()
    {
        if (!Enabled) return;
        var model = await PostAsync<ModelDetails>("api/show", new { model = Model });
        if (!string.IsNullOrEmpty(model.RemoteHost) || !string.IsNullOrEmpty(model.RemoteModel) ||
            model.Details?.Format != "gguf" || model.Capabilities?.Contains("completion") != true)
            throw new InvalidOperationException("Ollama no confirmó un modelo generativo local en formato GGUF. Se bloqueó el envío de documentos.");
        if (settings.UseLangChain)
        {
            using var check = JsonDocument.Parse(await LangChainRunner.RunAsync(settings));
            if (check.RootElement.GetProperty("status").GetString() != "ready" ||
                check.RootElement.GetProperty("framework").GetString() != "LangChain")
                throw new InvalidOperationException("El worker de LangChain no confirmó su disponibilidad.");
        }
    }

    public async Task<AgentAnswer> GenerateAsync(string query, IReadOnlyList<SearchHit> sources)
    {
        if (!Enabled) throw new InvalidOperationException("La generación con Ollama está desactivada.");
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000)
            throw new InvalidOperationException("La consulta debe tener entre 1 y 2000 caracteres.");
        if (sources.Count == 0) throw new InvalidOperationException("No hay fuentes autorizadas para consultar al modelo.");
        await CheckAsync();
        var selected = sources.DistinctBy(s => (s.Document.Id, s.PageNumber)).Take(6).ToList();
        var context = selected.Select((source, index) => new
        {
            id = index + 1,
            code = source.Document.InternalCode,
            title = source.Document.Title,
            page = source.PageNumber,
            text = TextAnalysis.Truncate(source.Snippet, 1000)
        }).ToArray();
        const string systemPrompt = AssistantPrompt.System;
        var response = settings.UseLangChain
            ? new ChatResponse(true, new ChatMessage(await LangChainRunner.RunAsync(settings, new
            {
                base_url = settings.BaseUrl, model = Model, timeout = settings.TimeoutSeconds,
                question = query, sources = context, system = systemPrompt
            })), "stop")
            : await PostAsync<ChatResponse>("api/chat", new
        {
            model = Model,
            stream = false,
            keep_alive = "5m",
            options = new { temperature = 0, num_ctx = 8192, num_predict = 600 },
            format = new
            {
                type = "object",
                properties = new
                {
                    answer = new { type = "string" },
                    citations = new { type = "array", items = new { type = "integer" } }
                },
                required = new[] { "answer", "citations" },
                additionalProperties = false
            },
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = JsonSerializer.Serialize(new { question = query, sources = context }, Catalog.Json) }
            }
        });
        if (!response.Done || response.DoneReason == "length" || string.IsNullOrWhiteSpace(response.Message?.Content))
            throw new InvalidOperationException("Ollama no completó la respuesta. Prueba una pregunta más específica.");
        GeneratedAnswer generated;
        try
        {
            generated = JsonSerializer.Deserialize<GeneratedAnswer>(response.Message.Content, Catalog.Json)
                ?? throw new InvalidOperationException("Ollama devolvió una respuesta vacía.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Ollama devolvió una respuesta sin el formato de citas requerido. No se ha mostrado como válida.", ex);
        }
        if (string.IsNullOrWhiteSpace(generated.Answer) || generated.Answer.Length > 10000 ||
            generated.Citations is not { Length: > 0 } || generated.Citations.Any(id => id < 1 || id > selected.Count))
            throw new InvalidOperationException("Ollama devolvió contenido vacío o citas fuera de las fuentes autorizadas. La respuesta se ha bloqueado.");
        var inputTokens = settings.UseLangChain ? generated.InputTokens : response.InputTokens;
        var outputTokens = settings.UseLangChain ? generated.OutputTokens : response.OutputTokens;
        if (inputTokens < 0 || outputTokens < 0)
            throw new InvalidOperationException("El modelo devolvió contadores de tokens inválidos.");
        return new(generated.Answer.Trim(), generated.Citations.Distinct().Select(id => selected[id - 1]).ToList(),
            $"{Provider} · {Model}", inputTokens, outputTokens);
    }

    private async Task<T> PostAsync<T>(string path, object payload)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(path, payload);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Ollama respondió HTTP {(int)response.StatusCode}. Comprueba que está activo y que el modelo «{Model}» está instalado.");
            return await response.Content.ReadFromJsonAsync<T>()
                ?? throw new InvalidOperationException("Ollama respondió sin datos.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("No se pudo conectar con Ollama local. Abre Ollama y vuelve a comprobar la conexión. No se ha utilizado ningún proveedor remoto.", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new InvalidOperationException($"Ollama superó el tiempo de espera de {settings.TimeoutSeconds} segundos. La primera carga del modelo puede tardar; prueba de nuevo con una consulta más breve.", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("El servidor configurado no devolvió una respuesta JSON válida de Ollama.", ex);
        }
    }

    private sealed record ModelDetails(
        LocalModelDetails? Details, string[]? Capabilities,
        [property: JsonPropertyName("remote_host")] string? RemoteHost,
        [property: JsonPropertyName("remote_model")] string? RemoteModel);
    private sealed record LocalModelDetails(string? Format);
    private sealed record ChatResponse(bool Done, ChatMessage? Message, [property: JsonPropertyName("done_reason")] string? DoneReason,
        [property: JsonPropertyName("prompt_eval_count")] long? InputTokens = null,
        [property: JsonPropertyName("eval_count")] long? OutputTokens = null);
    private sealed record ChatMessage(string? Content);
    private sealed record GeneratedAnswer(string? Answer, int[]? Citations,
        [property: JsonPropertyName("input_tokens")] long? InputTokens = null,
        [property: JsonPropertyName("output_tokens")] long? OutputTokens = null);
}
