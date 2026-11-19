using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocuSearch.Models;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services.Rag;

public sealed record RagCitation(string DocumentCode, string Title, int PageNumber, int ChunkIndex,
    double Score, string Text);

/// <param name="Outcome">Resultado normalizado (<see cref="RagOutcome"/>) para observabilidad.</param>
public sealed record RagQueryResponse(string Answer, IReadOnlyList<RagCitation> Sources, string Engine, long LatencyMs,
    string Outcome = RagOutcome.Extractive);

/// <summary>Resultados posibles de una consulta RAG, tal como se guardan en la bitácora.</summary>
public static class RagOutcome
{
    /// <summary>Respuesta generada por el motor y aceptada por la verificación AG-02.</summary>
    public const string Generated = "generated";
    /// <summary>Respuesta extractiva porque no había motor generativo activo o no se pidió generar.</summary>
    public const string Extractive = "extractive";
    /// <summary>El motor falló y se devolvió la extractiva.</summary>
    public const string EngineFallback = "engine_fallback";
    /// <summary>La respuesta generada no superó AG-02 y se devolvió la extractiva.</summary>
    public const string VerificationFailed = "verification_failed";
    /// <summary>El índice por fragmentos no aportó fuentes autorizadas.</summary>
    public const string NoCoverage = "no_coverage";
}

