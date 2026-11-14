using System.Security.Cryptography;
using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuSearch.Services;

public sealed record DuplicateGroupReport(string Sha256Hash, IReadOnlyList<string> DocumentCodes);

public sealed record InspectionFinding(
    string Kind,
    string Severity,
    string DocumentCode,
    string Title,
    string Department,
    string Confidentiality,
    string Detail);

public sealed record DocumentInspectionReport(
    DateTimeOffset GeneratedUtc,
    int TotalDocuments,
    int OcrPending,
    int MissingOriginals,
    int MetadataGaps,
    int IntegrityMismatches,
    IReadOnlyList<DuplicateGroupReport> Duplicates,
    IReadOnlyList<InspectionFinding> Findings);

/// <summary>
/// AG-10 Inspector: informe determinista de calidad documental. No usa LLM, no usa red,
/// no modifica documentos y no expone texto extraído; solo metadatos, estados y hashes.
/// </summary>
public static class DocumentInspector
{
    public static DocumentInspectionReport Analyze(
        IReadOnlyList<DocumentItem> documents,
        IReadOnlyDictionary<string, string>? observedHashes = null,
        DateTimeOffset? now = null)
    {
        var findings = new List<InspectionFinding>();
        var duplicates = AgentVerifier.FindDuplicateDocuments(documents)
            .Select(group => new DuplicateGroupReport(group.Sha256Hash, group.DocumentCodes))
            .ToList();
        var duplicateByCode = duplicates
            .SelectMany(group => group.DocumentCodes.Select(code => (code, group)))
            .ToDictionary(pair => pair.code, pair => pair.group, StringComparer.OrdinalIgnoreCase);
        var ocrPending = 0;
        var missingOriginals = 0;
        var metadataGaps = 0;
        var integrityMismatches = 0;

        foreach (var document in documents)
        {
            if (document.OcrStatus.Contains("Requiere OCR", StringComparison.OrdinalIgnoreCase))
            {
                ocrPending++;
                findings.Add(Finding(document, "ocr", "warning",
                    "El expediente tiene páginas sin capa de texto; requiere OCR o revisión humana."));
            }

            if (!document.HasOriginal)
            {
                missingOriginals++;
                findings.Add(Finding(document, "missing_original", "warning",
                    "El expediente no incluye el PDF original; el hash no puede verificarse."));
            }

            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(document.Title)) missing.Add("título");
            if (string.IsNullOrWhiteSpace(document.Author)) missing.Add("autor");
            if (string.IsNullOrWhiteSpace(document.Department)) missing.Add("departamento");
            if (string.IsNullOrWhiteSpace(document.Category)) missing.Add("categoría");
            if (string.IsNullOrWhiteSpace(document.ExecutiveSummary)) missing.Add("resumen");
            if (document.Keywords.Length == 0) missing.Add("palabras clave");
            if (missing.Count > 0)
            {
                metadataGaps++;
                findings.Add(Finding(document, "metadata", "info",
                    "Metadatos incompletos: " + string.Join(", ", missing) + "."));
            }

            if (duplicateByCode.TryGetValue(document.InternalCode, out var duplicateGroup))
            {
                var peers = duplicateGroup.DocumentCodes
                    .Where(code => !string.Equals(code, document.InternalCode, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                findings.Add(Finding(document, "duplicate", "warning",
                    "Comparte SHA-256 con: " + string.Join(", ", peers) + "."));
            }

            if (observedHashes is not null &&
                document.HasOriginal &&
                !string.IsNullOrWhiteSpace(document.Sha256Hash) &&
                observedHashes.TryGetValue(document.Id, out var observed) &&
                !string.Equals(observed, document.Sha256Hash, StringComparison.OrdinalIgnoreCase))
            {
                integrityMismatches++;
                findings.Add(Finding(document, "integrity", "error",
                    "El SHA-256 recalculado no coincide con el registrado."));
            }
        }

        findings = findings
            .OrderBy(finding => finding.DocumentCode, StringComparer.Ordinal)
            .ThenBy(finding => finding.Kind, StringComparer.Ordinal)
            .ToList();

        return new DocumentInspectionReport(
            now ?? DateTimeOffset.UtcNow,
            documents.Count,
            ocrPending,
            missingOriginals,
            metadataGaps,
            integrityMismatches,
            duplicates,
            findings);
    }

    private static InspectionFinding Finding(DocumentItem document, string kind, string severity, string detail) =>
        new(kind, severity, document.InternalCode,
            string.IsNullOrWhiteSpace(document.Title) ? "(sin título)" : document.Title,
            document.Department, document.Confidentiality, detail);
}

/// <summary>
/// Servicio de solo lectura que arma el informe con el actor autenticado y, si se pide,
/// recalcula el SHA-256 de los originales sin exponer su contenido.
/// </summary>
public sealed class DocumentQualityService(IDbContextFactory<AppDbContext> factory, LocalSession session)
{
    public async Task<DocumentInspectionReport> InspectAsync(bool verifyIntegrity)
    {
        await using var db = await factory.CreateDbContextAsync();
        var actor = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(user => user.Id == session.UserId && user.Status == "Activo")
            ?? throw new UnauthorizedAccessException("No hay una sesión autenticada activa.");
        if (!Permissions.CanAudit(actor))
            throw new UnauthorizedAccessException("Solo administración general y auditoría pueden consultar el informe de calidad.");

        var documents = (await db.Documents.AsNoTracking().ToListAsync())
            .Where(document => Permissions.CanRead(actor, document))
            .ToList();

        Dictionary<string, string>? observedHashes = null;
        if (verifyIntegrity)
        {
            observedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            var withOriginal = documents.Where(document => document.HasOriginal).Select(document => document.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var batch in withOriginal.Chunk(100))
            {
                await foreach (var file in db.Files.AsNoTracking()
                    .Where(file => batch.Contains(file.DocumentId))
                    .AsAsyncEnumerable())
                {
                    observedHashes[file.DocumentId] = Convert.ToHexStringLower(SHA256.HashData(file.Bytes));
                }
            }
        }

        return DocumentInspector.Analyze(documents, observedHashes);
    }
}
