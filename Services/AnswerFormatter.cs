using System.Text.RegularExpressions;

namespace DocuSearch.Services;

public enum AnswerInlineKind { Text, Bold, Code, Citation }

/// <summary>Fragmento de texto en línea. En una cita, <see cref="Code"/>/<see cref="Page"/> o <see cref="Index"/> la identifican.</summary>
public sealed record AnswerInline(AnswerInlineKind Kind, string Text, string? Code = null, int? Page = null, int? Index = null);

public abstract record AnswerBlock;
public sealed record AnswerHeading(IReadOnlyList<AnswerInline> Content) : AnswerBlock;
public sealed record AnswerParagraph(IReadOnlyList<AnswerInline> Content) : AnswerBlock;
public sealed record AnswerList(bool Ordered, IReadOnlyList<IReadOnlyList<AnswerInline>> Items) : AnswerBlock;
public sealed record AnswerTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<IReadOnlyList<AnswerInline>>> Rows) : AnswerBlock;
/// <summary>Fragmento literal de un expediente con su cita «[CÓDIGO, p. N]».</summary>
public sealed record AnswerExcerpt(IReadOnlyList<AnswerInline> Content, string Code, int Page) : AnswerBlock;
/// <summary>Aviso del sistema al final de la respuesta (modo extractivo, verificación…).</summary>
public sealed record AnswerNote(string Text) : AnswerBlock;

/// <summary>
/// Convierte el texto de una respuesta (Markdown ligero del modelo o el formato extractivo local)
/// en bloques estructurados. No genera HTML: la interfaz los pinta como componentes, así que el
/// contenido de los documentos nunca se interpreta como marcado.
/// </summary>
public static partial class AnswerFormatter
{
    private const string CodePattern = @"[A-Z]{2,5}-[A-Z0-9]+(?:-[A-Z0-9]+){0,4}";

    [GeneratedRegex(@"^\[(?<code>" + CodePattern + @"),\s*p\.\s*(?<page>\d{1,5})\]$", RegexOptions.IgnoreCase)]
    private static partial Regex CitationLine();

    [GeneratedRegex(@"\*\*(?<bold>[^*\n]+)\*\*|`(?<code>[^`\n]+)`|\[(?<cite>" + CodePattern + @"),\s*p\.\s*(?<page>\d{1,5})\]|\[(?<index>\d{1,2}(?:\s*,\s*\d{1,2})*)\]", RegexOptions.IgnoreCase)]
    private static partial Regex InlineToken();

    [GeneratedRegex(@"^(?<type>[A-Z_]{3,20}):\s*(?<value>.+?)\s+[—-]\s+\[(?<code>" + CodePattern + @"),\s*p\.\s*(?<page>\d{1,5})\]$")]
    private static partial Regex EntityLine();

    [GeneratedRegex(@"^(?<code>" + CodePattern + @")\s+—\s+(?<title>.+?)(?:\s+\((?<level>[^()]+)\))?$")]
    private static partial Regex DocumentLine();

    [GeneratedRegex(@"^(?<key>[A-Za-zÁÉÍÓÚÑáéíóúñ][\wÁÉÍÓÚÑáéíóúñ ]{1,28}):\s+(?<value>\S.*)$")]
    private static partial Regex KeyValueLine();

