using DeveloperTeam.Domain;
using DeveloperTeam.Services;
using Xunit;

namespace DeveloperTeam.Tests.Services;

public class ReviewRepairPolicyTests
{
    private static AgentTask Ready() => new()
    {
        Status = "completed", ReviewStatus = "reviewed", HasReviewFindings = true,
        ReviewFixAttempts = 0, CreatePullRequest = true,
        WorkspacePath = "/workspaces/task", Diff = "diff",
        PullRequestUrl = "https://github.com/org/repo/pull/1"
    };

    [Fact]
    public void EligibleCompletedTaskCanBeClaimedOnce()
    {
        var ready = Ready();
        Assert.True(ReviewRepairPolicy.Eligible.Compile()(ready));
        ready.Status = "review_fix_queued";
        Assert.False(ReviewRepairPolicy.Eligible.Compile()(ready));
        ready.Status = "review_fixing";
        Assert.False(ReviewRepairPolicy.Eligible.Compile()(ready));
    }

    [Fact]
    public void FailedAttemptCanBeRetriedUntilBound()
    {
        var task = Ready();
        task.Status = "review_fix_failed";
        task.ReviewStatus = "fixing";
        task.ReviewFixAttempts = 2;
        Assert.True(ReviewRepairPolicy.Eligible.Compile()(task));
        task.ReviewFixAttempts = ReviewRepairPolicy.MaxAttempts;
        Assert.False(ReviewRepairPolicy.Eligible.Compile()(task));
    }

    [Fact]
    public void RequiresReviewFindingsSavedWorkspaceAndExistingPr()
    {
        var task = Ready();
        task.HasReviewFindings = false;
        Assert.False(ReviewRepairPolicy.Eligible.Compile()(task));
        task.HasReviewFindings = true;
        task.PullRequestUrl = null;
        Assert.False(ReviewRepairPolicy.Eligible.Compile()(task));
        task.PullRequestUrl = "https://github.com/org/repo/pull/1";
        task.WorkspacePath = null;
        Assert.False(ReviewRepairPolicy.Eligible.Compile()(task));
    }
    [Fact]
    public void PublicationOnlyRetryNeedsNoNewModelTurnOrAttempt()
    {
        var task = Ready();
        task.Status = "review_fix_failed";
        task.HasReviewFindings = false;
        task.ReviewFixAttempts = ReviewRepairPolicy.MaxAttempts;
        Assert.True(ReviewRepairPolicy.Eligible.Compile()(task));
        task.ReviewStatus = "failed";
        Assert.False(ReviewRepairPolicy.Eligible.Compile()(task));
    }
}
