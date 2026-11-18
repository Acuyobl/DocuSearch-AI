using System.Security.Cryptography;
using DocuSearch.Data;
using DocuSearch.Models;
using DocuSearch.Services.Rag;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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

    internal static InspectionFinding Finding(DocumentItem document, string kind, string severity, string detail) =>
        new(kind, severity, document.InternalCode,
            string.IsNullOrWhiteSpace(document.Title) ? "(sin título)" : document.Title,
            document.Department, document.Confidentiality, detail);
}

/// <summary>
/// Servicio de solo lectura que arma el informe con el actor autenticado y, si se pide,
/// recalcula el SHA-256 de los originales sin exponer su contenido.
/// </summary>
public sealed class DocumentQualityService(
    IDbContextFactory<AppDbContext> factory,
    LocalSession session,
    IOptions<EmbeddingOptions>? embeddingOptions = null,
    IOptions<RagOptions>? ragOptions = null,
    PineconeClient? pinecone = null)
{
    private async Task<AppUser> AuditorAsync(AppDbContext db)
    {
        var actor = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(user => user.Id == session.UserId && user.Status == "Activo")
            ?? throw new UnauthorizedAccessException("No hay una sesión autenticada activa.");
        if (!Permissions.CanAudit(actor))
            throw new UnauthorizedAccessException("Solo administración general y auditoría pueden consultar el informe de calidad.");
        return actor;
    }

    private static async Task<List<DocumentItem>> ReadableAsync(AppDbContext db, AppUser actor) =>
        (await db.Documents.AsNoTracking().ToListAsync())
            .Where(document => Permissions.CanRead(actor, document))
            .ToList();

    /// <summary>El almacén RAG es Pinecone y está configurado, así que tiene sentido comparar con SQL.</summary>
    public bool PineconeConfigured =>
        pinecone is { Enabled: true } &&
        string.Equals(ragOptions?.Value.VectorStore, "pinecone", StringComparison.OrdinalIgnoreCase);

    public async Task<DocumentInspectionReport> InspectAsync(bool verifyIntegrity)
    {
        await using var db = await factory.CreateDbContextAsync();
        var actor = await AuditorAsync(db);
        var documents = await ReadableAsync(db, actor);

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

    /// <summary>
    /// Salud del índice RAG de los expedientes legibles. Solo agrega metadatos de los fragmentos
    /// (página, modelo, dimensiones, recuento y fecha); nunca carga su texto ni sus vectores.
    /// </summary>
    public async Task<RagIndexReport> InspectRagIndexAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var actor = await AuditorAsync(db);
        return await InspectRagIndexCoreAsync(db, await ReadableAsync(db, actor));
    }

    /// <summary>
    /// Escaneo de sistema para la rutina programada: revisa todos los expedientes y solo
    /// devuelve recuentos y códigos, sin texto. No se expone en la interfaz.
    /// </summary>
    internal async Task<RagIndexReport> InspectRagIndexForRoutineAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await InspectRagIndexCoreAsync(db, await db.Documents.AsNoTracking().ToListAsync());
    }

    private async Task<RagIndexReport> InspectRagIndexCoreAsync(AppDbContext db, List<DocumentItem> documents)
    {
        var ids = documents.Select(document => document.Id).ToHashSet(StringComparer.Ordinal);
        var codes = documents.Select(document => document.InternalCode).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stats = (await db.RagChunks.AsNoTracking()
                .GroupBy(chunk => new { chunk.DocumentId, chunk.PageNumber, chunk.Model, chunk.Dimensions })
                .Select(group => new
                {
                    group.Key.DocumentId, group.Key.PageNumber, group.Key.Model, group.Key.Dimensions,
                    Chunks = group.Count(), Last = group.Max(chunk => chunk.CreatedUtc)
                })
                .ToListAsync())
            .Where(row => ids.Contains(row.DocumentId))
            .Select(row => new RagPageStat(row.DocumentId, row.PageNumber, row.Model ?? "", row.Dimensions, row.Chunks, row.Last))
            .ToList();

        var changes = (await db.Audit.AsNoTracking()
                .Where(item => (item.Action == "UPLOAD" || item.Action == "REPROCESS") && item.Status == "EXITOSO")
                .GroupBy(item => item.DocumentCode)
                .Select(group => new { Code = group.Key, Last = group.Max(item => item.TimestampUTC) })
                .ToListAsync())
            .Where(row => codes.Contains(row.Code))
            .ToDictionary(row => row.Code, row => row.Last, StringComparer.OrdinalIgnoreCase);

        var withPageIndex = (await db.PageEmbeddings.AsNoTracking()
                .Select(embedding => embedding.DocumentId)
                .Distinct()
                .ToListAsync())
            .Where(ids.Contains)
            .ToHashSet(StringComparer.Ordinal);

        var embedding = embeddingOptions?.Value ?? new EmbeddingOptions();
        return RagIndexInspector.Analyze(documents, stats, changes, withPageIndex,
            embedding.Model, embedding.Dimensions, embedding.Enabled);
    }

    /// <summary>
    /// Compara los IDs de fragmentos de SQL con los vectores «rag:» de Pinecone. Hace llamadas
    /// de red, por eso se ejecuta solo a petición. Solo devuelve recuentos.
    /// </summary>
    public async Task<RagPineconeDrift> CheckPineconeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        _ = await AuditorAsync(db);
        if (!PineconeConfigured)
            return new RagPineconeDrift(false, "El almacén RAG es SQL Server (Rag:VectorStore=sql); no hay índice externo que comparar.");

        var expected = (await db.RagChunks.AsNoTracking().Select(chunk => chunk.Id).ToListAsync(cancellationToken))
            .Select(PineconeRagVectorStore.VectorId)
            .ToHashSet(StringComparer.Ordinal);
        var present = (await pinecone!.ListIdsAsync(PineconeRagVectorStore.IdPrefix, cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var missing = expected.Count(id => !present.Contains(id));
        var orphans = present.Count(id => !expected.Contains(id));
        var message = missing == 0 && orphans == 0
            ? "Pinecone y SQL Server están sincronizados."
            : "Hay diferencias entre Pinecone y SQL Server: reindexa los expedientes afectados o ejecuta --rag-pinecone-sync.";
        return new RagPineconeDrift(true, message, expected.Count, present.Count, missing, orphans);
    }
}
