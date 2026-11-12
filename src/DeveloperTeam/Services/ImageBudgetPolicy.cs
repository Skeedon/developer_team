using System.Linq.Expressions;
using DeveloperTeam.Domain;

namespace DeveloperTeam.Services;

public sealed record IncreaseImageBudgetRequest(decimal ImageBudgetUsd);

/// <summary>
/// Editing image budgets is a separate, non-billing action. It never
/// schedules a new paid operation; the user must explicitly request retry.
/// </summary>
public static class ImageBudgetPolicy
{
    public static readonly Expression<Func<AgentTask, bool>> Editable = t =>
        t.Status == "failed" && t.EnableImageGeneration && t.CreatePullRequest &&
        t.PullRequestUrl == null && t.WorkspacePath != null &&
        (t.ImageStatus == "planning" || t.ImageStatus == "failed" ||
         t.ImageStatus == "rejected");
}
