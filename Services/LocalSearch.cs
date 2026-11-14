using System.Text.RegularExpressions;
using DocuSearch.Models;

namespace DocuSearch.Services;

public static class LocalSearch
{
    public static List<SearchHit> Search(IEnumerable<DocumentItem> documents, string query, bool exact = false, int minimum = 10,
        float[]? queryVector = null, IReadOnlyDictionary<(string DocumentId, int PageNumber), double>? externalScores = null)
    {
        var normalized = TextAnalysis.Normalize(query.Trim());
        var terms = TextAnalysis.Terms(query);
        var candidates = documents.SelectMany(document =>
        {
            var pages = document.Pages.Count > 0 ? document.Pages : [new DocumentPage(1, document.ExecutiveSummary, document.ExecutiveSummary, [])];
            return pages.Select(page =>
            {
                var metadata = TextAnalysis.Normalize($"{document.Title} {document.InternalCode} {document.Category} {string.Join(' ', document.Keywords)}");
                var content = TextAnalysis.Normalize(page.ExtractedText);
                var combined = $"{metadata} {content}";
                var lexical = normalized.Length == 0 ? 100 : exact
                    ? (combined.Contains(normalized, StringComparison.Ordinal) ? 100 : 0)
                    : terms.Length == 0 ? 0 : (int)Math.Round(100.0 * terms.Count(t => combined.Contains(t, StringComparison.Ordinal)) / terms.Length);
                var matched = terms.Count(t => combined.Contains(t, StringComparison.Ordinal));
                float? cosine = null;
                if (!exact && queryVector is { Length: > 0 } &&
                    document.PageVectors.TryGetValue(page.PageNumber, out var pageVector) &&
                    pageVector.Length == queryVector.Length)
                    cosine = Cosine(queryVector, pageVector);
                return new VectorCandidate(document, page, lexical, matched, cosine);
            });
        }).ToList();

        // Compuerta de relevancia: bge-m3 tiene una línea base de coseno alta entre textos
        // no relacionados. Solo se usa el vector si el mejor candidato supera 0.40.
        var useVector = false;
        if (!exact && queryVector is { Length: > 0 })
        {
            var similarities = candidates.Where(candidate => candidate.Cosine.HasValue)
                .Select(candidate => candidate.Cosine!.Value).ToList();
            useVector = similarities.Count > 0 && similarities.Max() >= 0.40f;
        }

        var scored = candidates.Select(candidate =>
        {
            var score = candidate.Lexical;
            // Para consultas de varias palabras se exige al menos dos términos o señal
            // vectorial; evita que una palabra común ("capital", "es") devuelva páginas.
            if (!exact && terms.Length >= 2 && candidate.Matched < 2 && !useVector) score = 0;
            if (useVector && candidate.Cosine is { } cosine)
            {
                var vectorScore = Math.Clamp((cosine - 0.30f) / 0.35f, 0f, 1f) * 100f;
                score = (int)Math.Round(score * 0.45 + vectorScore * 0.55);
                // Sin coincidencia léxica, una similitud débil no alcanza para mostrar la página.
                if (candidate.Matched == 0 && cosine < 0.45f) score = 0;
            }
            // Pinecone puede aportar páginas que el índice local no priorizó; nunca
            // reemplaza la autorización, que se aplica antes de consultar.
            if (!exact && externalScores is not null &&
                externalScores.TryGetValue((candidate.Document.Id, candidate.Page.PageNumber), out var external))
                score = Math.Max(score, (int)Math.Round(external * 100));

            var content = TextAnalysis.Normalize(candidate.Page.ExtractedText);
            var position = terms.Select(t => content.IndexOf(t, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
            var snippet = candidate.Page.ExtractedText.Length == 0 ? candidate.Document.ExecutiveSummary :
                TextAnalysis.Truncate(candidate.Page.ExtractedText[Math.Max(0, position - 80)..], 600);
            return new SearchHit(candidate.Document, candidate.Page.PageNumber, snippet, score);
        }).Where(hit => hit.Score > 0 && hit.Score >= minimum).ToList();

        // En el asistente (minimum >= 20) se descartan las colas vectoriales muy por
        // debajo del mejor resultado; evita mezclar páginas apenas relacionadas.
        if (useVector && minimum >= 20 && scored.Count > 0)
        {
            var top = scored.Max(hit => hit.Score);
            scored = scored.Where(hit => hit.Score >= Math.Max(30, (int)Math.Round(top * 0.55))).ToList();
        }

        return scored.OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Document.InternalCode).ThenBy(hit => hit.PageNumber).ToList();
    }

    private sealed record VectorCandidate(DocumentItem Document, DocumentPage Page, int Lexical, int Matched, float? Cosine);

    /// <summary>Coseno entre dos vectores; 0 si alguno es nulo o de dimensión distinta.</summary>
    public static float Cosine(float[] left, float[] right)
    {
        if (left.Length == 0 || left.Length != right.Length) return 0;
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }
        if (leftNorm <= 0 || rightNorm <= 0) return 0;
        return (float)(dot / Math.Sqrt(leftNorm * rightNorm));
    }
}

