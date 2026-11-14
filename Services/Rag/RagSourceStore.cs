using System.Security.Cryptography;
using System.Text;
using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuSearch.Services.Rag;

/// <summary>
/// Persiste una fuente no-PDF (PDF/REST) como expediente consultable para que las
/// citas del pipeline puedan resolver documento y página con el RBAC existente.
/// </summary>
public sealed class RagSourceStore(IDbContextFactory<AppDbContext> factory,
    ILogger<RagSourceStore> logger)
{
    public async Task<string> EnsureDocumentAsync(RagDocument source, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var document = await db.Documents.SingleOrDefaultAsync(item => item.Id == source.SourceId, cancellationToken);
        var pages = source.Pages.Select(page => TextAnalysis.AnalyzePage(page.PageNumber, page.Text)).ToList();
        var text = string.Join("\n", pages.Select(page => page.ExtractedText));
        var metadata = source.Metadata;
        var code = TextAnalysis.Truncate(metadata.GetValueOrDefault("code",
            $"RAG-{source.SourceType.ToUpperInvariant()}-{source.SourceId[..Math.Min(8, source.SourceId.Length)]}"), 100);
        if (document is null)
        {
            document = new DocumentItem { Id = source.SourceId, InternalCode = code };
            db.Documents.Add(document);
        }
        document.Title = TextAnalysis.Truncate(source.Title, 300);
        document.Author = TextAnalysis.Truncate(metadata.GetValueOrDefault("author", "Ingesta RAG"), 200);
        document.Department = metadata.GetValueOrDefault("department", "Tecnología & TI");
        document.Category = metadata.GetValueOrDefault("category", "Manual Técnico");
        document.Confidentiality = metadata.GetValueOrDefault("confidentiality", "Uso Interno");
        document.Version = metadata.GetValueOrDefault("version", "v1.0");
        document.UploadDate = DateTimeOffset.UtcNow;
        document.PageCount = pages.Count;
        document.Pages = pages;
        document.ExecutiveSummary = TextAnalysis.Truncate(TextAnalysis.Summary(text), 10000);
        document.Keywords = TextAnalysis.Terms(text).Take(20).ToArray();
        document.Tags = [];
        document.FileSizeBytes = Encoding.UTF8.GetByteCount(text);
        document.Sha256Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        document.HasOriginal = false;
        document.OcrStatus = pages.Any(page => string.IsNullOrWhiteSpace(page.ExtractedText))
            ? "Requiere OCR" : "Completado";
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("RAG: fuente {Source} persistida como {Code} ({Pages} páginas).",
            source.SourceType, document.InternalCode, pages.Count);
        return document.Id;
    }
}
