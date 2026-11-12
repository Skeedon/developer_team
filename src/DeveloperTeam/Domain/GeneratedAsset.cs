namespace DeveloperTeam.Domain;

// Image bytes stay in the existing, read-only-to-API workspace volume.
// The database stores metadata only, not base64 image payloads.
public sealed class GeneratedAsset
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public string Path { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string CriteriaJson { get; set; } = "[]";
    public string Size { get; set; } = "1024x1024";
    public bool Transparent { get; set; }
    public string Status { get; set; } = "queued";
    public int DraftAttempts { get; set; }
    public int FinalAttempts { get; set; }
    public double? Score { get; set; }
    public string? Review { get; set; }
    public decimal ReservedUsd { get; set; }
    public long ImageInputTokens { get; set; }
    public long ImageOutputTokens { get; set; }
    public long EvaluatorInputTokens { get; set; }
    public long EvaluatorOutputTokens { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
