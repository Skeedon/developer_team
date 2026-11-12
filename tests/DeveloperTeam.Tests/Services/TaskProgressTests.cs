using DeveloperTeam.Domain;
using Xunit;

namespace DeveloperTeam.Tests.Services;

public class TaskProgressTests
{
    [Theory]
    [InlineData("developer", true)]
    [InlineData("designer", false)]
    [InlineData("designer,developer", true)]
    [InlineData("designer,developer", false)]
    public void Execution_AdvancesForWholeTeam_AndSkipsPublicationWithoutPr(string participants, bool createPr)
    {
        var task = new AgentTask { Participants = participants, CreatePullRequest = createPr };
        foreach (var stage in new[] { "assignment", "development", "testing", "review" })
        {
            TaskProgress.Advance(task, stage);
            Assert.Equal(stage, task.CurrentStage);
        }
        if (createPr) TaskProgress.Advance(task, "publication");
        else Assert.Throws<InvalidOperationException>(() => TaskProgress.Advance(task, "publication"));
        task.Status = "completed";
        Assert.Equal(createPr ? "publication" : "review", task.CurrentStage);
    }

    [Fact]
    public void FailureAndPublicationRetry_PreserveStageAndExecutionTimestamp()
    {
        var task = new AgentTask { CreatePullRequest = true, FinishedAt = DateTime.UtcNow };
        TaskProgress.Advance(task, "testing");
        task.Status = "failed";
        Assert.Equal("testing", task.CurrentStage);
        // Retrying fixes stays in testing; a publication retry uses the saved workspace.
        TaskProgress.Advance(task, "testing");
        TaskProgress.Advance(task, "publication");
        var finished = task.FinishedAt;
        foreach (var status in new[] { "publish_failed", "publish_queued", "publishing", "completed" })
        {
            task.Status = status;
            TaskProgress.Advance(task, "publication");
            task.RecordPublicationRetry(DateTime.UtcNow);
            Assert.Equal("publication", task.CurrentStage);
            Assert.Equal(finished, task.FinishedAt);
        }
    }

    [Fact]
    public void LegacyTask_StartsUnknown_AndCanResumePublication()
    {
        var task = new AgentTask { CreatePullRequest = true, Status = "publish_queued" };
        Assert.Null(task.CurrentStage);
        TaskProgress.Advance(task, "publication");
        Assert.Equal("publication", task.CurrentStage);
        Assert.Throws<InvalidOperationException>(() => TaskProgress.Advance(task, "development"));
        Assert.Throws<ArgumentException>(() => TaskProgress.Advance(task, "unknown"));
        Assert.Equal("publication", task.CurrentStage);
    }
}
