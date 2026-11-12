namespace DeveloperTeam.Domain;

/// <summary>Execution stages shared by the worker and task API. Status tracks the outcome.</summary>
public static class TaskProgress
{
    public const string Assignment = "assignment";
    public const string Development = "development";
    public const string Testing = "testing";
    public const string Review = "review";
    public const string Publication = "publication";

    private static readonly string[] Stages = [Assignment, Development, Testing, Review, Publication];

    public static void Advance(AgentTask task, string stage)
    {
        var next = Array.IndexOf(Stages, stage);
        if (next < 0) throw new ArgumentException("Unknown task stage.", nameof(stage));
        if (stage == Publication && !task.CreatePullRequest)
            throw new InvalidOperationException("Publication requires a PR task.");
        if (next < Array.IndexOf(Stages, task.CurrentStage))
            throw new InvalidOperationException("Task stages cannot move backwards.");
        task.CurrentStage = stage;
    }
}
