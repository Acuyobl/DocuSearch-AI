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
        var maxTokens = Math.Clamp(settings.MaxTokens, 64, 4096);
        var overlap = Math.Clamp(settings.OverlapTokens, 0, maxTokens / 2);
        var step = Math.Max(1, maxTokens - overlap);
        var result = new List<RagChunk>();
        var chunkIndex = 0;
        var pages = document.Pages.Count > 0 ? document.Pages : [new RagPage(1, document.Text)];
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(page.Text)) continue;
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
        return Task.FromResult<IReadOnlyList<RagChunk>>(result);
    }
}

/// <summary>
/// Chunking semántico aproximado: agrupa oraciones mientras la similitud con la
/// oración anterior no caiga por debajo del umbral, sin superar MaxTokens.
/// </summary>
public sealed class SemanticRagChunker(IOptions<RagOptions> options, IRagEmbedder embedder,
    FixedRagChunker fallback) : IRagChunker
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
            var sentences = Regex.Matches(page.Text, @"[^.!?;\r\n]+[.!?;]?")
                .Select(match => (Text: match.Value.Trim(), match.Index, Length: match.Length))
                .Where(item => item.Text.Length > 0).ToList();
            if (sentences.Count == 0)
            {
                result.AddRange(await fallback.ChunkAsync(new RagDocument(document.SourceId, document.SourceType,
                    document.Title, page.Text, [page], document.Metadata), cancellationToken));
                continue;
            }

            var current = new StringBuilder();
            var currentStart = sentences[0].Index;
            var currentEnd = currentStart;
            var currentTokens = 0;
            float[]? previous = null;
            foreach (var sentence in sentences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sentenceTokens = Regex.Matches(sentence.Text, @"\S+").Count;
                float[]? vector = null;
                try { vector = await embedder.EmbedAsync(sentence.Text, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { vector = null; }

                var similarity = vector is not null && previous is not null ? Cosine(previous, vector) : 1;
                var shouldBreak = currentTokens > 0 &&
                    (currentTokens + sentenceTokens > maxTokens ||
                     (vector is not null && previous is not null && similarity < settings.SemanticBreakThreshold));

                if (shouldBreak)
                {
                    AddChunk(result, document, ref chunkIndex, page.PageNumber, currentStart, currentEnd, current.ToString());
                    current.Clear();
                    currentStart = sentence.Index;
                    currentTokens = 0;
                }
                if (current.Length > 0) current.Append(' ');
                current.Append(sentence.Text);
                currentEnd = sentence.Index + sentence.Length;
                currentTokens += sentenceTokens;
                previous = vector;
            }
            AddChunk(result, document, ref chunkIndex, page.PageNumber, currentStart, currentEnd, current.ToString());
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
