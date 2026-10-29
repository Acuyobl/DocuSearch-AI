using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuSearch.Services;

public sealed class DocumentService(IDbContextFactory<AppDbContext> factory, LocalSession session, PdfProcessor processor)
{
    private async Task<AppUser> ActorAsync(AppDbContext db)
    {
        if (!session.IsAuthenticated)
            throw new UnauthorizedAccessException("No hay una sesión autenticada. Inicia sesión para continuar.");
        return await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == session.UserId && u.Status == "Activo")
            ?? throw new UnauthorizedAccessException("El usuario autenticado no existe o está inactivo. Vuelve a iniciar sesión.");
    }

    private static IQueryable<DocumentItem> Readable(AppDbContext db, AppUser actor)
    {
        var levels = Permissions.ReadableLevels(actor);
        return db.Documents.Where(d => levels.Contains(d.Confidentiality) &&
            (actor.Role != "admin_depto" || d.Department == actor.Department));
    }

    private static void Audit(AppDbContext db, AppUser user, string action, string details, string? code = null, string status = "EXITOSO") =>
        db.Audit.Add(new AuditEvent
        {
            UserName = user.Name, UserRole = user.Role, Action = action, Details = details,
            DocumentCode = code ?? "SYSTEM", Status = status
        });

    private async Task RequireAsync(AppDbContext db, AppUser actor, bool permitted, string operation)
    {
        if (permitted) return;
        // La denegación debe sobrevivir al rollback de la operación que se rechaza.
        await using var auditDb = await factory.CreateDbContextAsync();
        Audit(auditDb, actor, "ACCESS_DENIED", $"Operación denegada: {operation}", status: "DENEGADO");
        await auditDb.SaveChangesAsync();
        throw new UnauthorizedAccessException("Tu perfil no tiene permiso para esta operación.");
    }

    private static void Validate(object value) => Validator.ValidateObject(value, new ValidationContext(value), true);
    private static void ValidateDocument(DocumentItem value)
    {
        Validate(value);
        if (!Catalog.Categories.Contains(value.Category) || !Catalog.Levels.Contains(value.Confidentiality))
            throw new ValidationException("Categoría o confidencialidad inválida.");
        if (value.Keywords is null || value.Tags is null || value.Keywords.Length > 100 || value.Tags.Length > 100 ||
            value.Keywords.Concat(value.Tags).Any(x => x is null || x.Length > 200))
            throw new ValidationException("Se admiten hasta 100 palabras y etiquetas de 200 caracteres.");
    }

    public async Task<AppSnapshot> LoadAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var actor = await ActorAsync(db);
        return new(actor, await Readable(db, actor).AsNoTracking().OrderByDescending(d => d.UploadDate).ToListAsync(),
            await db.Users.AsNoTracking().OrderBy(u => u.Name).ToListAsync(),
            await db.Departments.AsNoTracking().OrderBy(d => d.Name).ToListAsync(),
            Permissions.CanAudit(actor) ? await db.Audit.AsNoTracking().OrderByDescending(a => a.TimestampUTC).Take(1000).ToListAsync() : []);
    }

    public async Task<List<AppUser>> ListProfilesAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Users.AsNoTracking().Where(u => u.Status == "Activo").OrderBy(u => u.Name).ToListAsync();
    }

    /// <summary>
    /// Cambia el usuario de la sesión en memoria. La interfaz ya no lo usa (la identidad
    /// procede de la cookie autenticada); se conserva para la suite de comprobaciones.
    /// </summary>
    public async Task SwitchProfileAsync(string id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id && u.Status == "Activo")
            ?? throw new ValidationException("El perfil seleccionado no está activo.");
        Audit(db, user, "PROFILE_SWITCH", "Cambio de perfil solicitado por la suite de comprobaciones; no es un inicio de sesión.");
        await db.SaveChangesAsync();
        session.UserId = user.Id;
        session.LastDocumentCode = null;
    }

    public async Task<DocumentItem> ViewAsync(string id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var actor = await ActorAsync(db);
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == id);
        await RequireAsync(db, actor, document is not null && Permissions.CanRead(actor, document), "VIEW");
        document!.ViewsCount++;
        Audit(db, actor, "VIEW", "Visualización del expediente.", document.InternalCode);
        await db.SaveChangesAsync();
        return document;
    }

    public async Task<byte[]> FileAsync(string id, bool download)
    {
        await using var db = await factory.CreateDbContextAsync();
        var actor = await ActorAsync(db);
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id);
        await RequireAsync(db, actor, document is not null && (download
            ? Permissions.CanDownload(actor, document) : Permissions.CanRead(actor, document)), "PDF");
        var file = await db.Files.AsNoTracking().SingleOrDefaultAsync(f => f.DocumentId == id)
            ?? throw new InvalidOperationException("Este expediente de ejemplo no incluye PDF original.");
        await VerifyIntegrityAsync(db, actor, document!, file.Bytes);
        if (download)
        {
            await db.Documents.Where(d => d.Id == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.DownloadsCount, d => d.DownloadsCount + 1));
            Audit(db, actor, "DOWNLOAD", "Descarga del PDF original.", document!.InternalCode);
            await db.SaveChangesAsync();
        }
        return file.Bytes;
    }

    /// <summary>Comprueba que el PDF servido coincide con el SHA-256 registrado en la ingesta.</summary>
    private async Task VerifyIntegrityAsync(AppDbContext db, AppUser actor, DocumentItem document, byte[] bytes)
    {
        if (!document.HasOriginal || string.IsNullOrWhiteSpace(document.Sha256Hash)) return;
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (string.Equals(actual, document.Sha256Hash, StringComparison.OrdinalIgnoreCase)) return;
        Audit(db, actor, "INTEGRITY_MISMATCH", $"El PDF de {document.InternalCode} no coincide con el SHA-256 registrado.", document.InternalCode, "ADVERTENCIA");
        await db.SaveChangesAsync();
        throw new InvalidDataException("La integridad del PDF no coincide con el SHA-256 registrado. La entrega se ha cancelado y el incidente se ha auditado.");
    }

    public async Task UploadAsync(DocumentItem metadata, byte[] bytes)
    {
        await using var db = await factory.CreateDbContextAsync();
        var actor = await ActorAsync(db);
        await RequireAsync(db, actor, Permissions.CanAssign(actor, metadata.Department, metadata.Confidentiality), "UPLOAD");
        var document = new DocumentItem
        {
            Title = metadata.Title.Trim(), Author = metadata.Author.Trim(), Department = metadata.Department,
            Category = metadata.Category, Version = metadata.Version.Trim(), Confidentiality = metadata.Confidentiality,
            Tags = metadata.Tags
        };
        await Task.Run(() => processor.Process(document, bytes));
        await using var tx = await db.Database.BeginTransactionAsync();
        await DatabaseSetup.LockAsync(db);
        actor = await ActorAsync(db);
        await RequireAsync(db, actor, Permissions.CanAssign(actor, document.Department, document.Confidentiality), "UPLOAD");
        var prefix = document.Category switch { "Contrato" => "CNT", "Política & Norma" => "POL", "Informe Financiero" => "FIN", "Reglamento" => "REG", "Dictamen" => "DIC", "Acta de Reunión" => "ACT", _ => "MAN" };
        document.InternalCode = $"{prefix}-{DateTime.UtcNow.Year}-{Catalog.NewId()[..12].ToUpperInvariant()}";
        ValidateDocument(document);
        await CheckQuotaAsync(db, document.Department, bytes.LongLength);
        db.Documents.Add(document);
        db.Files.Add(new DocumentFile { DocumentId = document.Id, Bytes = bytes });
        Audit(db, actor, "UPLOAD", $"{document.Title}. Estado: {document.OcrStatus}. SHA-256: {document.Sha256Hash}", document.InternalCode);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private static async Task CheckQuotaAsync(AppDbContext db, string department, long extraBytes, string? excluding = null)
    {
        var dept = await db.Departments.SingleOrDefaultAsync(d => d.Name == department)
            ?? throw new ValidationException("El departamento no existe.");
        var used = await db.Documents.Where(d => d.Department == department && d.HasOriginal && d.Id != excluding)
            .SumAsync(d => d.FileSizeBytes);
        if (used + extraBytes > dept.AllocatedStorageMB * 1024L * 1024)
            throw new ValidationException("La carga superaría la cuota del departamento.");
    }

    public async Task EditAsync(DocumentItem edited)
    {
        await MutateDocumentAsync(edited.Id, "UPDATE", async (db, actor, document) =>
        {
            await RequireAsync(db, actor, Permissions.CanAssign(actor, edited.Department, edited.Confidentiality), "UPDATE");
            if (document.HasOriginal) await CheckQuotaAsync(db, edited.Department, document.FileSizeBytes, document.Id);
            document.Title = edited.Title.Trim();
            document.Author = edited.Author.Trim();
            document.Department = edited.Department;
            document.Category = edited.Category;
            document.Version = edited.Version.Trim();
            document.Confidentiality = edited.Confidentiality;
            document.ExecutiveSummary = edited.ExecutiveSummary.Trim();
            document.Keywords = edited.Keywords;
            document.Tags = edited.Tags;
            ValidateDocument(document);
        });
    }

    public Task DeleteAsync(string id) => MutateDocumentAsync(id, "DELETE", async (db, actor, document) =>
    {
        await RequireAsync(db, actor, Permissions.CanAdmin(actor), "DELETE");
        db.Documents.Remove(document);
    });

    public async Task ReprocessAsync(string id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var actor = await ActorAsync(db);
        var existing = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id);
        await RequireAsync(db, actor, existing is not null && Permissions.CanRead(actor, existing) && Permissions.CanWrite(actor), "REPROCESS");
        var file = await db.Files.AsNoTracking().SingleOrDefaultAsync(f => f.DocumentId == id)
            ?? throw new InvalidOperationException("No hay PDF original para reprocesar este expediente.");
        // El parseo PDF (trabajo de CPU) se ejecuta fuera de la transacción y del lock de mutaciones.
        var processed = new DocumentItem { Id = existing!.Id, InternalCode = existing.InternalCode, Title = existing.Title };
        await Task.Run(() => processor.Process(processed, file.Bytes));
        await MutateDocumentAsync(id, "REPROCESS", (_, _, document) =>
        {
            document.Pages = processed.Pages;
            document.PageCount = processed.PageCount;
            document.Sha256Hash = processed.Sha256Hash;
            document.FileSizeBytes = processed.FileSizeBytes;
            document.HasOriginal = processed.HasOriginal;
            document.OcrStatus = processed.OcrStatus;
            document.ExecutiveSummary = processed.ExecutiveSummary;
            document.Keywords = processed.Keywords;
            return Task.CompletedTask;
        });
    }

    public async Task AddNoteAsync(string id, string text, int page)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4000) throw new ValidationException("La nota debe tener entre 1 y 4000 caracteres.");
        await MutateDocumentAsync(id, "NOTE", (_, actor, document) =>
        {
            if (page < 1 || page > document.PageCount) throw new ValidationException("Página de la nota inválida.");
            document.Notes = [..document.Notes, new(Catalog.NewId(), actor.Name, actor.Role, DateTimeOffset.UtcNow, text.Trim(), page)];
            return Task.CompletedTask;
        }, writeRequired: false);
    }

    private async Task MutateDocumentAsync(string id, string action, Func<AppDbContext, AppUser, DocumentItem, Task> mutation, bool writeRequired = true)
    {
        await using var db = await factory.CreateDbContextAsync();
        var actor = await ActorAsync(db);
        var existing = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id);
        await RequireAsync(db, actor, existing is not null && Permissions.CanRead(actor, existing) &&
            (!writeRequired || Permissions.CanWrite(actor)), action);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await DatabaseSetup.LockAsync(db);
        actor = await ActorAsync(db);
        var document = await db.Documents.SingleAsync(d => d.Id == id);
        await RequireAsync(db, actor, Permissions.CanRead(actor, document) &&
            (!writeRequired || Permissions.CanWrite(actor)), action);
        await mutation(db, actor, document);
        Audit(db, actor, action, $"{action}: {document.Title}", document.InternalCode);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task SaveUserAsync(AppUser input, bool delete = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        var actor = await ActorAsync(db);
        await RequireAsync(db, actor, Permissions.CanAdmin(actor), "ADMIN_USERS");
        Validate(input);
        if (!Catalog.Roles.Contains(input.Role) || input.Status is not ("Activo" or "Inactivo"))
            throw new ValidationException("Rol o estado inválido.");
        if (input.Id == actor.Id && (delete || input.Status != "Activo" || input.Role != "admin_general"))
            throw new ValidationException("No puedes eliminar, desactivar ni degradar tu propio perfil de administrador.");
        await using var tx = await db.Database.BeginTransactionAsync();
        await DatabaseSetup.LockAsync(db);
        actor = await ActorAsync(db);
        await RequireAsync(db, actor, Permissions.CanAdmin(actor), "ADMIN_USERS");
        if (!await db.Departments.AnyAsync(d => d.Name == input.Department)) throw new ValidationException("Departamento inválido.");
        var existing = await db.Users.SingleOrDefaultAsync(u => u.Id == input.Id);
        if (delete)
        {
            if (existing is null) throw new ValidationException("El usuario ya no existe.");
            db.Users.Remove(existing);
        }
        else
        {
            input.Email = input.Email.Trim().ToLowerInvariant();
            if (await db.Users.AnyAsync(u => u.Email == input.Email && u.Id != input.Id))
                throw new ValidationException("El correo ya está registrado.");
            if (existing is null) db.Users.Add(input);
            else db.Entry(existing).CurrentValues.SetValues(input);
        }
        Audit(db, actor, delete ? "DELETE_USER" : "UPDATE_USER", $"{input.Name} ({input.Email})");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task SaveDepartmentAsync(DepartmentInfo input)
    {
        Validate(input);
        await using var db = await factory.CreateDbContextAsync();
        var actor = await ActorAsync(db);
        await RequireAsync(db, actor, Permissions.CanAdmin(actor), "ADMIN_DEPARTMENTS");
        await using var tx = await db.Database.BeginTransactionAsync();
        await DatabaseSetup.LockAsync(db);
        actor = await ActorAsync(db);
        await RequireAsync(db, actor, Permissions.CanAdmin(actor), "ADMIN_DEPARTMENTS");
        var existing = await db.Departments.SingleOrDefaultAsync(d => d.Id == input.Id);
        if (existing is not null && existing.Name != input.Name)
            throw new ValidationException("El nombre de un departamento existente no se puede cambiar.");
        if (await db.Departments.AnyAsync(d => d.Name == input.Name && d.Id != input.Id))
            throw new ValidationException("El departamento ya existe.");
        var used = await db.Documents.Where(d => d.Department == input.Name && d.HasOriginal).SumAsync(d => d.FileSizeBytes);
        if (used > input.AllocatedStorageMB * 1024L * 1024)
            throw new ValidationException("La cuota no puede ser menor que el almacenamiento ocupado.");
        if (existing is null) db.Departments.Add(input);
        else db.Entry(existing).CurrentValues.SetValues(input);
        Audit(db, actor, "UPDATE_DEPARTMENT", $"{input.Name}: {input.AllocatedStorageMB} MB");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task LogQueryAsync(string action, string query)
    {
        if (action is not ("SEARCH" or "AGENT_QUERY")) throw new ValidationException("Tipo de consulta inválido.");
        await using var db = await factory.CreateDbContextAsync();
        var actor = await ActorAsync(db);
        Audit(db, actor, action, TextAnalysis.Truncate(query, 2000));
        await db.SaveChangesAsync();
    }

    public async Task<List<AuditEvent>> ExportAuditAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var actor = await ActorAsync(db);
        await RequireAsync(db, actor, Permissions.CanAudit(actor), "EXPORT_AUDIT");
        return await db.Audit.AsNoTracking().OrderByDescending(a => a.TimestampUTC).ToListAsync();
    }

    public async Task ResetAsync(string confirmation)
    {
        if (!string.Equals(confirmation, "RESTABLECER", StringComparison.Ordinal))
            throw new ValidationException("Escribe RESTABLECER para confirmar el restablecimiento de los datos de ejemplo.");
        await using var db = await factory.CreateDbContextAsync();
        var actor = await ActorAsync(db);
        await RequireAsync(db, actor, Permissions.CanAdmin(actor), "RESET");
        await using var tx = await db.Database.BeginTransactionAsync();
        await DatabaseSetup.LockAsync(db);
        actor = await ActorAsync(db);
        await RequireAsync(db, actor, Permissions.CanAdmin(actor), "RESET");
        await db.Files.ExecuteDeleteAsync();
        await db.Documents.ExecuteDeleteAsync();
        await db.Users.ExecuteDeleteAsync();
        await db.Departments.ExecuteDeleteAsync();
        var seed = DatabaseSetup.ReadSeed();
        db.Departments.AddRange(seed.Departments);
        db.Users.AddRange(seed.Users);
        db.Documents.AddRange(seed.Documents);
        Audit(db, actor, "RESET", "Datos de ejemplo restaurados por confirmación del administrador. La bitácora se conserva.");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        // La identidad no se reasigna aquí: procede de la cookie autenticada.
        session.LastDocumentCode = null;
    }
}
