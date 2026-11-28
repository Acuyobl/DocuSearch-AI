using System.Text.RegularExpressions;
using DocuSearch.Models;

namespace DocuSearch.Services.Rag;

/// <summary>
/// Corrección conservadora de erratas de una sola edición usando el vocabulario de los
/// títulos y palabras clave de los expedientes legibles. Evita que una errata como
/// «Fabic» impida encontrar el expediente «Microsoft Fabric».
/// </summary>
public static class QueryCorrector
{
    public static string Correct(string query, IReadOnlyList<DocumentItem> documents)
    {
        if (string.IsNullOrWhiteSpace(query) || documents.Count == 0) return query;
        var vocabulary = documents
            .SelectMany(document => TextAnalysis.Terms(
                $"{document.Title} {string.Join(' ', document.Keywords)}"))
            .Where(term => term.Length >= 4)
            .GroupBy(term => term, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        if (vocabulary.Count == 0) return query;

        var result = query;
        foreach (var term in TextAnalysis.Terms(query).Where(term => term.Length >= 4).Distinct(StringComparer.Ordinal))
        {
            if (vocabulary.ContainsKey(term)) continue;
            var correction = vocabulary
                .Where(candidate => Math.Abs(candidate.Key.Length - term.Length) <= 1 &&
                    Distance(term, candidate.Key) == 1)
                .OrderByDescending(candidate => candidate.Value)
                .ThenBy(candidate => candidate.Key, StringComparer.Ordinal)
                .Select(candidate => candidate.Key)
                .FirstOrDefault();
            if (correction is null) continue;
            result = Regex.Replace(result, $@"\b{Regex.Escape(term)}\b", correction,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return result;
    }

    private static int Distance(string left, string right)
    {
        if (left == right) return 0;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var row = 1; row <= left.Length; row++)
        {
            var current = new int[right.Length + 1];
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(Math.Min(
                    current[column - 1] + 1, previous[column] + 1), previous[column - 1] + cost);
            }
            previous = current;
        }
        return previous[right.Length];
    }
}
