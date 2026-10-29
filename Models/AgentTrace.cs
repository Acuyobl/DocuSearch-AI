using System.ComponentModel.DataAnnotations;

namespace DocuSearch.Models;

public sealed class AgentTrace
{
    [Key, MaxLength(32)] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(40)] public string Engine { get; set; } = "extractive";
    [MaxLength(200)] public string Model { get; set; } = "";
    [MaxLength(20)] public string Status { get; set; } = "error";
    [MaxLength(40)] public string ErrorCode { get; set; } = "";
    public long DurationMs { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
}
