using DeveloperTeam.Domain;
using DeveloperTeam.Services;
using Xunit;

namespace DeveloperTeam.Tests.Services;

public sealed class ImageBudgetPolicyTests
{
    private static AgentTask Failed() => new()
    {
        Status = "failed", EnableImageGeneration = true, CreatePullRequest = true,
        ImageStatus = "planning", WorkspacePath = "/workspaces/task",
        ImageBudgetUsd = 0.35m, ImageReservedUsd = 0m
    };

    [Fact]
    public void BudgetCanBeIncreasedOnlyForImageStageFailures()
    {
        var task = Failed();
        Assert.True(ImageBudgetPolicy.Editable.Compile()(task));
        task.ImageStatus = "rejected";
        Assert.True(ImageBudgetPolicy.Editable.Compile()(task));
        task.ImageStatus = "failed";
        Assert.True(ImageBudgetPolicy.Editable.Compile()(task));
        task.ImageStatus = "completed";
        Assert.False(ImageBudgetPolicy.Editable.Compile()(task));
        task.ImageStatus = "planning";
        task.Status = "running";
        Assert.False(ImageBudgetPolicy.Editable.Compile()(task));
        task.Status = "failed";
        task.PullRequestUrl = "https://github.com/test/repo/pull/1";
        Assert.False(ImageBudgetPolicy.Editable.Compile()(task));
    }

    [Fact]
    public void CannotEditBudgetWithoutWorkspaceOrImageOptIn()
    {
        var task = Failed();
        task.WorkspacePath = null;
        Assert.False(ImageBudgetPolicy.Editable.Compile()(task));
        task.WorkspacePath = "/workspaces/task";
        task.EnableImageGeneration = false;
        Assert.False(ImageBudgetPolicy.Editable.Compile()(task));
    }
}
