namespace DeveloperTeam.Domain;

public record CreateTaskRequest(string RepositoryUrl, string Prompt, bool CreatePullRequest = true, string? BaseBranch = null);
