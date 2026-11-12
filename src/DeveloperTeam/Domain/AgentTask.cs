namespace DeveloperTeam.Domain;

public sealed class AgentTask
{
    public Guid Id { get; set; }
    public string RepositoryUrl { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Role { get; set; } = "developer";
    public string? BaseBranch { get; set; }
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    // The original execution end timestamp is never overwritten on publication retry.
    public DateTime? FinishedAt { get; set; }
    public DateTime? LastPublicationRetryAt { get; set; }

    public void RecordPublicationRetry(DateTime attemptedAt)
    {
        LastPublicationRetryAt = attemptedAt;
    }
    public string? WorkspacePath { get; set; }
    public string? Log { get; set; }
    public string? InitialDiff { get; set; }
    public string? Diff { get; set; }
    public string? Error { get; set; }
    public bool CreatePullRequest { get; set; }
    public string? PullRequestUrl { get; set; }
    public string? PullRequestBranch { get; set; }
    public string? TestStatus { get; set; }
    public string? TestLog { get; set; }
    public string? ReviewStatus { get; set; }
    public string? ReviewLog { get; set; }
    public bool HasDeveloperMetrics { get; set; }
    public bool HasReviewerMetrics { get; set; }
    public int? FixAttempts { get; set; }
    public long DeveloperInputTokens { get; set; }
    public long DeveloperCachedTokens { get; set; }
    public long DeveloperOutputTokens { get; set; }
    public long ReviewerInputTokens { get; set; }
    public long ReviewerCachedTokens { get; set; }
    public long ReviewerOutputTokens { get; set; }
}
