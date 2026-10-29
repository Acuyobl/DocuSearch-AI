namespace DocuSearch.Models;

public sealed class DocumentFilter
{
    public string Text { get; set; } = "";
    public string Department { get; set; } = "";
    public string Category { get; set; } = "";
    public string Level { get; set; } = "";
    public string Year { get; set; } = "";
    public IEnumerable<DocumentItem> Apply(IEnumerable<DocumentItem> documents) => documents.Where(d =>
        (Department.Length == 0 || d.Department == Department) &&
        (Category.Length == 0 || d.Category == Category) &&
        (Level.Length == 0 || d.Confidentiality == Level) &&
        (Year.Length == 0 || d.UploadDate.Year.ToString() == Year || d.InternalCode.Contains(Year)) &&
        (Text.Length == 0 || Services.TextAnalysis.Normalize($"{d.Title} {d.InternalCode} {d.Author} {string.Join(' ', d.Tags)} {string.Join(' ', d.Keywords)}")
            .Contains(Services.TextAnalysis.Normalize(Text.Trim()), StringComparison.Ordinal)));
}
