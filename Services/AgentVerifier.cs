using DocuSearch.Models;

namespace DocuSearch.Services;

/// <summary>Códigos cerrados del verificador; no incluyen texto de la respuesta ni de las fuentes.</summary>
public enum VerificationCode
{
    Ok,
    Empty,
    TooLong,
    NoCitations,
    BadCitation,
    DuplicateCitation,
    PageMissing,
    UnauthorizedSource,
    DuplicatePair,
    TextNotInPage,
    DuplicateDocument
}

public sealed record VerificationIssue(
    VerificationCode Code,
    string? DocumentCode = null,
    int? Page = null);

public sealed record VerificationResult(bool IsValid, IReadOnlyList<VerificationIssue> Issues);

public sealed record DuplicateDocumentGroup(string Sha256Hash, IReadOnlyList<string> DocumentCodes);

/// <summary>
/// Agente Verificador (AG-02). Determinista: no llama al modelo, no escribe, no usa red.
/// Revisa la respuesta generada antes de mostrarla y centraliza la validación de citas
/// que antes estaba duplicada en cada motor de generación.
/// </summary>
public static class AgentVerifier
{
    public const int MaxAnswerLength = 10_000;

    /// <summary>
    /// Normaliza IDs de cita: exige al menos uno, dentro de 1..N y sin duplicados.
    /// Los duplicados se eliminan (el contrato del asistente ya los toleraba).
    /// </summary>
    public static bool TryNormalizeCitations(
        IReadOnlyList<int>? citations,
        int sourceCount,
        out IReadOnlyList<int> normalized,
        out VerificationCode code)
    {
        normalized = [];
        if (citations is not { Count: > 0 })
        {
            code = VerificationCode.NoCitations;
            return false;
        }
        if (citations.Any(id => id < 1 || id > sourceCount))
        {
            code = VerificationCode.BadCitation;
            return false;
        }
        normalized = citations.Distinct().ToArray();
        code = VerificationCode.Ok;
        return true;
    }

    /// <summary>
    /// Revisa la respuesta de un motor generativo contra las fuentes y el actor reales.
    /// Comprueba longitud, citas, documento autorizado, página existente, par duplicado
    /// y que el texto citado conste en la página (o en su resumen extractivo).
    /// </summary>
    public static VerificationResult Verify(
        AgentAnswer answer,
        IReadOnlyList<DocumentItem> readableDocuments,
        AppUser actor)
    {
        var issues = new List<VerificationIssue>();

        if (string.IsNullOrWhiteSpace(answer.Text))
            issues.Add(new VerificationIssue(VerificationCode.Empty));
        else if (answer.Text.Length > MaxAnswerLength)
            issues.Add(new VerificationIssue(VerificationCode.TooLong));

        if (answer.Sources is not { Count: > 0 })
        {
            issues.Add(new VerificationIssue(VerificationCode.NoCitations));
            return new VerificationResult(false, issues);
        }

        var documentsById = readableDocuments.ToDictionary(document => document.Id, StringComparer.Ordinal);
        var seenPairs = new HashSet<(string DocumentId, int Page)>();

        foreach (var source in answer.Sources)
        {
            var sourceDocument = source.Document;
            if (sourceDocument is null ||
                !documentsById.TryGetValue(sourceDocument.Id, out var document) ||
                !Permissions.CanRead(actor, document))
            {
                issues.Add(new VerificationIssue(VerificationCode.UnauthorizedSource, sourceDocument?.InternalCode));
                continue;
            }

            List<DocumentPage> pages;
            try
            {
                pages = document.Pages;
            }
            catch (InvalidDataException)
            {
                issues.Add(new VerificationIssue(VerificationCode.PageMissing, document.InternalCode, source.PageNumber));
                continue;
            }

            var page = pages.FirstOrDefault(candidate => candidate.PageNumber == source.PageNumber);
            if (page is null)
            {
                issues.Add(new VerificationIssue(VerificationCode.PageMissing, document.InternalCode, source.PageNumber));
                continue;
            }

            if (!seenPairs.Add((document.Id, source.PageNumber)))
                issues.Add(new VerificationIssue(VerificationCode.DuplicatePair, document.InternalCode, source.PageNumber));

            if (string.IsNullOrWhiteSpace(page.ExtractedText) || string.IsNullOrWhiteSpace(source.Snippet))
                continue;

            // El contexto se trunca con «…»; se compara el texto normalizado y también el
            // resumen extractivo de la página, que es una selección de su propio texto.
            var rawSnippet = source.Snippet.Trim().TrimEnd('…');
            if (rawSnippet.Length == 0) continue;
            if (page.ExtractedText.Contains(rawSnippet, StringComparison.Ordinal) ||
                (page.Summary ?? "").Contains(rawSnippet, StringComparison.Ordinal))
                continue;

            var snippet = TextAnalysis.Normalize(rawSnippet);
            if (snippet.Length == 0) continue;

            var pageText = TextAnalysis.Normalize(page.ExtractedText);
            var pageSummary = TextAnalysis.Normalize(page.Summary ?? "");
            if (!pageText.Contains(snippet, StringComparison.Ordinal) &&
                !pageSummary.Contains(snippet, StringComparison.Ordinal))
            {
                issues.Add(new VerificationIssue(VerificationCode.TextNotInPage, document.InternalCode, source.PageNumber));
            }
        }

        return new VerificationResult(issues.Count == 0, issues);
    }

    /// <summary>
    /// Informe determinista de duplicados por SHA-256 para el Inspector/AG-10.
    /// No lee contenido ni modifica documentos.
    /// </summary>
    public static IReadOnlyList<DuplicateDocumentGroup> FindDuplicateDocuments(IEnumerable<DocumentItem> documents) =>
        documents
            .Where(document => !string.IsNullOrWhiteSpace(document.Sha256Hash))
            .GroupBy(document => document.Sha256Hash, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => new DuplicateDocumentGroup(
                group.Key,
                group.Select(document => document.InternalCode).OrderBy(code => code, StringComparer.Ordinal).ToList()))
            .ToList();
}
