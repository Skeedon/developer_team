namespace DeveloperTeam.Domain;

public record CreateTaskRequest(string RepositoryUrl, string Prompt, bool CreatePullRequest = true, string? BaseBranch = null, string? Role = null, bool EnableImageGeneration = false, decimal ImageBudgetUsd = 0.35m);
