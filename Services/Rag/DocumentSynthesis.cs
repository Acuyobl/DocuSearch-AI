using System.Text.RegularExpressions;
using DocuSearch.Models;

namespace DocuSearch.Services.Rag;

/// <summary>Qué pide la pregunta: tipo de respuesta, si abarca varios documentos y cuáles nombra.</summary>
/// <param name="Bare">La pregunta no trae tema: solo «resumen», «análisis», «resume esto»…</param>
/// <param name="WantsRelated">Pide incluir los documentos relacionados.</param>
public sealed record AnswerIntent(AnswerStyle Style, bool MultiDocument, IReadOnlyList<string> Codes, bool Bare, bool WantsRelated);

/// <summary>
/// Síntesis de uno o varios documentos: detecta la intención, elige los documentos relacionados
/// y las fuentes (máximo 6 páginas distintas, repartidas entre documentos) y redacta, sin modelo,
/// un resumen en lenguaje natural cuando no hay motor generativo o su respuesta no se verifica.
/// </summary>
public static partial class DocumentSynthesis
{
    /// <summary>Límite de fuentes que aceptan los motores (C# y workers Python).</summary>
    public const int MaxSources = 6;
    public const int MaxDocuments = 5;
    /// <summary>Un documento entra en la síntesis si su mejor fragmento llega a esta fracción del mejor global.</summary>
    public const double RelatedThreshold = 0.6;
    public const int MaxGistChars = 380;
    /// <summary>Palabras iniciales donde se busca la intención de resumir o analizar.</summary>
    public const int IntentWindow = 5;

    private static readonly string[] SummaryWords =
        ["resumen", "resume", "resumir", "resumeme", "resumelo", "resumelos", "sintetiza", "sintesis", "sintetizar", "panorama", "de que trata", "de que tratan", "vision general"];
    private static readonly string[] AnalysisWords =
        ["analisis", "analiza", "analizar", "analizame", "analizalo", "compara", "comparar", "comparame", "comparacion", "comparativa",
         "evalua", "evaluar", "conclusiones"];
    /// <summary>Expresiones comparativas que piden análisis en cualquier posición de la pregunta.</summary>
    private static readonly string[] AnalysisPhrases =
        ["en comun", "se diferencian", "diferencias entre", "similitudes entre", "relacion entre", "se relacionan", "como se relacionan"];
    private static readonly string[] MultiWords =
        ["documentos", "expedientes", "todos", "todas", "relacionados", "relacionadas", "conjunto", "compara", "comparar", "comparacion",
         "comparativa", "entre", "ambos", "ambas", "varios", "varias", "diferencias", "similitudes", "en comun", "contratos", "politicas",
         "informes", "actas", "manuales", "reglamentos", "dictamenes"];
    private static readonly HashSet<string> FillerWords = new(StringComparer.Ordinal)
    {
        "de", "del", "la", "el", "los", "las", "un", "una", "unos", "unas", "me", "haz", "hazme", "dame", "por", "favor", "y", "o",
        "sobre", "este", "ese", "esta", "esa", "esto", "eso", "documento", "documentos", "expediente", "expedientes", "general",
        "breve", "corto", "completo", "quiero", "necesito", "puedes", "podrias", "un", "lo", "los", "al", "a", "en", "con", "su", "sus",
        "relacionados", "relacionadas", "todos", "todas", "mis", "mi", "que", "trata", "tratan", "comun", "vision", "porfa"
    };

