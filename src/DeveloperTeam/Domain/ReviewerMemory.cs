namespace DeveloperTeam.Domain;

// Persisted separately from cloned workspaces, scoped to the GitHub repository.
public sealed class ReviewerMemory
{
    public string RepositoryKey { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}
