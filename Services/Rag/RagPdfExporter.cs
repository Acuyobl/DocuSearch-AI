using System.Text;
using System.Text.RegularExpressions;
using DocuSearch.Models;

namespace DocuSearch.Services.Rag;

/// <summary>
/// Genera un PDF sencillo y autocontenido para una respuesta del asistente, sin
/// dependencias nativas ni servicios externos. Usa fuentes estándar PDF y WinAnsi.
/// </summary>
public static partial class RagPdfExporter
{
    private const float PageWidth = 595;
    private const float PageHeight = 842;
    private const float Margin = 50;
    private const float Bottom = 55;

    [GeneratedRegex(@"\*\*|__|`")]
    private static partial Regex MarkdownMarks();

    [GeneratedRegex(@"(?m)^#{1,6}\s*")]
    private static partial Regex HeadingMarks();

    public static byte[] Build(string question, string answer, IReadOnlyList<SearchHit> sources)
    {
        var title = "DocuSearch AI · Resumen documental";
        var blocks = new List<(string Text, string Font, float Size, float Before, float After)>
        {
            (title, "F2", 16, 0, 10),
            ($"Consulta: {TextAnalysis.Truncate(question, 500)}", "F1", 9, 0, 8),
            ($"Generado: {DateTimeOffset.Now:dd/MM/yyyy HH:mm}", "F1", 9, 0, 18)
        };

        var clean = HeadingMarks().Replace(MarkdownMarks().Replace(answer, ""), "");
        foreach (var paragraph in clean.Replace("\r\n", "\n").Split('\n'))
        {
            var text = Regex.Replace(paragraph, @"\s+", " ").Trim();
            if (text.Length == 0) continue;
            foreach (var line in Wrap(text, 92))
                blocks.Add((line, "F1", 10, 0, 6));
            blocks.Add(("", "F1", 4, 0, 2));
        }

        if (sources.Count > 0)
        {
            blocks.Add(("Fuentes consultadas", "F2", 12, 12, 8));
            var seen = new HashSet<(string, int)>();
            var number = 0;
            foreach (var source in sources)
            {
                if (!seen.Add((source.Document.InternalCode, source.PageNumber))) continue;
                number++;
                var line = $"{number}. {source.Document.InternalCode} · {source.Document.Title} · página {source.PageNumber}";
                foreach (var wrapped in Wrap(line, 92))
                    blocks.Add((wrapped, "F1", 9, 0, 5));
            }
        }

        blocks.Add(("Documento generado por DocuSearch AI a partir de fuentes autorizadas. Verifica siempre las citas.", "F3", 8, 14, 0));
        return WritePdf(blocks);
    }

    private static byte[] WritePdf(List<(string Text, string Font, float Size, float Before, float After)> blocks)
    {
        var pages = new List<List<(string Text, string Font, float Size, float Y)>>();
        var current = new List<(string, string, float, float)>();
        var y = PageHeight - Margin;
        foreach (var block in blocks)
        {
            y -= block.Before;
            var height = block.Text.Length == 0 ? 4 : block.Size * 1.35f + block.After;
            if (y - height < Bottom && current.Count > 0)
            {
                pages.Add(current);
                current = [];
                y = PageHeight - Margin;
            }
            if (block.Text.Length > 0)
                current.Add((block.Text, block.Font, block.Size, y));
            y -= height;
        }
        if (current.Count > 0) pages.Add(current);
        if (pages.Count == 0) pages.Add([]);

        var objects = new List<byte[]>();
        var pageObjectIds = new List<int>();
        var contentObjectIds = new List<int>();
        // 1 catálogo, 2 pages, 3-5 fuentes; después páginas y contenidos.
        var nextId = 6;
        foreach (var _ in pages)
        {
            pageObjectIds.Add(nextId++);
            contentObjectIds.Add(nextId++);
        }
        AddObject(objects, 1, "<< /Type /Catalog /Pages 2 0 R >>");
        AddObject(objects, 2, $"<< /Type /Pages /Kids [{string.Join(" ", pageObjectIds.Select(id => $"{id} 0 R"))}] /Count {pages.Count} >>");
        AddObject(objects, 3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        AddObject(objects, 4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        AddObject(objects, 5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Oblique /Encoding /WinAnsiEncoding >>");

        for (var index = 0; index < pages.Count; index++)
        {
            var content = new StringBuilder();
            foreach (var line in pages[index])
            {
                var font = line.Font;
                content.Append("BT /").Append(font).Append(' ').Append(line.Size.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" Tf 50 ").Append(line.Y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" Td (").Append(Escape(line.Text)).Append(") Tj ET\n");
            }
            content.Append("BT /F1 8 Tf 50 30 Td (DocuSearch AI · página ")
                .Append(index + 1).Append(" de ").Append(pages.Count).Append(") Tj ET\n");
            AddObject(objects, pageObjectIds[index],
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth:0} {PageHeight:0}] " +
                "/Resources << /Font << /F1 3 0 R /F2 4 0 R /F3 5 0 R >> >> " +
                $"/Contents {contentObjectIds[index]} 0 R >>");
            AddStreamObject(objects, contentObjectIds[index], content.ToString());
        }

        var output = new List<byte>();
        output.AddRange(Encoding.ASCII.GetBytes("%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n"));
        var offsets = new int[objects.Count + 1];
        for (var index = 0; index < objects.Count; index++)
        {
            offsets[index + 1] = output.Count;
            output.AddRange(objects[index]);
        }
        var xref = output.Count;
        output.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
        for (var index = 1; index <= objects.Count; index++)
            output.AddRange(Encoding.ASCII.GetBytes($"{offsets[index]:0000000000} 00000 n \n"));
        output.AddRange(Encoding.ASCII.GetBytes($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        return output.ToArray();
    }

    private static void AddObject(List<byte[]> objects, int id, string body)
    {
        objects.Add(Encoding.Latin1.GetBytes($"{id} 0 obj\n{body}\nendobj\n"));
    }

    private static void AddStreamObject(List<byte[]> objects, int id, string content)
    {
        var body = Encoding.Latin1.GetBytes(content);
        var header = Encoding.ASCII.GetBytes($"{id} 0 obj\n<< /Length {body.Length} >>\nstream\n");
        var footer = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
        objects.Add([.. header, .. body, .. footer]);
    }

    private static string Escape(string text) => text
        .Replace("\\", "\\\\")
        .Replace("(", "\\(")
        .Replace(")", "\\)")
        .Replace("\r", " ");

    private static IEnumerable<string> Wrap(string text, int max)
    {
        var current = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > max)
            {
                yield return current.ToString();
                current.Clear();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(word);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}
