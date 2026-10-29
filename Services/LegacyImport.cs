using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuSearch.Services;

public sealed class LegacyImport(IDbContextFactory<AppDbContext> factory, LocalSession session, PdfProcessor processor)
{
    public async Task ImportAsync(Stream stream)
    {
        using var json = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 32 });
        var root = json.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var number) || number != 1)
            throw new InvalidDataException("Formato de respaldo desconocido. Usa el exportador incluido.");
        var backup = root.Deserialize<SeedData>(Catalog.Json) ?? throw new InvalidDataException("Respaldo vacío.");
        if (backup.Documents is null || backup.Users is null || backup.Departments is null ||
            backup.Documents.Any(d => d is null) || backup.Users.Any(u => u is null) || backup.Departments.Any(d => d is null) ||
            backup.Documents.Count > 100 || backup.Users.Count > 1000 || backup.Departments.Count > 100)
            throw new InvalidDataException("El respaldo supera los límites: 100 documentos, 1000 usuarios y 100 departamentos.");
        await using var db = await factory.CreateDbContextAsync();
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == session.UserId && u.Status == "Activo")
            ?? throw new UnauthorizedAccessException("Perfil inactivo.");
        if (!Permissions.CanAdmin(actor)) throw new UnauthorizedAccessException("Solo el administrador puede importar respaldos.");
        await using var tx = await db.Database.BeginTransactionAsync();
        await DatabaseSetup.LockAsync(db);
        actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == session.UserId && u.Status == "Activo")
            ?? throw new UnauthorizedAccessException("Perfil inactivo.");
        if (!Permissions.CanAdmin(actor)) throw new UnauthorizedAccessException("Solo el administrador puede importar respaldos.");
        foreach (var department in backup.Departments)
        {
            Validate(department);
            var existing = await db.Departments.SingleOrDefaultAsync(d => d.Id == department.Id);
            if (existing is not null && existing.Name != department.Name)
                throw new ValidationException("No se puede renombrar un departamento existente desde un respaldo.");
            if (existing is null) db.Departments.Add(department);
            else db.Entry(existing).CurrentValues.SetValues(department);
        }
        await db.SaveChangesAsync();
        foreach (var user in backup.Users)
        {
            Validate(user);
            if (!Catalog.Roles.Contains(user.Role) || user.Status is not ("Activo" or "Inactivo"))
                throw new ValidationException("El respaldo contiene un perfil inválido.");
            if (user.Id == actor.Id && (user.Role != "admin_general" || user.Status != "Activo"))
                throw new ValidationException("El respaldo no puede desactivar o degradar tu perfil.");
            user.Email = user.Email.Trim().ToLowerInvariant();
            var existing = await db.Users.SingleOrDefaultAsync(u => u.Id == user.Id);
            if (existing is null) db.Users.Add(user);
            else db.Entry(existing).CurrentValues.SetValues(user);
        }
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Falta el catálogo de archivos del respaldo.");
        foreach (var document in backup.Documents)
        {
            Validate(document);
            if (!Catalog.Categories.Contains(document.Category) || !Catalog.Levels.Contains(document.Confidentiality) ||
                document.PageCount is < 1 or > PdfProcessor.MaxPages || document.Pages.Count > PdfProcessor.MaxPages ||
                document.Pages.Any(p => p is null || p.PageNumber < 1 || p.PageNumber > document.PageCount || p.ExtractedText is null || p.ExtractedText.Length > 250000 || p.Entities is null) ||
                document.Notes.Count > 1000 || document.Notes.Any(n => n is null || string.IsNullOrWhiteSpace(n.Text) || n.Text.Length > 4000) ||
                document.Keywords is null || document.Tags is null || document.Keywords.Length > 100 || document.Tags.Length > 100 ||
                document.Keywords.Concat(document.Tags).Any(t => t is null || t.Length > 200))
                throw new InvalidDataException($"Expediente inválido en el respaldo: {document.Id}.");
            var existing = await db.Documents.SingleOrDefaultAsync(d => d.Id == document.Id);
            document.UploadDate = document.UploadDate.ToUniversalTime();
            document.Notes = document.Notes.Select(n => n with { CreatedAt = n.CreatedAt.ToUniversalTime() }).ToList();
            if (files.TryGetProperty(document.Id, out var encoded))
            {
                var base64 = encoded.GetString() ?? throw new InvalidDataException("PDF codificado vacío.");
                if (base64.Length > (PdfProcessor.MaxFileBytes + 2) / 3 * 4)
                    throw new InvalidDataException("Un PDF del respaldo supera los 25 MB.");
                byte[] bytes;
                try { bytes = Convert.FromBase64String(base64); }
                catch (FormatException ex) { throw new InvalidDataException("PDF base64 inválido.", ex); }
                await Task.Run(() => processor.Process(document, bytes));
                var file = await db.Files.SingleOrDefaultAsync(f => f.DocumentId == document.Id);
                if (file is null) db.Files.Add(new() { DocumentId = document.Id, Bytes = bytes });
                else file.Bytes = bytes;
            }
            else if (existing?.HasOriginal == true)
            {
                document.HasOriginal = true;
                document.FileSizeBytes = existing.FileSizeBytes;
                document.Sha256Hash = existing.Sha256Hash;
                document.PageCount = existing.PageCount;
                document.Pages = existing.Pages;
                document.OcrStatus = existing.OcrStatus;
            }
            else document.HasOriginal = false;
            if (existing is null) db.Documents.Add(document);
            else db.Entry(existing).CurrentValues.SetValues(document);
        }
        await db.SaveChangesAsync();
        foreach (var department in await db.Departments.ToListAsync())
        {
            var used = await db.Documents.Where(d => d.Department == department.Name && d.HasOriginal).SumAsync(d => d.FileSizeBytes);
            if (used > department.AllocatedStorageMB * 1024L * 1024)
                throw new ValidationException($"El respaldo supera la cuota de {department.Name}.");
        }
        if (root.TryGetProperty("auditLogs", out var history))
        {
            if (history.ValueKind != JsonValueKind.Array || history.GetArrayLength() > 10000)
                throw new InvalidDataException("Historial de respaldo inválido o demasiado grande.");
            foreach (var entry in history.EnumerateArray())
            {
                db.Audit.Add(new AuditEvent
                {
                    UserName = actor.Name, UserRole = actor.Role, Action = "IMPORT_HISTORY",
                    Details = "Registro histórico importado (origen no verificado): " + TextAnalysis.Truncate(entry.GetRawText(), 4000)
                });
            }
        }
        db.Audit.Add(new AuditEvent
        {
            UserName = actor.Name, UserRole = actor.Role, Action = "IMPORT",
            Details = $"Importados {backup.Documents.Count} documentos, {backup.Users.Count} perfiles y {backup.Departments.Count} departamentos."
        });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private static void Validate(object value) => Validator.ValidateObject(value, new ValidationContext(value), true);
}
