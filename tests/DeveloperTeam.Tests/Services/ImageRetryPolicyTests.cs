using DeveloperTeam.Domain;
using DeveloperTeam.Services;
using Xunit;

namespace DeveloperTeam.Tests.Services;

public sealed class ImageRetryPolicyTests
{
    private static AgentTask Ready() => new()
    {
        Id = Guid.NewGuid(), Status = "failed", EnableImageGeneration = true,
        CreatePullRequest = true, ImageStatus = "failed",
        WorkspacePath = "/workspaces/task", ImageBudgetUsd = 0.35m,
        ImageReservedUsd = 0.015m
    };
    [Fact]
    public void FailedImageTaskCanBeAtomicallyClaimedOnce()
    {
        var task = Ready();
        Assert.True(ImageRetryPolicy.Eligible.Compile()(task));
        task.Status = "image_retry_queued";
        Assert.False(ImageRetryPolicy.Eligible.Compile()(task));
        task.Status = "running";
        Assert.False(ImageRetryPolicy.Eligible.Compile()(task));
    }

    [Fact]
    public void RejectsTasksWithoutFailedImageSavedWorkspaceOrBudget()
    {
        var task = Ready();
        task.ImageStatus = "completed";
        Assert.False(ImageRetryPolicy.Eligible.Compile()(task));
        task.ImageStatus = "failed";
        task.WorkspacePath = null;
        Assert.False(ImageRetryPolicy.Eligible.Compile()(task));
        task.WorkspacePath = "/workspaces/task";
        task.ImageReservedUsd = 0.349m;
        Assert.False(ImageRetryPolicy.Eligible.Compile()(task));
        task.ImageReservedUsd = 0.015m;
        task.PullRequestUrl = "https://github.com/test/repo/pull/2";
        Assert.False(ImageRetryPolicy.Eligible.Compile()(task));
    }

    [Fact]
    public void RejectedDraftCanAlsoBeRetriedIfBudgetRemains()
    {
        var task = Ready();
        task.ImageStatus = "rejected";
        Assert.True(ImageRetryPolicy.Eligible.Compile()(task));
    }
}
