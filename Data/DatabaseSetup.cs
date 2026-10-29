using System.Text.Json;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuSearch.Data;

public static class DatabaseSetup
{
    public static SeedData ReadSeed()
    {
        using var stream = typeof(DatabaseSetup).Assembly.GetManifestResourceStream("DocuSearch.Data.seed.json")
            ?? throw new InvalidOperationException("No se encontraron los datos de ejemplo.");
        return JsonSerializer.Deserialize<SeedData>(stream, Catalog.Json)
            ?? throw new InvalidDataException("Datos de ejemplo inválidos.");
    }

    public static async Task InitializeAsync(AppDbContext db)
    {
        await db.Database.MigrateAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await LockAsync(db);
        if (!await db.Users.AnyAsync())
        {
            var seed = ReadSeed();
            db.Departments.AddRange(seed.Departments);
            db.Users.AddRange(seed.Users);
            db.Documents.AddRange(seed.Documents);
            db.Audit.Add(new AuditEvent
            {
                UserName = "Sistema", UserRole = "system", Action = "INITIALIZE",
                Details = "Esquema SQL Server inicializado con cinco expedientes de ejemplo sin PDF original."
            });
            await db.SaveChangesAsync();
        }
        await transaction.CommitAsync();
    }

    // Serializa las mutaciones, incluidas cuotas, contadores y notas, entre todos los circuitos.
    public static Task LockAsync(AppDbContext db) =>
        db.Database.ExecuteSqlRawAsync("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = N'DocuSearch.DocumentMutations',
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = 15000;
            IF @result < 0
                THROW 51000, N'No se pudo obtener el bloqueo transaccional de DocuSearch. Reintenta la operación.', 1;
            """);
}
