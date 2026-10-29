using System.Text;

internal static class PdfFixture
{
    public static byte[] Create(params string[] pageTexts)
        => CreateWithPadding(0, pageTexts);

    public static byte[] CreateWithPadding(int padding, params string[] pageTexts)
    {
        var fontId = 3 + 2 * pageTexts.Length;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Count {pageTexts.Length} /Kids [ {string.Join(" ", pageTexts.Select((_, i) => $"{3 + i * 2} 0 R"))} ] >>"
        };
        for (var i = 0; i < pageTexts.Length; i++)
        {
            var text = pageTexts[i].Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
            var content = $"BT /F1 12 Tf 50 700 Td ({text}) Tj ET";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {fontId} 0 R >> >> /Contents {4 + i * 2} 0 R >>");
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream");
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        using var output = new MemoryStream();
        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
        Write("%PDF-1.4\n");
        if (padding > 0) Write("%" + new string(' ', padding) + "\n");
        var offsets = new List<long> { 0 };
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = output.Position;
        Write($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) Write($"{offset:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }
}
