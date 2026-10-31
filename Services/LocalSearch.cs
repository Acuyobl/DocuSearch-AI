using System.Text.RegularExpressions;
using DocuSearch.Models;

namespace DocuSearch.Services;

public static class LocalSearch
{
    public static List<SearchHit> Search(IEnumerable<DocumentItem> documents, string query, bool exact = false, int minimum = 10)
    {
        var normalized = TextAnalysis.Normalize(query.Trim());
        var terms = TextAnalysis.Terms(query);
        return documents.SelectMany(document =>
        {
            var pages = document.Pages.Count > 0 ? document.Pages : [new DocumentPage(1, document.ExecutiveSummary, document.ExecutiveSummary, [])];
            return pages.Select(page =>
            {
                var metadata = TextAnalysis.Normalize($"{document.Title} {document.InternalCode} {document.Category} {string.Join(' ', document.Keywords)}");
                var content = TextAnalysis.Normalize(page.ExtractedText);
                var combined = $"{metadata} {content}";
                var score = normalized.Length == 0 ? 100 : exact
                    ? (combined.Contains(normalized, StringComparison.Ordinal) ? 100 : 0)
                    : terms.Length == 0 ? 0 : (int)Math.Round(100.0 * terms.Count(t => combined.Contains(t, StringComparison.Ordinal)) / terms.Length);
                var position = terms.Select(t => content.IndexOf(t, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
                var snippet = page.ExtractedText.Length == 0 ? document.ExecutiveSummary :
                    TextAnalysis.Truncate(page.ExtractedText[Math.Max(0, position - 80)..], 600);
                return new SearchHit(document, page.PageNumber, snippet, score);
            });
        }).Where(hit => hit.Score > 0 && hit.Score >= minimum).OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Document.InternalCode).ThenBy(hit => hit.PageNumber).ToList();
    }
}

public sealed class LocalAgent(DocumentService documents, LocalSession session, OllamaClient ollama,
    AgentObservability observability, OpenCodeClient? opencode = null, AssistantRuntime? assistant = null,
    ILogger<LocalAgent>? logger = null)
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
        var extracted = AnswerFromSources(query, snapshot.Documents, session);
        if (assistant is not null) await assistant.EnsureLoadedAsync();
        var profile = assistant?.Active;
        var engine = profile?.Engine?.ToLowerInvariant()
            ?? (UseOpenCode ? "opencode" : ollama.Enabled ? "ollama" : "extractive");
        if (engine is not ("opencode" or "ollama") || extracted.Sources.Count == 0 || IsExactRequest(query)) return extracted;
        AgentAnswer answer;
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
            }
            else
            {
                if (!ollama.Enabled) return extracted;
                trace.Engine = ollama.UseLangChain ? "langchain-ollama" : "ollama";
                trace.Model = ollama.Model;
                answer = await ollama.GenerateAsync(query, extracted.Sources);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnauthorizedAccessException)
        {
            // El motor puede fallar por cuota, timeout, reintentos agotados o circuito abierto.
            // La recuperación extractiva ya está calculada: se devuelve y se marca la consulta
            // para revisión humana en lugar de romper el asistente.
            var escalated = ex is AgentUnavailableException;
            trace.ErrorCode = escalated ? "human_fallback" : AgentObservability.ErrorCode(ex);
            logger?.LogWarning(ex, "El motor {Engine} no respondió; se devuelve la respuesta extractiva{Review}.",
                engineLabel, escalated ? " y se marca para revisión humana" : "");
            answer = new AgentAnswer(
                extracted.Text + (escalated
                    ? "\n\nNo se pudo completar con el motor generativo tras varios intentos. Se muestra la respuesta extractiva con las fuentes autorizadas y la consulta queda marcada para revisión humana."
                    : "\n\nNo se pudo usar el motor generativo seleccionado; se muestra la respuesta extractiva con las mismas fuentes."),
                extracted.Sources,
                escalated
                    ? $"{engineLabel} no disponible · revisión humana · respuesta extractiva"
                    : $"{engineLabel} no disponible · respuesta extractiva");
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

    public static AgentAnswer AnswerFromSources(string query, IReadOnlyList<DocumentItem> readableDocuments, LocalSession session)
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
            var matches = LocalSearch.Search([document], question, minimum: 20).Take(3).ToList();
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
        var hits = LocalSearch.Search(readableDocuments, quoted.Success ? quoted.Groups[1].Value : query, quoted.Success, 20).Take(5).ToList();
        if (hits.Count == 0) return new("No hay fuentes suficientes para responder. Indica un código o términos más específicos. No se ha consultado ningún servicio externo.", []);
        session.LastDocumentCode = hits[0].Document.InternalCode;
        if (Regex.IsMatch(normalized, @"\b(resume|resumen|sintetiza)\b")) return Summary(hits[0].Document);
        return new(string.Join("\n\n", hits.Select(h => $"{h.Snippet}\n[{h.Document.InternalCode}, p. {h.PageNumber}]")) +
            "\n\nRespuesta extractiva local: relevancia por coincidencia de términos, sin modelo generativo ni embeddings.", hits);
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
