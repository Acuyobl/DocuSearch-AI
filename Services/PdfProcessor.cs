using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DocuSearch.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DocuSearch.Services;

public static class TextAnalysis
{
    private static readonly HashSet<string> StopWords = new(
        "de la que el en y a los del se las por un para con no una su al lo como mas sus le sobre entre este esta ese documento documentos buscar busca mostrar muestra resume resumen extrae entidades pagina cuales cuanto cuantos lista listado tiene cual son hay".Split(' '));

    public static string Normalize(string value) => string.Concat(value.ToLowerInvariant()
        .Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark));
    private static IEnumerable<string> Tokens(string value) => Regex.Matches(Normalize(value), @"[\p{L}\d]+")
        .Select(m => m.Value).Where(t => t.Length > 1 && !StopWords.Contains(t))
        .Select(t => t.Length > 5 && t.EndsWith('s') ? t[..^1] : t);
    public static string[] Terms(string value) => Tokens(value).Distinct().ToArray();
    public static string Truncate(string value, int length) => value.Length <= length ? value : value[..length] + "…";
    public static string Summary(string text)
    {
        var sentences = Regex.Split(text, @"(?<=[.!?;])\s+|\r?\n").Where(s => s.Trim().Length > 20).ToList();
        if (sentences.Count == 0) return Truncate(text.Trim(), 1200);
        var frequencies = Tokens(text).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        return Truncate(string.Join(" ", sentences.Select((s, i) => new
            {
                Text = s.Trim(), Index = i,
                Score = Terms(s).Sum(t => frequencies.GetValueOrDefault(t)) / Math.Sqrt(Math.Max(1, Terms(s).Length))
            }).OrderByDescending(s => s.Score).Take(3).OrderBy(s => s.Index).Select(s => s.Text)), 1200);
    }
    public static List<DocumentEntity> Entities(string text, int page)
    {
        (string Type, string Pattern)[] patterns = [
            ("MONEY", @"(?:\$|USD|MXN|EUR)\s?\d[\d.,]*(?:\s?(?:millones|mil|USD|MXN))?"),
            ("DATE", @"\b\d{1,2}[/-]\d{1,2}[/-]\d{2,4}\b|\b\d{4}-\d{2}-\d{2}\b|\b\d{1,2}\s+de\s+\p{L}+\s+de\s+\d{4}\b"),
            ("LAW", @"\b(?:Ley|Reglamento|Decreto|Código|Norma|NOM|ISO|Artículo)\s+[\p{L}\d ./:-]{2,70}"),
            ("PERSON", @"\b(?:Lic|Ing|Dr|Dra|Mtro|Mtra|C\.P)\.\s+(?:[A-ZÁÉÍÓÚÑ][\p{L}]+\s?){2,4}"),
            ("ORGANIZATION", @"\b(?:[A-ZÁÉÍÓÚÑ][\p{L}&]+\s){1,4}(?:Corp|LLC|Inc|S\.A\.|Ltd)\b")
        ];
        return patterns.SelectMany(p => Regex.Matches(text, p.Pattern, RegexOptions.None, TimeSpan.FromSeconds(1))
            .Select(m => new DocumentEntity(Catalog.NewId(), m.Value.Trim(), p.Type, page)))
            .DistinctBy(e => (e.Type, e.Text)).Take(30).ToList();
    }
    public static DocumentPage AnalyzePage(int number, string text) => new(number, text, Summary(text), Entities(text, number));
}

public sealed class PdfProcessor
{
    public const long MaxFileBytes = 25 * 1024 * 1024;
    public const int MaxPages = 500;

    public void Process(DocumentItem document, byte[] bytes)
    {
        if (bytes.Length is 0 || bytes.LongLength > MaxFileBytes)
            throw new InvalidDataException("El PDF debe tener entre 1 byte y 25 MB.");
        if (bytes.Length < 5 || Encoding.ASCII.GetString(bytes, 0, 5) != "%PDF-")
            throw new InvalidDataException("El archivo no tiene una cabecera PDF válida.");
        using var pdf = PdfDocument.Open(bytes);
        if (pdf.NumberOfPages > MaxPages)
            throw new InvalidDataException($"El límite es de {MaxPages} páginas por expediente.");
        var pages = new List<DocumentPage>();
        var totalChars = 0;
        foreach (var page in pdf.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page);
            totalChars += text.Length;
            if (totalChars > 5_000_000)
                throw new InvalidDataException("El PDF supera el límite total de cinco millones de caracteres.");
            if (text.Length > 250000)
                throw new InvalidDataException($"La página {page.Number} supera el límite de texto admitido.");
            pages.Add(TextAnalysis.AnalyzePage(page.Number, text));
        }
        document.Pages = pages;
        document.PageCount = pdf.NumberOfPages;
        document.Sha256Hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        document.FileSizeBytes = bytes.LongLength;
        document.HasOriginal = true;
        var allText = string.Join("\n", pages.Select(p => p.ExtractedText));
        document.OcrStatus = pages.Any(p => string.IsNullOrWhiteSpace(p.ExtractedText)) ? "Requiere OCR" : "Completado";
        document.ExecutiveSummary = string.IsNullOrWhiteSpace(allText)
            ? "PDF sin capa de texto. Se requiere un motor OCR de imágenes; no se ha simulado su extracción."
            : TextAnalysis.Summary(allText);
        document.Keywords = TextAnalysis.Terms(allText).Take(20).ToArray();
    }
}
