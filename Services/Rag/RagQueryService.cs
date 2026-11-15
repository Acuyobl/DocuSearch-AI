using System.Diagnostics;
using System.Text.Json;
using DocuSearch.Models;

namespace DocuSearch.Services.Rag;

public sealed record RagCitation(string DocumentCode, string Title, int PageNumber, int ChunkIndex,
    double Score, string Text);

public sealed record RagQueryResponse(string Answer, IReadOnlyList<RagCitation> Sources, string Engine, long LatencyMs);

/// <summary>Consulta RAG completa: retrieval híbrido, augmentación, generación y verificación.</summary>
public sealed class RagQueryService(
    DocumentService documents,
    IRagRetriever retriever,
    IRagEmbedder embedder,
    AssistantRuntime assistant,
    OpenCodeClient? opencode,
    OllamaClient ollama,
    ILogger<RagQueryService> logger)
{
    /// <summary>
    /// Motor informado cuando el índice por fragmentos no aporta ninguna fuente
    /// (p. ej. expedientes aún sin indexar). Quien llama puede recurrir al RAG anterior.
    /// </summary>
    public const string NoCoverageEngine = "sin-indice";

    public async Task<RagQueryResponse> QueryAsync(string question, bool generate = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > 2000)
            throw new InvalidOperationException("La consulta debe tener entre 1 y 2000 caracteres.");
        var timer = Stopwatch.StartNew();
        var snapshot = await documents.LoadAsync();
        var queryVector = await embedder.EmbedAsync(question, cancellationToken);
        var hits = await retriever.RetrieveAsync(question, queryVector,
            snapshot.Documents.Select(document => document.Id).ToArray(), cancellationToken);
        if (hits.Count == 0)
        {
            var empty = new RagQueryResponse(
                "No hay fuentes suficientes para responder. No se ha consultado ningún servicio externo.",
                [], NoCoverageEngine, timer.ElapsedMilliseconds);
            await LogAsync(question, empty);
            return empty;
        }

        var sourcePairs = new List<(SearchHit Hit, RagChunk Chunk, double Score)>();
        foreach (var hit in hits)
        {
            var document = snapshot.Documents.FirstOrDefault(item => item.Id == hit.Chunk.DocumentId);
            if (document is null || document.Pages.All(page => page.PageNumber != hit.Chunk.PageNumber)) continue;
            sourcePairs.Add((new SearchHit(document, hit.Chunk.PageNumber,
                TextAnalysis.Truncate(hit.Chunk.Text, 1000), Math.Clamp((int)Math.Round(hit.Score * 100), 0, 100)), hit.Chunk, hit.Score));
        }
        if (sourcePairs.Count == 0)
        {
            var empty = new RagQueryResponse("No hay fuentes autorizadas para responder.", [], NoCoverageEngine, timer.ElapsedMilliseconds);
            await LogAsync(question, empty);
            return empty;
        }
        var sourceHits = sourcePairs.Select(pair => pair.Hit).ToList();

        var extractive = string.Join("\n\n", hits.Select(hit =>
            $"{TextAnalysis.Truncate(hit.Chunk.Text, 700)}\n[{hit.Chunk.InternalCode}, p. {hit.Chunk.PageNumber}]"));
        var answer = new AgentAnswer(extractive + "\n\nRespuesta extractiva con citas verificables.", sourceHits);
        var generated = false;
        if (generate)
        {
            await assistant.EnsureLoadedAsync();
            var profile = assistant.Active;
            var engine = profile.Engine.ToLowerInvariant();
            try
            {
                if (engine == "opencode" && opencode is not null)
                {
                    answer = await opencode.GenerateAsync(question, sourceHits, profile.ProviderId, profile.ModelId, profile.Agent);
                    generated = true;
                }
                else if (ollama.Enabled)
                {
                    answer = await ollama.GenerateAsync(question, sourceHits);
                    generated = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not UnauthorizedAccessException)
            {
                logger.LogWarning("RAG: el motor falló ({Error}); se devuelve extractivo.", ex.GetType().Name);
            }
        }

        if (generated)
        {
            var verification = AgentVerifier.Verify(answer, snapshot.Documents, snapshot.Actor);
            if (!verification.IsValid)
            {
                answer = new AgentAnswer(extractive + "\n\nLa respuesta generada no superó la verificación de citas. Se muestra la extractiva.",
                    sourceHits, "extractivo · verificación AG-02");
            }
        }

        var citations = sourcePairs.Select(pair => new RagCitation(
            pair.Hit.Document.InternalCode, pair.Hit.Document.Title, pair.Hit.PageNumber,
            pair.Chunk.ChunkIndex, pair.Score, TextAnalysis.Truncate(pair.Chunk.Text, 1000))).ToList();
        var response = new RagQueryResponse(answer.Text, citations, answer.Provider, timer.ElapsedMilliseconds);
        await LogAsync(question, response);
        return response;
    }

    private async Task LogAsync(string question, RagQueryResponse response)
    {
        try
        {
            var details = JsonSerializer.Serialize(new
            {
                query = question,
                answer = response.Answer,
                engine = response.Engine,
                latencyMs = response.LatencyMs,
                sources = response.Sources.Select(source => new
                {
                    source.DocumentCode, source.PageNumber, source.ChunkIndex, source.Score
                })
            }, Catalog.Json);
            await documents.LogRagQueryAsync(details);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("RAG: no se pudo registrar la consulta ({Error}).", ex.GetType().Name);
        }
    }
}
