using System.Text.Json;
using DocuSearch.Models;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services.Rag;

/// <summary>
/// Integración LangGraph para resumen/análisis. Recibe fuentes ya autorizadas, llama a
/// un worker Python aislado y vuelve a aplicar AG-02 en C#. Nunca escribe documentos.
/// </summary>
public sealed class LangGraphRagService(
    IOptions<AssistantOptions> assistantOptions,
    IOptions<OllamaOptions> ollamaOptions,
    ILogger<LangGraphRagService> logger)
{
    private readonly AssistantOptions settings = assistantOptions.Value;
    private readonly OllamaOptions ollama = ollamaOptions.Value;

    public bool Enabled => settings.UseLangGraph && ollama.Enabled;

    public async Task<AgentAnswer?> TryGenerateAsync(string question, IReadOnlyList<SearchHit> sources,
        AppSnapshot snapshot, AnswerStyle style, CancellationToken cancellationToken)
    {
        if (!Enabled || sources.Count == 0 || string.IsNullOrWhiteSpace(question)) return null;
        var selected = sources.Take(Math.Clamp(settings.LangGraphMaxSources, 1, 6)).ToList();
        var request = new
        {
            base_url = ollama.BaseUrl,
            model = ollama.Model,
            timeout = Math.Clamp(settings.LangGraphTimeoutSeconds, 5, 600),
            question,
            sources = selected.Select((source, index) => new
            {
                id = index + 1,
                code = source.Document.InternalCode,
                title = source.Document.Title,
                page = source.PageNumber,
                text = TextAnalysis.Truncate(source.Snippet, 1500)
            }).ToArray(),
            system = AssistantPrompt.For(style),
            max_sources = selected.Count,
            max_retries = Math.Clamp(settings.LangGraphMaxRetries, 0, 2)
        };

        try
        {
            var raw = await LangChainRunner.RunAsync(ollama.PythonExecutable, "langgraph_worker.py",
                request.timeout, request);
            var result = JsonSerializer.Deserialize<LangGraphResponse>(raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (result?.Status != "ok" || string.IsNullOrWhiteSpace(result.Answer) ||
                result.Citations is not { Count: > 0 }) return null;
            var cited = result.Citations.Distinct().Where(id => id >= 1 && id <= selected.Count)
                .Select(id => selected[id - 1]).ToList();
            if (cited.Count == 0) return null;
            var answer = new AgentAnswer(result.Answer, cited,
                $"LangGraph · Ollama local · {ollama.Model}", null, null);
            if (!AgentVerifier.Verify(answer, snapshot.Documents, snapshot.Actor).IsValid)
            {
                logger.LogWarning("LangGraph: AG-02 rechazó la respuesta; se usará el camino anterior.");
                return null;
            }
            return answer;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnauthorizedAccessException)
        {
            logger.LogWarning("LangGraph no disponible ({Error}): {Message}; se usa el camino anterior.",
                ex.GetType().Name, TextAnalysis.Truncate(ex.Message, 600));
            return null;
        }
    }

    private sealed record LangGraphResponse(string? Status, string? Answer, List<int>? Citations,
        List<string>? Steps, LangGraphUsage? Usage);
    private sealed record LangGraphUsage(int? ModelCalls);
}
