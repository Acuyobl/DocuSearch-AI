using System.Text.Json;
using DocuSearch.Models;
using DocuSearch.Services;
using Microsoft.Data.SqlClient;
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
        await MigrateWithRetryAsync(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await LockAsync(db);
        if (DemoPolicy.SeedOnInitialize && !await db.Users.AnyAsync())
        {
            var seed = ReadSeed();
            db.Departments.AddRange(seed.Departments);
            db.Users.AddRange(seed.Users);
            db.Documents.AddRange(seed.Documents);
            db.Audit.Add(new AuditEvent
            {
                UserName = "Sistema", UserRole = "system", Action = "INITIALIZE",
                Details = "Esquema SQL Server inicializado con tres usuarios y cinco expedientes de ejemplo sin PDF original."
            });
            await db.SaveChangesAsync();
        }
        await transaction.CommitAsync();
    }

    // SQL Server puede tardar varios minutos en recuperar la base después de un reinicio
    // del contenedor. La app no debe morir por arrancar antes de que termine.
    private static async Task MigrateWithRetryAsync(AppDbContext db)
    {
        const int attempts = 36;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync();
                return;
            }
            catch (SqlException ex) when (attempt < attempts && IsTransient(ex))
            {
                Console.WriteLine($"SQL Server aún no está disponible ({ex.Number}); reintento {attempt}/{attempts} en 10 s.");
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }
    }

    private static bool IsTransient(SqlException exception) =>
        exception.Number is -2 or 0 or 233 or 1205 or 1801 or 4060 or 40613 or 40197 or 18456;

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
