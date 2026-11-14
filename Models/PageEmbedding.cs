using System.ComponentModel.DataAnnotations;

namespace DocuSearch.Models;

/// <summary>Vector normalizado de una página para búsqueda semántica local.</summary>
public sealed class PageEmbedding
{
    [Key, MaxLength(100)] public string Id { get; set; } = Catalog.NewId();
    [Required, MaxLength(100)] public string DocumentId { get; set; } = "";
    public int PageNumber { get; set; }
    [MaxLength(100)] public string Model { get; set; } = "";
    public int Dimensions { get; set; }
    public byte[] Vector { get; set; } = [];
    [MaxLength(64)] public string SourceHash { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
