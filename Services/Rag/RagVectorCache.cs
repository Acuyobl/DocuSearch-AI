using DocuSearch.Data;
using Microsoft.EntityFrameworkCore;

namespace DocuSearch.Services.Rag;

/// <summary>
/// Vectores de fragmento normalizados en memoria (sin texto) para que <see cref="SqlRagVectorStore"/>
/// no traiga de SQL todos los fragmentos en cada consulta. Es un singleton por proceso.
/// </summary>
/// <remarks>
/// Frescura: cada consulta lee una huella barata de la tabla (número de filas y último
/// <c>CreatedUtc</c>). Toda escritura de fragmentos fija <c>CreatedUtc</c> al momento actual y
/// todo borrado cambia el número de filas, así que la caché detecta cambios hechos por cualquier
/// instancia o proceso (p. ej. <c>--rag-reindex</c> o el borrado en cascada de un documento) y se
/// recarga entera. Memoria aproximada: 4 KB por fragmento con vectores de 1024 dimensiones.
/// </remarks>
public sealed class RagVectorCache(ILogger<RagVectorCache>? logger = null)
{
    public sealed record Entry(string ChunkId, string DocumentId, string Model, int Dimensions, float[] Unit);

    private sealed record State(long Count, DateTimeOffset? LastWrite, ILookup<string, Entry> ByDocument);

    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile State? state;

    /// <summary>Número de recargas completas; sirve para diagnóstico y pruebas.</summary>
    public int Reloads { get; private set; }

    /// <summary>Fragmentos en memoria agrupados por documento, recargados si la tabla cambió.</summary>
    public async Task<ILookup<string, Entry>> GetAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var (count, lastWrite) = await FingerprintAsync(db, cancellationToken);
        if (state is { } current && current.Count == count && current.LastWrite == lastWrite)
            return current.ByDocument;

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (state is { } fresh && fresh.Count == count && fresh.LastWrite == lastWrite)
                return fresh.ByDocument;
            // La huella se leyó antes que las filas: si alguien escribe entre ambas lecturas, la
            // siguiente consulta verá otra huella y volverá a recargar. Nunca se sirve algo más viejo.
            var rows = await db.RagChunks.AsNoTracking()
                .Select(record => new { record.Id, record.DocumentId, record.Model, record.Dimensions, record.Vector })
                .ToListAsync(cancellationToken);
            var byDocument = rows
                .Select(row => new Entry(row.Id, row.DocumentId, row.Model, row.Dimensions,
                    Normalize(SqlRagVectorStore.FromBytes(row.Vector))))
                .ToLookup(entry => entry.DocumentId, StringComparer.Ordinal);
            state = new State(count, lastWrite, byDocument);
            Reloads++;
            logger?.LogInformation("RAG: caché de vectores recargada con {Count} fragmentos.", rows.Count);
            return byDocument;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<(long Count, DateTimeOffset? LastWrite)> FingerprintAsync(AppDbContext db,
        CancellationToken cancellationToken)
    {
        var fingerprint = await db.RagChunks.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(group => new { Count = group.LongCount(), LastWrite = group.Max(record => (DateTimeOffset?)record.CreatedUtc) })
            .SingleOrDefaultAsync(cancellationToken);
        return fingerprint is null ? (0, null) : (fingerprint.Count, fingerprint.LastWrite);
    }

    /// <summary>Copia unitaria del vector; un vector nulo se deja a ceros (similitud 0, como antes).</summary>
    public static float[] Normalize(float[] vector)
    {
        double norm = 0;
        foreach (var value in vector) norm += value * value;
        if (norm == 0) return new float[vector.Length];
        var scale = 1 / Math.Sqrt(norm);
        var unit = new float[vector.Length];
        for (var index = 0; index < vector.Length; index++) unit[index] = (float)(vector[index] * scale);
        return unit;
    }

    /// <summary>Producto escalar acumulado en double: con vectores unitarios equivale al coseno.</summary>
    public static double Dot(float[] left, float[] right)
    {
        if (left.Length == 0 || left.Length != right.Length) return 0;
        double sum = 0;
        for (var index = 0; index < left.Length; index++) sum += left[index] * right[index];
        return sum;
    }
}