    [GeneratedRegex(@"\b(?=[A-Z0-9-]*\d)[A-Z]{2,5}-[A-Z0-9]+(?:-[A-Z0-9]+){0,4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentCode();

    [GeneratedRegex(@"[\p{L}\d]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"(?<=[.!?;])\s+")]
    private static partial Regex SentenceBreak();

    public static AnswerIntent Detect(string question)
    {
        var words = Word().Matches(TextAnalysis.Normalize(question)).Select(match => match.Value).ToList();
        var normalized = " " + string.Join(' ', words) + " ";
        // La intención se pide al principio («Resume…», «Hazme un análisis…»); un verbo dentro de una
        // pregunta concreta («¿qué prueba se usó para comparar las medias?») no la convierte en análisis.
        var opening = " " + string.Join(' ', words.Take(IntentWindow)) + " ";
        bool Has(string[] list, string text) => list.Any(word => text.Contains($" {word} ", StringComparison.Ordinal));
        var style = Has(AnalysisWords, opening) || Has(AnalysisPhrases, normalized) ? AnswerStyle.Analysis
            : Has(SummaryWords, opening) ? AnswerStyle.Summary : AnswerStyle.Default;
        var codes = DocumentCode().Matches(question).Select(match => match.Value.ToUpperInvariant()).Distinct().ToList();
        var topic = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !FillerWords.Contains(word) && !SummaryWords.Contains(word) && !AnalysisWords.Contains(word))
            .Count(word => !codes.Any(code => TextAnalysis.Normalize(code).Contains(word, StringComparison.Ordinal)));
        return new AnswerIntent(style, codes.Count >= 2 || Has(MultiWords, normalized), codes, codes.Count == 0 && topic == 0,
            normalized.Contains(" relacionad", StringComparison.Ordinal));
    }

    /// <summary>Documentos relacionados con la consulta: los que tienen fragmentos cerca del mejor resultado.</summary>
    public static IReadOnlyList<string> RankDocuments(IReadOnlyList<RagHit> hits, int max = MaxDocuments, double relative = RelatedThreshold)
    {
        var best = hits.GroupBy(hit => hit.Chunk.DocumentId)
            .Select(group => (Id: group.Key, Score: group.Max(hit => hit.Score)))
            .OrderByDescending(item => item.Score)
            .ToList();
        if (best.Count == 0) return [];
        var top = best[0].Score;
        return best.Where(item => top <= 0 || item.Score >= top * relative).Take(max).Select(item => item.Id).ToList();
    }

    /// <summary>
    /// Fuentes para la síntesis: páginas distintas repartidas por turnos entre documentos. Primero
    /// las páginas donde la consulta encontró fragmentos; después páginas que cubren el documento
    /// (primera, última, intermedias) con su resumen extractivo, para no resumir solo un trozo.
    /// </summary>
    public static List<SearchHit> SelectSources(IReadOnlyList<DocumentItem> documents,
        IReadOnlyDictionary<string, IReadOnlyList<RagHit>> hitsByDocument, int max = MaxSources)
    {
        var queues = documents.Take(max).Select(document =>
        {
            var queue = new Queue<SearchHit>();
            var pages = new HashSet<int>();
            var textPages = document.Pages.Where(page => !string.IsNullOrWhiteSpace(page.ExtractedText)).ToList();
            foreach (var hit in hitsByDocument.GetValueOrDefault(document.Id) ?? [])
            {
                if (textPages.All(page => page.PageNumber != hit.Chunk.PageNumber) || !pages.Add(hit.Chunk.PageNumber)) continue;
                queue.Enqueue(new SearchHit(document, hit.Chunk.PageNumber, TextAnalysis.Truncate(hit.Chunk.Text, 1000),
                    Math.Clamp((int)Math.Round(hit.Score * 100), 0, 100)));
            }
            foreach (var page in SpreadPages(textPages).Where(page => pages.Add(page.PageNumber)))
                queue.Enqueue(new SearchHit(document, page.PageNumber,
                    TextAnalysis.Truncate(string.IsNullOrWhiteSpace(page.Summary) ? page.ExtractedText : page.Summary, 1000), 100));
            return queue;
        }).Where(queue => queue.Count > 0).ToList();

        var selected = new List<SearchHit>();
        while (selected.Count < max && queues.Any(queue => queue.Count > 0))
            foreach (var queue in queues.Where(queue => queue.Count > 0))
            {
                if (selected.Count >= max) break;
                selected.Add(queue.Dequeue());
            }
        return selected;
    }

    /// <summary>Páginas que cubren el documento: primera, última, mitad, cuartos…</summary>
    public static IEnumerable<DocumentPage> SpreadPages(IReadOnlyList<DocumentPage> pages)
    {
        if (pages.Count == 0) yield break;
        var used = new HashSet<int>();
        foreach (var fraction in new[] { 0.0, 1.0, 0.5, 0.25, 0.75 })
        {
            var index = (int)Math.Round(fraction * (pages.Count - 1));
            if (used.Add(index)) yield return pages[index];
        }
    }

    /// <summary>
    /// Resumen o análisis en lenguaje natural sin modelo generativo, a partir de los resúmenes
    /// de cada expediente y de las páginas seleccionadas. Usa el formato que la interfaz
    /// convierte en párrafos, negritas y citas.
    /// </summary>
    public static string Compose(AnswerStyle style, IReadOnlyList<DocumentItem> documents, IReadOnlyList<SearchHit> sources,
        int totalAvailable = 0)
    {
        var parts = new List<string>();
        var codes = documents.Select(document => $"**{document.InternalCode}**").ToList();
        if (documents.Count == 1)
        {
            var only = documents[0];
            parts.Add($"**{only.InternalCode} · {only.Title}** es {Article(only.Category)} {only.Category.ToLowerInvariant()} " +
                $"del área de {only.Department}, de {only.PageCount} página(s).");
        }
        else
        {
            parts.Add(totalAvailable > documents.Count
                ? $"Tienes acceso a {totalAvailable} expedientes. Este es un resumen de los {documents.Count} más relevantes: {Join(codes)}."
                : $"He revisado {documents.Count} documentos relacionados con tu consulta: {Join(codes)}.");
        }

        foreach (var document in documents)
        {
            var source = sources.FirstOrDefault(item => item.Document.Id == document.Id);
            var page = source?.PageNumber ?? document.Pages.FirstOrDefault()?.PageNumber ?? 1;
            var gist = Gist(document, source);
            var lead = documents.Count == 1 ? "" : $"**{document.InternalCode} · {document.Title}.** ";
            parts.Add($"{lead}{gist} [{document.InternalCode}, p. {page}]");
        }

        if (documents.Count > 1)
        {
            var shared = SharedKeywords(documents);
            if (shared.Count > 0)
                parts.Add($"**En común:** tratan temas compartidos como {Join(shared)}.");
            else if (documents.Select(document => document.Department).Distinct().Count() == 1)
                parts.Add($"**En común:** todos pertenecen al área de {documents[0].Department}.");

            if (style == AnswerStyle.Analysis)
            {
                var categories = documents.GroupBy(document => document.Category).ToList();
                if (categories.Count > 1)
                    parts.Add("**Diferencias:** " + string.Join("; ", categories.Select(group =>
                        $"{Join(group.Select(document => document.InternalCode).ToList())} {(group.Count() > 1 ? "son" : "es")} {group.Key.ToLowerInvariant()}")) + ".");
                var newest = documents.MaxBy(document => document.UploadDate)!;
                parts.Add($"El más reciente es **{newest.InternalCode}** ({newest.UploadDate.ToLocalTime():dd/MM/yyyy}).");
            }
        }

        if (style == AnswerStyle.Analysis)
        {
            var entities = documents.SelectMany(document => document.Pages.SelectMany(page => page.Entities)).ToList();
            var facts = new List<string>();
            void Count(string type, string label) { var n = entities.Count(entity => entity.Type == type); if (n > 0) facts.Add($"{n} {label}"); }
            Count("MONEY", "importe(s)");
            Count("DATE", "fecha(s)");
            Count("LAW", "referencia(s) normativa(s)");
            Count("PERSON", "persona(s)");
            if (facts.Count > 0)
                parts.Add($"**A tener en cuenta:** {(documents.Count == 1 ? "el documento menciona" : "en total mencionan")} {Join(facts)}. Pide «extrae montos de CÓDIGO» para verlos.");
        }

        parts.Add($"Resumen extractivo elaborado sin modelo generativo a partir de los resúmenes de {(documents.Count == 1 ? "este expediente" : "cada expediente")}; abre las fuentes para ver el detalle.");
        return string.Join("\n\n", parts);
    }

    /// <summary>Idea principal del documento: primeras frases del resumen ejecutivo o de la fuente.</summary>
    public static string Gist(DocumentItem document, SearchHit? source = null)
    {
        var text = !string.IsNullOrWhiteSpace(document.ExecutiveSummary) ? document.ExecutiveSummary
            : source is not null ? source.Snippet
            : document.Pages.Select(page => page.Summary).FirstOrDefault(summary => !string.IsNullOrWhiteSpace(summary)) ?? "";
        text = Regex.Replace(text.Replace('\n', ' '), @"\s+", " ").Trim().TrimEnd('…');
        if (text.Length == 0) return "No tiene texto extraído para resumir; puede requerir OCR.";
        var gist = "";
        foreach (var sentence in SentenceBreak().Split(text))
        {
            if (gist.Length > 0 && gist.Length + sentence.Length + 1 > MaxGistChars) break;
            gist = gist.Length == 0 ? sentence : $"{gist} {sentence}";
            if (gist.Length >= MaxGistChars / 2) break;
        }
        return gist.Length > MaxGistChars ? TextAnalysis.Truncate(gist, MaxGistChars) : gist;
    }

    /// <summary>Palabras clave presentes en al menos dos documentos (máximo 5).</summary>
    public static IReadOnlyList<string> SharedKeywords(IReadOnlyList<DocumentItem> documents) =>
        documents.SelectMany(document => document.Keywords.Select(keyword => keyword.Trim().ToLowerInvariant()).Where(keyword => keyword.Length > 2).Distinct())
            .GroupBy(keyword => keyword)
            .Where(group => group.Count() >= 2)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(5).Select(group => group.Key).ToList();

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " y " + items[^1]
    };

    private static string Article(string category) =>
        category.StartsWith("Política", StringComparison.Ordinal) ? "una" : "un";
}
