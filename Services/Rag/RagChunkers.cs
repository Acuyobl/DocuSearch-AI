using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services.Rag;

internal static class RagChunkFactory
{
    public static RagChunk Create(RagDocument document, int chunkIndex, int pageNumber,
        int start, int end, string text)
    {
        var normalized = text.Trim();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var code = document.Metadata.GetValueOrDefault("code", document.SourceId);
        return new RagChunk($"{document.SourceId}:{chunkIndex}", document.SourceId, code,
            document.SourceType, pageNumber, chunkIndex, start, end, normalized, hash);
    }
}

/// <summary>Chunking fijo por tokens aproximados (palabras) con solape configurable.</summary>
public sealed class FixedRagChunker(IOptions<RagOptions> options) : IRagChunker
{
    private readonly RagOptions settings = options.Value;
    public string Strategy => "fixed";

    public Task<IReadOnlyList<RagChunk>> ChunkAsync(RagDocument document, CancellationToken cancellationToken = default)
    {
        var result = new List<RagChunk>();
        var chunkIndex = 0;
        var pages = document.Pages.Count > 0 ? document.Pages : [new RagPage(1, document.Text)];
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChunkPage(document, page, ref chunkIndex, result);
        }
        return Task.FromResult<IReadOnlyList<RagChunk>>(result);
    }

    /// <summary>
    /// Trocea una página continuando la numeración recibida, para que el chunker
    /// semántico pueda usarlo como respaldo sin repetir identificadores.
    /// </summary>
    internal void ChunkPage(RagDocument document, RagPage page, ref int chunkIndex, List<RagChunk> result)
    {
        if (string.IsNullOrWhiteSpace(page.Text)) return;
        var maxTokens = Math.Clamp(settings.MaxTokens, 64, 4096);
        var overlap = Math.Clamp(settings.OverlapTokens, 0, maxTokens / 2);
        var step = Math.Max(1, maxTokens - overlap);
        var tokens = Regex.Matches(page.Text, @"\S+").Select(match => (Start: match.Index, End: match.Index + match.Length)).ToList();
        for (var start = 0; start < tokens.Count; start += step)
        {
            var end = Math.Min(start + maxTokens, tokens.Count);
            if (end <= start) break;
            var text = page.Text[tokens[start].Start..tokens[end - 1].End];
            result.Add(RagChunkFactory.Create(document, chunkIndex++, page.PageNumber,
                tokens[start].Start, tokens[end - 1].End, text));
            if (end == tokens.Count) break;
        }
    }
}

/// <summary>
/// Chunking semántico aproximado: agrupa oraciones mientras la similitud con la
/// oración anterior no caiga por debajo del umbral, sin superar MaxTokens.
/// </summary>
public sealed class SemanticRagChunker(IOptions<RagOptions> options, IRagEmbedder embedder,
    FixedRagChunker fallback, ILogger<SemanticRagChunker>? logger = null) : IRagChunker
{
    private readonly RagOptions settings = options.Value;
    public string Strategy => "semantic";

    public async Task<IReadOnlyList<RagChunk>> ChunkAsync(RagDocument document, CancellationToken cancellationToken = default)
    {
        if (!embedder.Enabled) return await fallback.ChunkAsync(document, cancellationToken);
        var maxTokens = Math.Clamp(settings.MaxTokens, 64, 4096);
        var result = new List<RagChunk>();
        var chunkIndex = 0;
        var pages = document.Pages.Count > 0 ? document.Pages : [new RagPage(1, document.Text)];
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(page.Text)) continue;
            var segments = Segments(page.Text, maxTokens);
            if (segments.Count == 0)
            {
                fallback.ChunkPage(document, page, ref chunkIndex, result);
                continue;
            }

            IReadOnlyList<float[]> vectors;
            try
            {
                vectors = await embedder.EmbedBatchAsync(segments.Select(segment => segment.Text).ToList(), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Sin vectores no hay criterio semántico: la página se trocea por tamaño.
                logger?.LogWarning("Chunking semántico sin embeddings en página {Page} ({Error}); se usa el fijo.",
                    page.PageNumber, ex.GetType().Name);
                fallback.ChunkPage(document, page, ref chunkIndex, result);
                continue;
            }

            var current = new StringBuilder();
            var currentStart = segments[0].Start;
            var currentEnd = currentStart;
            var currentTokens = 0;
            float[]? previous = null;
            for (var index = 0; index < segments.Count; index++)
            {
                var segment = segments[index];
                var vector = index < vectors.Count ? vectors[index] : null;
                var shouldBreak = currentTokens > 0 &&
                    (currentTokens + segment.Tokens > maxTokens ||
                     (vector is not null && previous is not null &&
                      Cosine(previous, vector) < settings.SemanticBreakThreshold));

                if (shouldBreak)
                {
                    AddChunk(result, document, ref chunkIndex, page.PageNumber, currentStart, currentEnd, current.ToString());
                    current.Clear();
                    currentStart = segment.Start;
                    currentTokens = 0;
                }
                if (current.Length > 0) current.Append(' ');
                current.Append(segment.Text);
                currentEnd = segment.End;
                currentTokens += segment.Tokens;
                previous = vector;
            }
            AddChunk(result, document, ref chunkIndex, page.PageNumber, currentStart, currentEnd, current.ToString());
        }
        return result;
    }

    /// <summary>
    /// Divide el texto en oraciones y parte por palabras las que superan maxTokens,
    /// de modo que ningún segmento por sí solo exceda el tamaño máximo del fragmento.
    /// </summary>
    internal static List<(string Text, int Start, int End, int Tokens)> Segments(string text, int maxTokens)
    {
        var result = new List<(string Text, int Start, int End, int Tokens)>();
        foreach (Match sentence in Regex.Matches(text, @"[^.!?;\r\n]+[.!?;]?"))
        {
            var words = Regex.Matches(sentence.Value, @"\S+");
            if (words.Count == 0) continue;
            for (var start = 0; start < words.Count; start += maxTokens)
            {
                var end = Math.Min(start + maxTokens, words.Count) - 1;
                var from = sentence.Index + words[start].Index;
                var to = sentence.Index + words[end].Index + words[end].Length;
                result.Add((text[from..to], from, to, end - start + 1));
            }
        }
        return result;
    }

    private static void AddChunk(List<RagChunk> chunks, RagDocument document, ref int chunkIndex,
        int pageNumber, int start, int end, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        chunks.Add(RagChunkFactory.Create(document, chunkIndex++, pageNumber, start, end, text));
    }

    private static double Cosine(float[] left, float[] right)
    {
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }
        return leftNorm == 0 || rightNorm == 0 ? 0 : dot / Math.Sqrt(leftNorm * rightNorm);
    }
}