    [GeneratedRegex(@"^\s*(?:[-*•]\s+)(?<item>.+)$")]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"^\s*\d{1,3}[.)]\s+(?<item>.+)$")]
    private static partial Regex OrderedLine();

    [GeneratedRegex(@"^\s*#{1,4}\s+(?<text>.+?)\s*#*\s*$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$")]
    private static partial Regex TableSeparator();

    private static readonly string[] NotePrefixes =
    [
        "Respuesta extractiva", "Resumen extractivo", "La respuesta generada no superó",
        "No se pudo usar el motor", "No se pudo completar con el motor", "Presupuesto mensual de IA", "Cuota mensual de"
    ];

    private static readonly Dictionary<string, string> EntityLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MONEY"] = "Monto", ["DATE"] = "Fecha", ["LAW"] = "Norma", ["PERSON"] = "Persona", ["ORGANIZATION"] = "Organización"
    };

    public static IReadOnlyList<AnswerBlock> Format(string? text)
    {
        var blocks = new List<AnswerBlock>();
        if (string.IsNullOrWhiteSpace(text)) return blocks;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var index = 0;
        while (index < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) { index++; continue; }
            var start = index;
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index])) index++;
            FormatGroup(lines[start..index].Select(line => line.TrimEnd()).ToList(), blocks);
        }
        return blocks;
    }

    private static void FormatGroup(List<string> group, List<AnswerBlock> blocks)
    {
        // Formato extractivo: texto del fragmento seguido de su cita en la última línea.
        if (group.Count >= 2 && CitationLine().Match(group[^1].Trim()) is { Success: true } groupCite &&
            !EntityLine().IsMatch(group[0].Trim()) && !TableSeparator().IsMatch(group[1]))
        {
            blocks.Add(new AnswerExcerpt(Inlines(string.Join(" ", group[..^1].Select(item => item.Trim()))),
                groupCite.Groups["code"].Value.ToUpperInvariant(), int.Parse(groupCite.Groups["page"].Value)));
            return;
        }
        var position = 0;
        while (position < group.Count)
        {
            var line = group[position].Trim();

            if (HeadingLine().Match(line) is { Success: true } heading)
            {
                blocks.Add(new AnswerHeading(Inlines(heading.Groups["text"].Value)));
                position++;
                continue;
            }

            if (NotePrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                blocks.Add(new AnswerNote(string.Join(" ", group.Skip(position).Select(item => item.Trim()))));
                return;
            }

            // Tabla Markdown: cabecera | separador | filas.
            if (line.Contains('|') && position + 1 < group.Count && TableSeparator().IsMatch(group[position + 1]))
            {
                var headers = Cells(line);
                var rows = new List<IReadOnlyList<IReadOnlyList<AnswerInline>>>();
                position += 2;
                while (position < group.Count && group[position].Contains('|'))
                {
                    var cells = Cells(group[position]);
                    rows.Add(Enumerable.Range(0, headers.Count)
                        .Select(column => (IReadOnlyList<AnswerInline>)Inlines(column < cells.Count ? cells[column] : ""))
                        .ToList());
                    position++;
                }
                blocks.Add(new AnswerTable(headers, rows));
                continue;
            }

            // Entidades extraídas: «MONEY: 500 € — [CÓDIGO, p. 3]».
            if (Run(group, position, EntityLine()) is { Count: > 0 } entities)
            {
                blocks.Add(new AnswerTable(["Tipo", "Valor", "Fuente"], entities.Select(match => (IReadOnlyList<IReadOnlyList<AnswerInline>>)
                [
                    [new AnswerInline(AnswerInlineKind.Text, EntityLabels.GetValueOrDefault(match.Groups["type"].Value, match.Groups["type"].Value))],
                    Inlines(match.Groups["value"].Value),
                    [Citation(match.Groups["code"].Value, match.Groups["page"].Value)]
                ]).ToList()));
                position += entities.Count;
                continue;
            }

            // Listado de expedientes: «CÓDIGO — Título (Clasificación)»; una sola línea es el encabezado de una ficha.
            if (Run(group, position, DocumentLine()) is { Count: > 0 } documents)
            {
                if (documents.Count == 1)
                {
                    var single = documents[0];
                    blocks.Add(new AnswerHeading([.. Code(single.Groups["code"].Value), .. Text(" " + single.Groups["title"].Value +
                        (single.Groups["level"].Success ? $" ({single.Groups["level"].Value})" : ""))]));
                    position++;
                    continue;
                }
                var withLevel = documents.Any(match => match.Groups["level"].Success);
                blocks.Add(new AnswerTable(withLevel ? ["Código", "Título", "Clasificación"] : ["Código", "Título"],
                    documents.Select(match => (IReadOnlyList<IReadOnlyList<AnswerInline>>)(withLevel
                        ? [Code(match.Groups["code"].Value), Text(match.Groups["title"].Value), Text(match.Groups["level"].Value)]
                        : [Code(match.Groups["code"].Value), Text(match.Groups["title"].Value)])).ToList()));
                position += documents.Count;
                continue;
            }

            // Ficha «Clave: valor» (2 o más líneas seguidas).
            if (Run(group, position, KeyValueLine()) is { Count: >= 2 } pairs)
            {
                blocks.Add(new AnswerTable(["Campo", "Valor"], pairs.Select(match => (IReadOnlyList<IReadOnlyList<AnswerInline>>)
                    [Text(match.Groups["key"].Value.Trim()), Inlines(match.Groups["value"].Value)]).ToList()));
                position += pairs.Count;
                continue;
            }

            if (Run(group, position, BulletLine()) is { Count: > 0 } bullets)
            {
                blocks.Add(new AnswerList(false, bullets.Select(match => (IReadOnlyList<AnswerInline>)Inlines(match.Groups["item"].Value)).ToList()));
                position += bullets.Count;
                continue;
            }

            if (Run(group, position, OrderedLine()) is { Count: > 0 } ordered)
            {
                blocks.Add(new AnswerList(true, ordered.Select(match => (IReadOnlyList<AnswerInline>)Inlines(match.Groups["item"].Value)).ToList()));
                position += ordered.Count;
                continue;
            }

            // Párrafo hasta la siguiente estructura; si termina en una cita sola, es un fragmento.
            var end = position + 1;
            while (end < group.Count && !StartsStructure(group, end)) end++;
            var paragraph = group.GetRange(position, end - position).Select(item => item.Trim()).ToList();
            if (paragraph.Count >= 2 && CitationLine().Match(paragraph[^1]) is { Success: true } cite)
            {
                blocks.Add(new AnswerExcerpt(Inlines(string.Join(" ", paragraph[..^1])), cite.Groups["code"].Value.ToUpperInvariant(),
                    int.Parse(cite.Groups["page"].Value)));
            }
            else
            {
                blocks.Add(new AnswerParagraph(Inlines(string.Join(" ", paragraph))));
            }
            position = end;
        }
    }

    private static bool StartsStructure(List<string> group, int position)
    {
        var line = group[position].Trim();
        return HeadingLine().IsMatch(line) || BulletLine().IsMatch(line) || OrderedLine().IsMatch(line) ||
            EntityLine().IsMatch(line) || DocumentLine().IsMatch(line) ||
            NotePrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
            (line.Contains('|') && position + 1 < group.Count && TableSeparator().IsMatch(group[position + 1])) ||
            (position + 1 < group.Count && KeyValueLine().IsMatch(line) && KeyValueLine().IsMatch(group[position + 1].Trim()));
    }

    private static List<Match> Run(List<string> group, int position, Regex pattern)
    {
        var matches = new List<Match>();
        while (position < group.Count && pattern.Match(group[position].Trim()) is { Success: true } match)
        {
            matches.Add(match);
            position++;
        }
        return matches;
    }

    private static List<string> Cells(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];
        return trimmed.Split('|').Select(cell => cell.Trim()).ToList();
    }

    public static List<AnswerInline> Inlines(string text)
    {
        var result = new List<AnswerInline>();
        var last = 0;
        foreach (Match match in InlineToken().Matches(text))
        {
            if (match.Index > last) result.Add(Text(text[last..match.Index])[0]);
            if (match.Groups["bold"].Success) result.Add(new(AnswerInlineKind.Bold, match.Groups["bold"].Value));
            else if (match.Groups["code"].Success) result.Add(new(AnswerInlineKind.Code, match.Groups["code"].Value));
            else if (match.Groups["cite"].Success) result.Add(Citation(match.Groups["cite"].Value, match.Groups["page"].Value));
            else
            {
                foreach (var number in match.Groups["index"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    result.Add(new(AnswerInlineKind.Citation, $"[{number}]", Index: int.Parse(number)));
            }
            last = match.Index + match.Length;
        }
        if (last < text.Length) result.Add(Text(text[last..])[0]);
        return result;
    }

    private static AnswerInline Citation(string code, string page) =>
        new(AnswerInlineKind.Citation, $"{code.ToUpperInvariant()} · p. {page}", code.ToUpperInvariant(), int.Parse(page));

    private static List<AnswerInline> Text(string text) => [new(AnswerInlineKind.Text, text)];
    private static List<AnswerInline> Code(string text) => [new(AnswerInlineKind.Code, text)];
}
