namespace DocuSearch.Services;

/// <summary>
/// Consulta Pinecone para una pregunta y devuelve solo páginas autorizadas.
/// Si Pinecone falla, la búsqueda local sigue funcionando como respaldo.
/// </summary>
public sealed class PineconeRetriever(PineconeClient client, ILogger<PineconeRetriever> logger)
{
    public bool Enabled => client.Enabled;

    public async Task<IReadOnlyDictionary<(string DocumentId, int PageNumber), double>> SearchAsync(
        float[] queryVector, IReadOnlyCollection<string> readableDocumentIds, CancellationToken cancellationToken = default)
    {
        if (!Enabled || readableDocumentIds.Count == 0) return new Dictionary<(string, int), double>();
        try
        {
            var matches = await client.QueryAsync(queryVector, readableDocumentIds, cancellationToken);
            var scores = new Dictionary<(string, int), double>();
            foreach (var match in matches)
            {
                if (!readableDocumentIds.Contains(match.DocumentId, StringComparer.Ordinal)) continue;
                var key = (match.DocumentId, match.PageNumber);
                if (!scores.TryGetValue(key, out var current) || match.Score > current) scores[key] = match.Score;
            }
            return scores;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Pinecone no disponible ({Error}); se usa la búsqueda local.", ex.GetType().Name);
            return new Dictionary<(string, int), double>();
        }
    }
}
