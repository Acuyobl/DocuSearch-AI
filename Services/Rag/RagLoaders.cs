using System.Text.Json;
using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DocuSearch.Services.Rag;

/// <summary>Loader de PDF: una entrada de la fuente con páginas y metadatos.</summary>
public sealed class PdfRagLoader(string path) : IRagLoader
{
    public string SourceType => "pdf";

    public Task<IReadOnlyList<RagDocument>> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("No existe el PDF de ingesta.", fullPath);
        using var pdf = PdfDocument.Open(fullPath);
        var pages = pdf.GetPages()
            .Select(page => new RagPage(page.Number, ContentOrderTextExtractor.GetText(page)))
            .ToList();
        var text = string.Join("\n", pages.Select(page => page.Text));
        var id = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(fullPath)))[..16];
        var metadata = new Dictionary<string, string>
        {
            ["source"] = fullPath,
            ["date"] = File.GetLastWriteTimeUtc(fullPath).ToString("O"),
            ["code"] = Path.GetFileNameWithoutExtension(fullPath)
        };
        return Task.FromResult<IReadOnlyList<RagDocument>>(
            [new RagDocument(id, SourceType, Path.GetFileNameWithoutExtension(fullPath), text, pages, metadata)]);
    }
}

/// <summary>Loader de documentos ya persistidos en SQL Server.</summary>
public sealed class SqlRagLoader(IDbContextFactory<AppDbContext> factory) : IRagLoader
{
    public string SourceType => "sql";

    public async Task<IReadOnlyList<RagDocument>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var documents = await db.Documents.AsNoTracking()
            .OrderBy(document => document.InternalCode)
            .ToListAsync(cancellationToken);
        return documents.Select(document => new RagDocument(
            document.Id, SourceType, document.Title,
            string.Join("\n", document.Pages.Select(page => page.ExtractedText)),
            document.Pages.Select(page => new RagPage(page.PageNumber, page.ExtractedText)).ToList(),
            new Dictionary<string, string>
            {
                ["source"] = "sql:Documents",
                ["code"] = document.InternalCode,
                ["date"] = document.UploadDate.ToString("O"),
                ["department"] = document.Department,
                ["confidentiality"] = document.Confidentiality
            })).ToList();
    }
}

/// <summary>Loader de una API REST que devuelve documentos/páginas en JSON.</summary>
public sealed class RestRagLoader(HttpClient http, string url) : IRagLoader
{
    public string SourceType => "rest";

    public async Task<IReadOnlyList<RagDocument>> LoadAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;
        var items = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToList()
            : root.TryGetProperty("documents", out var documents) && documents.ValueKind == JsonValueKind.Array
                ? documents.EnumerateArray().ToList()
                : [];
        if (items.Count is 0 or > 100) throw new InvalidDataException("La API debe devolver entre 1 y 100 documentos.");
        var result = new List<RagDocument>();
        foreach (var item in items)
        {
            var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var title = item.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : id;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
                throw new InvalidDataException("Cada documento REST necesita id y title.");
            var pages = new List<RagPage>();
            if (item.TryGetProperty("pages", out var pagesElement) && pagesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var page in pagesElement.EnumerateArray())
                {
                    var number = page.TryGetProperty("pageNumber", out var numberElement) ? numberElement.GetInt32() : pages.Count + 1;
                    var text = page.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? "" : "";
                    pages.Add(new RagPage(number, text));
                }
            }
            if (pages.Count == 0)
            {
                var text = item.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? "" : "";
                pages.Add(new RagPage(1, text));
            }
            var fullText = string.Join("\n", pages.Select(page => page.Text));
            result.Add(new RagDocument(id, SourceType, title, fullText, pages,
                new Dictionary<string, string>
                {
                    ["source"] = url,
                    ["code"] = id,
                    ["date"] = DateTimeOffset.UtcNow.ToString("O")
                }));
        }
        return result;
    }
}
