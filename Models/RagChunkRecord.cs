using System.ComponentModel.DataAnnotations;

namespace DocuSearch.Models;

public sealed class RagChunkRecord
{
    [Key, MaxLength(180)] public string Id { get; set; } = "";
    [Required, MaxLength(100)] public string DocumentId { get; set; } = "";
    [MaxLength(100)] public string InternalCode { get; set; } = "";
    [MaxLength(30)] public string SourceType { get; set; } = "pdf";
    public int PageNumber { get; set; }
    public int ChunkIndex { get; set; }
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public string Text { get; set; } = "";
    [MaxLength(64)] public string Hash { get; set; } = "";
    [MaxLength(100)] public string Model { get; set; } = "";
    public int Dimensions { get; set; }
    public byte[] Vector { get; set; } = [];
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
