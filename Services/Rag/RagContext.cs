namespace DocuSearch.Services.Rag;

/// <summary>Límites del contexto que recibe el modelo. Un único sitio para que la lista de fuentes que ve el
/// usuario coincida con la que leyó el modelo.</summary>
public static class RagContextLimits
{
    /// <summary>Fuentes (páginas) por pregunta concreta.</summary>
    public const int MaxSources = 8;
    /// <summary>Caracteres por fuente en preguntas concretas (≈ dos fragmentos de 300 palabras).</summary>
    public const int MaxSourceChars = 4000;
    /// <summary>Fuentes en resúmenes y análisis de documentos.</summary>
    public const int MaxSynthesisSources = 10;
    /// <summary>Caracteres por fuente en resúmenes y análisis.</summary>
    public const int MaxSynthesisSourceChars = 2500;
    /// <summary>Tope de fuentes que acepta el cliente generativo.</summary>
    public const int MaxModelSources = MaxSynthesisSources;
}

/// <summary>Una página como fuente: texto de sus fragmentos recuperados, fusionado y sin solapes.</summary>
public sealed record RagPageSource(RagChunk First, string Text, double Score, int ChunkCount);

public static class RagContext
{
    /// <summary>
    /// Agrupa los fragmentos por documento y página (una cita por página) en el orden de relevancia del
    /// mejor fragmento de cada página. Antes se descartaba todo fragmento salvo el primero de cada página;
    /// ahora se unen en orden de aparición, quitando el texto que se repite por el solape del troceado.
    /// </summary>
    public static List<RagPageSource> MergeByPage(IReadOnlyList<RagHit> hits, int maxChars = RagContextLimits.MaxSourceChars)
    {
        return hits
            .GroupBy(hit => (hit.Chunk.DocumentId, hit.Chunk.PageNumber))
            .Select(group =>
            {
                var ordered = group.OrderBy(hit => hit.Chunk.StartOffset).Select(hit => hit.Chunk).ToList();
                return new RagPageSource(ordered[0], TextAnalysis.Truncate(Join(ordered), maxChars),
                    group.Max(hit => hit.Score), ordered.Count);
            })
            .OrderByDescending(source => source.Score)
            .ToList();
    }

    /// <summary>Concatena fragmentos ordenados por posición; el solape se recorta y los huecos se marcan con «…».</summary>
    public static string Join(IReadOnlyList<RagChunk> ordered)
    {
        if (ordered.Count == 0) return "";
        var text = ordered[0].Text;
        var end = ordered[0].EndOffset;
        foreach (var chunk in ordered.Skip(1))
        {
            // Solo se recorta el solape si el texto corresponde exactamente a sus offsets en la página.
            var exact = chunk.Text.Length == chunk.EndOffset - chunk.StartOffset;
            if (exact && chunk.EndOffset <= end) continue; // contenido ya incluido
            if (exact && chunk.StartOffset < end)
            {
                var skip = end - chunk.StartOffset;
                text += skip < chunk.Text.Length ? chunk.Text[skip..] : "";
            }
            else
            {
                text += "\n…\n" + chunk.Text;
            }
            end = chunk.EndOffset;
        }
        return text;
    }
}
