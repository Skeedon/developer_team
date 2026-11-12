namespace DeveloperTeam.Domain;

public sealed class AgentTask
{
    public Guid Id { get; set; }
    public string RepositoryUrl { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? WorkspacePath { get; set; }
    public string? Log { get; set; }
    public string? Diff { get; set; }
    public string? Error { get; set; }
}
