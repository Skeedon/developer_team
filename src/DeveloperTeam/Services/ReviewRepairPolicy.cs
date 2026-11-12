using System.Linq.Expressions;
using DeveloperTeam.Domain;

namespace DeveloperTeam.Services;

/// <summary>One immutable eligibility rule shared by API and regression tests.</summary>
public static class ReviewRepairPolicy
{
    public const int MaxAttempts = 3;

    public static readonly Expression<Func<AgentTask, bool>> Eligible = task =>
        (((task.Status == "completed" || task.Status == "review_fix_failed") &&
          task.ReviewStatus == "reviewed" && task.HasReviewFindings &&
          task.ReviewFixAttempts < MaxAttempts) ||
          (task.Status == "review_fix_failed" && !task.HasReviewFindings &&
           task.ReviewStatus == "reviewed")) &&
        task.CreatePullRequest && task.PullRequestUrl != null &&
        task.WorkspacePath != null && task.Diff != null && task.Diff != "";
}