public sealed class LocalAgent(DocumentService documents, LocalSession session, OllamaClient ollama,
    AgentObservability observability, OpenCodeClient? opencode = null, AssistantRuntime? assistant = null,
    ILogger<LocalAgent>? logger = null, EmbeddingClient? embeddings = null, PineconeRetriever? pinecone = null)
{
    private bool UseOpenCode => opencode is { Enabled: true };

    public async Task<AgentAnswer> AskAsync(string query)
    {
        var trace = new AgentTrace();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var answer = await AnswerAsync(query, trace);
            trace.InputTokens = answer.InputTokens;
            trace.OutputTokens = answer.OutputTokens;
            trace.Status = answer.Sources.Count == 0 ? "no_sources" : "success";
            return answer;
        }
        catch (Exception ex)
        {
            trace.ErrorCode = AgentObservability.ErrorCode(ex);
            throw;
        }
        finally
        {
            trace.DurationMs = timer.ElapsedMilliseconds;
            await observability.RecordAsync(trace);
        }
    }

    private async Task<AgentAnswer> AnswerAsync(string query, AgentTrace trace)
    {
        ValidateQuery(query);
        var snapshot = await documents.LoadAsync();
        await documents.LogQueryAsync("AGENT_QUERY", "Consulta del asistente; contenido omitido por privacidad.");
        float[]? queryVector = null;
        if (embeddings is { Enabled: true })
            queryVector = await embeddings.TryEmbedAsync(query);
        IReadOnlyDictionary<(string DocumentId, int PageNumber), double>? externalScores = null;
        if (queryVector is not null && pinecone is { Enabled: true })
            externalScores = await pinecone.SearchAsync(queryVector, snapshot.Documents.Select(document => document.Id).ToArray());
        var extracted = AnswerFromSources(query, snapshot.Documents, session, queryVector, externalScores);
        if (assistant is not null) await assistant.EnsureLoadedAsync();
        var profile = assistant?.Active;
        var engine = profile?.Engine?.ToLowerInvariant()
            ?? (UseOpenCode ? "opencode" : ollama.Enabled ? "ollama" : "extractive");
        if (engine is not ("opencode" or "ollama") || extracted.Sources.Count == 0 || IsExactRequest(query)) return extracted;
        AgentAnswer answer;
        var generated = false;
        var engineLabel = engine == "opencode" ? "OpenCode" : "Ollama";
        try
        {
            if (engine == "opencode")
            {
                if (!UseOpenCode)
                    throw new InvalidOperationException("El motor OpenCode está seleccionado en la interfaz, pero OpenCode:Enabled está desactivado en la configuración.");
                trace.Engine = "opencode";
                trace.Model = $"{profile?.ProviderId}/{profile?.ModelId} ({profile?.Agent})";
                answer = await opencode!.GenerateAsync(query, extracted.Sources, profile?.ProviderId, profile?.ModelId, profile?.Agent);
                generated = true;
            }
            else
            {
                if (!ollama.Enabled) return extracted;
                trace.Engine = ollama.UseLangChain ? "langchain-ollama" : "ollama";
                trace.Model = ollama.Model;
                answer = await ollama.GenerateAsync(query, extracted.Sources);
                generated = true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnauthorizedAccessException)
        {
            // El motor puede fallar por cuota, timeout, reintentos agotados o circuito abierto.
            // La recuperación extractiva ya está calculada: se devuelve y se marca la consulta
            // para revisión humana en lugar de romper el asistente.
            var quota = ex as FreeQuotaExceededException;
            var escalated = ex is AgentUnavailableException;
            trace.ErrorCode = quota is not null ? "free_quota" : escalated ? "human_fallback" : AgentObservability.ErrorCode(ex);
            logger?.LogWarning(ex, "El motor {Engine} no respondió; se devuelve la respuesta extractiva{Review}.",
                engineLabel, escalated ? " y se marca para revisión humana" : "");
            answer = new AgentAnswer(
                extracted.Text + (quota is not null
                    ? "\n\n" + quota.Message + " Se muestra la respuesta extractiva con las fuentes autorizadas."
                    : escalated
                        ? "\n\nNo se pudo completar con el motor generativo tras varios intentos. Se muestra la respuesta extractiva con las fuentes autorizadas y la consulta queda marcada para revisión humana."
                        : "\n\nNo se pudo usar el motor generativo seleccionado; se muestra la respuesta extractiva con las mismas fuentes."),
                extracted.Sources,
                quota is not null
                    ? $"{engineLabel} · cuota gratis agotada · respuesta extractiva"
                    : escalated
                        ? $"{engineLabel} no disponible · revisión humana · respuesta extractiva"
                        : $"{engineLabel} no disponible · respuesta extractiva");
        }

        // AG-02: el motor propone; C# verifica antes de mostrar. Si la respuesta generada
        // cita una fuente/página inexistente, no autorizada o texto que no consta, se bloquea
        // y se devuelve la respuesta extractiva con las mismas fuentes.
        if (generated)
        {
            var verification = AgentVerifier.Verify(answer, snapshot.Documents, snapshot.Actor);
            if (!verification.IsValid)
            {
                trace.ErrorCode = "generation_or_validation";
                var reason = verification.Issues.Any(issue => issue.Code == VerificationCode.TextNotInPage)
                    ? "La respuesta generada citó texto que no consta en la página indicada."
                    : "La respuesta generada no superó la verificación de citas.";
                logger?.LogWarning("Verificador AG-02 bloqueó una respuesta generada: {Codes}.",
                    string.Join(",", verification.Issues.Select(issue => issue.Code)));
                answer = new AgentAnswer(
                    extracted.Text + "\n\n" + reason + " Se muestra la respuesta extractiva con las fuentes autorizadas.",
                    extracted.Sources,
                    $"{engineLabel} · respuesta bloqueada por verificación · extractivo");
            }
        }

        var current = await documents.LoadAsync();
        if (current.Actor.Id != snapshot.Actor.Id ||
            extracted.Sources.Any(s => current.Documents.All(d => d.Id != s.Document.Id)))
            throw new UnauthorizedAccessException("Los permisos cambiaron durante la consulta. La respuesta no se mostrará; vuelve a seleccionar tu perfil.");
        return answer;
    }

    public static bool IsExactRequest(string query) =>
        Regex.IsMatch(TextAnalysis.Normalize(query),
            @"\b(lista|listado|listar|cuantos|cuales|disponibles|pagina|extrae|entidades|montos|fechas|leyes|personas|firmantes|organizaciones|autor|departamento|clasificacion|version|ficha|metadatos)\b") ||
        Regex.IsMatch(query.Trim(), @"^[A-Z]{2,4}-[A-Z0-9]+(?:-[A-Z0-9]+){0,3}$", RegexOptions.IgnoreCase);

    public static AgentAnswer AnswerFromSources(string query, IReadOnlyList<DocumentItem> readableDocuments, LocalSession session,
        float[]? queryVector = null, IReadOnlyDictionary<(string DocumentId, int PageNumber), double>? externalScores = null)
    {
        ValidateQuery(query);
        var normalized = TextAnalysis.Normalize(query);
        var code = Regex.Match(query, @"\b(?=[A-Z0-9-]*\d)[A-Z]{2,4}-[A-Z0-9]+(?:-[A-Z0-9]+){0,3}\b", RegexOptions.IgnoreCase).Value;
        if (code.Length == 0 && Regex.IsMatch(normalized, @"\b(ese|este|anterior|sus|su)\b"))
            code = session.LastDocumentCode ?? "";
        var document = readableDocuments.FirstOrDefault(d => d.InternalCode.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (code.Length > 0 && document is null)
            return new("No hay un expediente con ese código entre las fuentes a las que tienes acceso.", []);
        if (document is not null)
        {
            session.LastDocumentCode = document.InternalCode;
            var pageMatch = Regex.Match(normalized, @"pagina\s+(\d{1,4})");
            if (pageMatch.Success)
            {
                var page = document.Pages.FirstOrDefault(p => p.PageNumber == int.Parse(pageMatch.Groups[1].Value));
                return page is null ? new("Esa página no tiene texto disponible en el expediente.", []) :
                    new(page.ExtractedText, [new(document, page.PageNumber, page.ExtractedText, 100)]);
            }
            if (Regex.IsMatch(normalized, @"\b(extrae|entidades|montos|fechas|leyes|personas|firmantes|organizaciones)\b"))
            {
                var types = new List<string>();
                if (Regex.IsMatch(normalized, @"\b(montos|dinero|importes)\b")) types.Add("MONEY");
                if (normalized.Contains("fechas")) types.Add("DATE");
                if (Regex.IsMatch(normalized, @"\b(leyes|normas)\b")) types.Add("LAW");
                if (Regex.IsMatch(normalized, @"\b(personas|firmantes)\b")) types.Add("PERSON");
                if (normalized.Contains("organizaciones")) types.Add("ORGANIZATION");
                var entities = document.Pages.SelectMany(p => p.Entities).Where(e => types.Count == 0 || types.Contains(e.Type)).ToList();
                return new(entities.Count == 0 ? "No se encontraron entidades del tipo solicitado." :
                    string.Join("\n", entities.Select(e => $"{e.Type}: {e.Text} — [{document.InternalCode}, p. {e.PageNumber}]")),
                    entities.Select(e => new SearchHit(document, e.PageNumber, e.Text, 100)).DistinctBy(h => h.PageNumber).ToList());
            }
            if (Regex.IsMatch(normalized, @"\b(resume|resumen|sintetiza|sintesis)\b"))
                return Summary(document);
            var question = Regex.Replace(query, Regex.Escape(code), "", RegexOptions.IgnoreCase);
            var matches = LocalSearch.Search([document], question, minimum: 20, queryVector: queryVector, externalScores: externalScores).Take(3).ToList();
            var metadata = $"{document.InternalCode} — {document.Title}\nAutor: {document.Author}\nDepartamento: {document.Department}\n" +
                $"Clasificación: {document.Confidentiality}\nVersión: {document.Version}\n\n{document.ExecutiveSummary}";
            return new(metadata + (matches.Count > 0 ? "\n\n" + string.Join("\n\n", matches.Select(h =>
                    $"{h.Snippet}\n[{document.InternalCode}, p. {h.PageNumber}]")) : ""),
                matches.Count > 0 ? matches : document.Pages.Select(p => new SearchHit(document, p.PageNumber, p.Summary, 100)).ToList());
        }
        if (Regex.IsMatch(normalized, @"\b(lista|listado|listar|cuantos|cuales|disponibles)\b"))
        {
            var filtered = readableDocuments.AsEnumerable();
            var category = Catalog.Categories.FirstOrDefault(c => normalized.Contains(TextAnalysis.Normalize(c).Split(' ')[0]));
            var level = Catalog.Levels.Reverse().FirstOrDefault(c => normalized.Contains(TextAnalysis.Normalize(c)));
            if (category is not null) filtered = filtered.Where(d => d.Category == category);
            if (level is not null) filtered = filtered.Where(d => d.Confidentiality == level);
            var rows = filtered.ToList();
            return new($"{rows.Count} expediente(s) accesible(s).\n" +
                string.Join("\n", rows.Select(d => $"{d.InternalCode} — {d.Title} ({d.Confidentiality})")),
                rows.Select(d => new SearchHit(d, d.Pages.FirstOrDefault()?.PageNumber ?? 1, d.ExecutiveSummary, 100)).ToList());
        }
        var quoted = Regex.Match(query, "[\"“]([^\"”]{2,})[\"”]");
        var hits = LocalSearch.Search(readableDocuments, quoted.Success ? quoted.Groups[1].Value : query, quoted.Success, 20, queryVector, externalScores).Take(5).ToList();
        if (hits.Count == 0) return new("No hay fuentes suficientes para responder. Indica un código o términos más específicos. No se ha consultado ningún servicio externo.", []);
        session.LastDocumentCode = hits[0].Document.InternalCode;
        if (Regex.IsMatch(normalized, @"\b(resume|resumen|sintetiza)\b")) return Summary(hits[0].Document);
        return new(string.Join("\n\n", hits.Select(h => $"{h.Snippet}\n[{h.Document.InternalCode}, p. {h.PageNumber}]")) +
            "\n\nRespuesta extractiva local: recuperación híbrida (léxica + vectores locales cuando el índice está disponible), sin modelo generativo.", hits);
    }

    private static void ValidateQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000)
            throw new System.ComponentModel.DataAnnotations.ValidationException("La consulta debe tener entre 1 y 2000 caracteres.");
    }

    private static AgentAnswer Summary(DocumentItem document) => new(
        $"{document.Title}\n\n{document.ExecutiveSummary}\n\nResumen extractivo del expediente; verifica sus páginas como fuente.",
        document.Pages.Select(p => new SearchHit(document, p.PageNumber, p.Summary, 100)).ToList());
}