/// <summary>Consulta RAG completa: retrieval híbrido, augmentación, generación y verificación.</summary>
public sealed class RagQueryService(
    DocumentService documents,
    IRagRetriever retriever,
    IRagEmbedder embedder,
    AssistantRuntime assistant,
    OpenCodeClient? opencode,
    OllamaClient ollama,
    ILogger<RagQueryService> logger,
    AgentObservability? observability = null,
    IOptions<RagOptions>? options = null,
    IAiGovernance? aiGovernance = null)
{
    /// <summary>
    /// Motor informado cuando el índice por fragmentos no aporta ninguna fuente
    /// (p. ej. expedientes aún sin indexar). Quien llama puede recurrir al RAG anterior.
    /// </summary>
    public const string NoCoverageEngine = "sin-indice";
    /// <summary>Longitud máxima de la respuesta guardada en la bitácora cuando se audita el contenido.</summary>
    public const int AuditAnswerChars = 1000;

    private readonly bool auditContent = options?.Value.AuditContent ?? true;

    /// <param name="hint">Contexto de la conversación para preguntas de seguimiento; la bitácora guarda solo <paramref name="question"/>.</param>
    public async Task<RagQueryResponse> QueryAsync(string question, bool generate = true,
        CancellationToken cancellationToken = default, ConversationHint? hint = null)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > 2000)
            throw new InvalidOperationException("La consulta debe tener entre 1 y 2000 caracteres.");
        var trace = new AgentTrace { Engine = "rag-extractive" };
        var timer = Stopwatch.StartNew();
        try
        {
            var response = await AnswerAsync(question, generate, trace, timer, hint, cancellationToken);
            trace.Status = response.Outcome == RagOutcome.NoCoverage ? "no_sources" : "success";
            if (response.Outcome == RagOutcome.VerificationFailed) trace.ErrorCode = "verification_failed";
            await LogAsync(question, response);
            return response;
        }
        catch (Exception ex)
        {
            trace.Status = "error";
            trace.ErrorCode = AgentObservability.ErrorCode(ex);
            throw;
        }
        finally
        {
            trace.DurationMs = timer.ElapsedMilliseconds;
            await RecordTraceAsync(trace);
        }
    }

    private async Task<RagQueryResponse> AnswerAsync(string question, bool generate, AgentTrace trace,
        Stopwatch timer, ConversationHint? hint, CancellationToken cancellationToken)
    {
        var snapshot = await documents.LoadAsync();
        var retrievalQuery = hint?.RetrievalQuery ?? question;
        var queryVector = await embedder.EmbedAsync(retrievalQuery, cancellationToken);
        var generationQuestion = hint?.GenerationQuestion ?? question;

        // «Resumen», «análisis», «compara…»: síntesis en lenguaje natural de uno o varios documentos.
        var intent = DocumentSynthesis.Detect(question);
        if (intent.Style != AnswerStyle.Default &&
            await SynthesizeAsync(intent, snapshot, retrievalQuery, queryVector, generationQuestion, generate, trace, timer, hint, cancellationToken)
                is { } synthesis)
            return synthesis;

        IReadOnlyList<RagHit> hits = [];
        // Pregunta de seguimiento: primero se busca en el expediente del que se venía hablando.
        if (hint?.FocusDocumentCode is { } focusCode &&
            snapshot.Documents.FirstOrDefault(document => document.InternalCode.Equals(focusCode, StringComparison.OrdinalIgnoreCase)) is { } focus)
            hits = await retriever.RetrieveAsync(retrievalQuery, queryVector, [focus.Id], cancellationToken);
        if (hits.Count == 0)
            hits = await retriever.RetrieveAsync(retrievalQuery, queryVector,
                snapshot.Documents.Select(document => document.Id).ToArray(), cancellationToken);
        if (hits.Count == 0)
            return new RagQueryResponse(
                "No hay fuentes suficientes para responder. No se ha consultado ningún servicio externo.",
                [], NoCoverageEngine, timer.ElapsedMilliseconds, RagOutcome.NoCoverage);

        var sourcePairs = new List<(SearchHit Hit, RagChunk Chunk, double Score)>();
        foreach (var hit in hits)
        {
            var document = snapshot.Documents.FirstOrDefault(item => item.Id == hit.Chunk.DocumentId);
            if (document is null || document.Pages.All(page => page.PageNumber != hit.Chunk.PageNumber)) continue;
            sourcePairs.Add((new SearchHit(document, hit.Chunk.PageNumber,
                TextAnalysis.Truncate(hit.Chunk.Text, 1000), Math.Clamp((int)Math.Round(hit.Score * 100), 0, 100)), hit.Chunk, hit.Score));
        }
        // Una fuente por página, en el mismo orden que recibe el motor: así las citas [n] coinciden con la lista.
        sourcePairs = sourcePairs.DistinctBy(pair => (pair.Hit.Document.Id, pair.Hit.PageNumber)).ToList();
        if (sourcePairs.Count == 0)
            return new RagQueryResponse("No hay fuentes autorizadas para responder.", [], NoCoverageEngine,
                timer.ElapsedMilliseconds, RagOutcome.NoCoverage);
        var sourceHits = sourcePairs.Select(pair => pair.Hit).ToList();

        var extractive = string.Join("\n\n", hits.Select(hit =>
            $"{TextAnalysis.Truncate(hit.Chunk.Text, 700)}\n[{hit.Chunk.InternalCode}, p. {hit.Chunk.PageNumber}]"))
            + "\n\nRespuesta extractiva con citas verificables.";
        var (answer, outcome) = await GenerateVerifiedAsync(generationQuestion, sourceHits, AnswerStyle.Default,
            new AgentAnswer(extractive, sourceHits), generate, trace, snapshot);

        var citations = sourcePairs.Select(pair => new RagCitation(
            pair.Hit.Document.InternalCode, pair.Hit.Document.Title, pair.Hit.PageNumber,
            pair.Chunk.ChunkIndex, pair.Score, TextAnalysis.Truncate(pair.Chunk.Text, 1000))).ToList();
        return new RagQueryResponse(answer.Text, citations, answer.Provider, timer.ElapsedMilliseconds, outcome);
    }

    /// <summary>
    /// Resumen o análisis de uno o varios documentos. Elige los documentos (los nombrados, el de la
    /// conversación o los relacionados con la consulta, más los afines si se piden «relacionados»),
    /// reparte las fuentes entre ellos y pide al motor una respuesta en lenguaje natural. Sin motor,
    /// o si la verificación falla, redacta un resumen extractivo legible. Devuelve null si no
    /// encuentra documentos con texto, para seguir por la vía normal.
    /// </summary>
    private async Task<RagQueryResponse?> SynthesizeAsync(AnswerIntent intent, AppSnapshot snapshot, string retrievalQuery,
        float[] queryVector, string generationQuestion, bool generate, AgentTrace trace, Stopwatch timer,
        ConversationHint? hint, CancellationToken cancellationToken)
    {
        var readable = snapshot.Documents;
        // Para elegir documentos, el tema pesa más que la intención: «resumen completo de
        // Mesopotamia» debe buscar «Mesopotamia», no fragmentos que contengan «resumen».
        var topicQuery = DocumentSynthesis.Subject(retrievalQuery);
        var searchQuery = string.IsNullOrWhiteSpace(topicQuery) ? retrievalQuery : topicQuery;
        var searchVector = string.Equals(searchQuery, retrievalQuery, StringComparison.Ordinal)
            ? queryVector
            : await embedder.EmbedAsync(searchQuery, cancellationToken);
        DocumentItem? Find(string code) => readable.FirstOrDefault(document => document.InternalCode.Equals(code, StringComparison.OrdinalIgnoreCase));
        var targets = new List<DocumentItem>();
        var hitsByDocument = new Dictionary<string, IReadOnlyList<RagHit>>(StringComparer.Ordinal);
        var overviewTotal = 0;
        void Remember(IEnumerable<RagHit> hits)
        {
            foreach (var group in hits.GroupBy(hit => hit.Chunk.DocumentId))
                hitsByDocument[group.Key] = (hitsByDocument.GetValueOrDefault(group.Key) ?? []).Concat(group).OrderByDescending(hit => hit.Score).ToList();
        }

        targets.AddRange(intent.Codes.Select(Find).Where(document => document is not null).Select(document => document!));
        if (targets.Count == 0 && intent.Bare && hint?.FocusDocumentCode is { } focusCode && Find(focusCode) is { } focus)
            targets.Add(focus);

        if (targets.Count > 0 && !intent.Bare)
            Remember(await retriever.RetrieveAsync(searchQuery, searchVector, targets.Select(document => document.Id).ToArray(), cancellationToken));
        else if (targets.Count == 0 && !intent.Bare)
        {
            var hits = await retriever.RetrieveAsync(searchQuery, searchVector, readable.Select(document => document.Id).ToArray(), cancellationToken);
            Remember(hits);
            var ranked = DocumentSynthesis.RankDocuments(hits,
                intent.MultiDocument ? DocumentSynthesis.MaxDocuments : 3,
                intent.MultiDocument ? DocumentSynthesis.RelatedThreshold : 0.8);
            targets.AddRange(ranked.Select(id => readable.First(document => document.Id == id)));
        }
        if (targets.Count == 0 && intent.Bare)
        {
            // «Resumen» sin tema ni conversación previa: visión general de lo más reciente.
            targets.AddRange(readable.OrderByDescending(document => document.UploadDate).Take(DocumentSynthesis.MaxDocuments));
            overviewTotal = readable.Count;
        }
        if (targets.Count == 0) return null;

        // «…y sus relacionados»: añade los documentos más parecidos al primero.
        if (intent.WantsRelated && targets.Count < DocumentSynthesis.MaxDocuments && overviewTotal == 0)
        {
            var seed = targets[0];
            var seedText = TextAnalysis.Truncate($"{seed.Title}. {seed.ExecutiveSummary} {string.Join(", ", seed.Keywords)}", 1500);
            var others = readable.Where(document => targets.All(target => target.Id != document.Id)).Select(document => document.Id).ToArray();
            if (others.Length > 0)
            {
                var related = await retriever.RetrieveAsync(seedText, await embedder.EmbedAsync(seedText, cancellationToken), others, cancellationToken);
                Remember(related);
                targets.AddRange(DocumentSynthesis.RankDocuments(related, DocumentSynthesis.MaxDocuments - targets.Count, 0.75)
                    .Select(id => readable.First(document => document.Id == id)));
            }
        }

        var sources = DocumentSynthesis.SelectSources(targets, hitsByDocument);
        if (sources.Count == 0) return null;
        targets = targets.Where(document => sources.Any(source => source.Document.Id == document.Id)).ToList();

        var included = string.Join("; ", targets.Select(document => $"{document.InternalCode} ({document.Title})"));
        var question = TextAnalysis.Truncate(
            (intent.Bare && overviewTotal > 0 ? "Haz un resumen general de estos documentos." : generationQuestion) +
            $"\n\nDocumentos incluidos: {included}.", 2000);
        var label = intent.Style == AnswerStyle.Analysis ? "análisis" : "resumen";
        var extractive = new AgentAnswer(DocumentSynthesis.Compose(intent.Style, targets, sources, overviewTotal), sources,
            $"Extractivo local · {label}");
        var (answer, outcome) = await GenerateVerifiedAsync(question, sources, intent.Style, extractive, generate, trace, snapshot);

        var citations = sources.Select(source => new RagCitation(source.Document.InternalCode, source.Document.Title,
            source.PageNumber, 0, source.Score / 100d, source.Snippet)).ToList();
        return new RagQueryResponse(answer.Text, citations, answer.Provider, timer.ElapsedMilliseconds, outcome);
    }

    /// <summary>
    /// Genera con el motor activo y verifica con AG-02. Si no hay motor, falla o la verificación
    /// no pasa, devuelve la respuesta extractiva ya preparada.
    /// </summary>
    private async Task<(AgentAnswer Answer, string Outcome)> GenerateVerifiedAsync(string question, List<SearchHit> sources,
        AnswerStyle style, AgentAnswer extractive, bool generate, AgentTrace trace, AppSnapshot snapshot)
    {
        var answer = extractive;
        var outcome = RagOutcome.Extractive;
        if (generate)
        {
            await assistant.EnsureLoadedAsync();
            var profile = assistant.Active;
            var engine = profile.Engine.ToLowerInvariant();
            try
            {
                if (engine == "opencode" && opencode is not null)
                {
                    trace.Engine = "rag-opencode";
                    trace.Model = TextAnalysis.Truncate($"{profile.ProviderId}/{profile.ModelId} ({profile.Agent})", 200);
                    answer = await AiCallGuard.RunAsync(aiGovernance,
                        CallContext("opencode", profile.ProviderId, profile.ModelId, profile, style, question, sources, trace.Id),
                        () => opencode.GenerateAsync(question, sources, profile.ProviderId, profile.ModelId, profile.Agent, style));
                    outcome = RagOutcome.Generated;
                }
                else if (ollama.Enabled)
                {
                    trace.Engine = "rag-ollama";
                    trace.Model = TextAnalysis.Truncate(ollama.Model, 200);
                    answer = await AiCallGuard.RunAsync(aiGovernance,
                        CallContext("ollama", "ollama", ollama.Model, profile, style, question, sources, trace.Id),
                        () => ollama.GenerateAsync(question, sources, style));
                    outcome = RagOutcome.Generated;
                }
            }
            catch (AiQuotaExceededException quota)
            {
                // Presupuesto o cuota agotados: no se llamó al proveedor; se responde sin modelo.
                logger.LogWarning("RAG: {Code} ({Kind}).", AiQuotaExceededException.Code, quota.Kind);
                trace.ErrorCode = AgentObservability.ErrorCode(quota);
                outcome = RagOutcome.EngineFallback;
                answer = extractive with
                {
                    Text = $"{extractive.Text}\n\n{quota.Message} Se muestra la respuesta extractiva sin consultar al modelo.",
                    Provider = "extractivo · presupuesto de IA"
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not UnauthorizedAccessException)
            {
                logger.LogWarning("RAG: el motor falló ({Error}); se devuelve extractivo.", ex.GetType().Name);
                trace.ErrorCode = AgentObservability.ErrorCode(ex);
                outcome = RagOutcome.EngineFallback;
                answer = extractive;
            }
        }

        if (outcome == RagOutcome.Generated)
        {
            trace.InputTokens = answer.InputTokens;
            trace.OutputTokens = answer.OutputTokens;
            if (!AgentVerifier.Verify(answer, snapshot.Documents, snapshot.Actor).IsValid)
            {
                answer = extractive with
                {
                    Text = extractive.Text + "\n\nLa respuesta generada no superó la verificación de citas. Se muestra la extractiva.",
                    Provider = "extractivo · verificación AG-02"
                };
                outcome = RagOutcome.VerificationFailed;
            }
        }
        return (answer, outcome);
    }

    /// <summary>Contexto de consumo de IA: una función por tipo de respuesta para el desglose del panel.</summary>
    private AiCallContext CallContext(string engine, string provider, string model, AssistantProfile? profile, AnswerStyle style,
        string question, IReadOnlyList<SearchHit> sources, string traceId) =>
        new(style switch { AnswerStyle.Summary => "assistant_summary", AnswerStyle.Analysis => "assistant_analysis", _ => "assistant_rag" },
            engine, provider, model, $"assistant.rag.{style.ToString().ToLowerInvariant()}", AssistantPrompt.For(style),
            question + "\n" + string.Join("\n", sources.Take(DocumentSynthesis.MaxSources).Select(source => source.Snippet)),
            aiGovernance?.ResolvePricing(engine, provider, model, profile) ?? AiPricing.Free,
            style == AnswerStyle.Default ? 600 : 1000, nameof(AgentTrace), traceId);

    /// <summary>
    /// Detalle JSON del evento RAG_QUERY. Con <paramref name="auditContent"/> desactivado no guarda
    /// la pregunta ni la respuesta: solo un hash (para agrupar repeticiones) y la longitud.
    /// </summary>
    public static string AuditDetails(string question, RagQueryResponse response, bool auditContent)
    {
        var sources = response.Sources.Select(source => new
        {
            source.DocumentCode, source.PageNumber, source.ChunkIndex, source.Score
        }).ToList();
        double? topScore = response.Sources.Count == 0 ? null : Math.Round(response.Sources.Max(source => source.Score), 4);
        object details = auditContent
            ? new
            {
                query = question,
                answer = TextAnalysis.Truncate(response.Answer, AuditAnswerChars),
                engine = response.Engine,
                outcome = response.Outcome,
                latencyMs = response.LatencyMs,
                topScore,
                sources
            }
            : new
            {
                queryHash = QueryHash(question),
                queryLength = question.Length,
                engine = response.Engine,
                outcome = response.Outcome,
                latencyMs = response.LatencyMs,
                topScore,
                sources
            };
        return JsonSerializer.Serialize(details, Catalog.Json);
    }

    /// <summary>Hash corto y estable de la pregunta normalizada; permite contar repeticiones sin guardar el texto.</summary>
    public static string QueryHash(string question) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(RagObservatory.NormalizeQuestion(question))))[..16];

    private async Task LogAsync(string question, RagQueryResponse response)
    {
        try
        {
            await documents.LogRagQueryAsync(AuditDetails(question, response, auditContent));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("RAG: no se pudo registrar la consulta ({Error}).", ex.GetType().Name);
        }
    }

    private async Task RecordTraceAsync(AgentTrace trace)
    {
        if (observability is null) return;
        try
        {
            await observability.RecordAsync(trace);
        }
        catch (InvalidOperationException)
        {
            // AgentObservability ya registró la categoría del error; no se rompe la respuesta al usuario.
        }
    }
}
