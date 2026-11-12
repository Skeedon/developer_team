using System.Linq.Expressions;
using DeveloperTeam.Domain;

namespace DeveloperTeam.Services;

/// <summary>Only the human may retry a failed image stage; never automatically rebill.</summary>
public static class ImageRetryPolicy
{
    public static readonly Expression<Func<AgentTask, bool>> Eligible = task =>
        task.Status == "failed" &&
        task.EnableImageGeneration && task.CreatePullRequest &&
        (task.ImageStatus == "failed" || task.ImageStatus == "rejected") &&
        task.WorkspacePath != null && task.PullRequestUrl == null &&
        task.ImageReservedUsd + ImagePipeline.DraftReserveUsd <= task.ImageBudgetUsd;
}
