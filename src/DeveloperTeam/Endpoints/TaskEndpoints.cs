using DeveloperTeam.Data;
using DeveloperTeam.Domain;
using DeveloperTeam.Validation;
using Microsoft.EntityFrameworkCore;

namespace DeveloperTeam.Endpoints;

public static class TaskEndpoints
{
    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder app)
    {
    var group = app.MapGroup("/api/tasks");
    group.MapGet("/", async (IDbContextFactory<AgentDbContext> factory) =>
    {
        await using var db = await factory.CreateDbContextAsync();
        return Results.Ok(await db.Tasks.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(100)
            .Select(x => new { x.Id, x.RepositoryUrl, x.Prompt, x.Status, x.CreatedAt, x.StartedAt, x.FinishedAt, x.Error, x.CreatePullRequest, x.PullRequestUrl })
            .ToListAsync());
    });

    group.MapGet("/{id:guid}", async (Guid id, IDbContextFactory<AgentDbContext> factory) =>
    {
        await using var db = await factory.CreateDbContextAsync();
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return task is null ? Results.NotFound() : Results.Ok(new
        {
            task.Id, task.RepositoryUrl, task.Prompt, task.Status, task.CreatedAt,
            task.StartedAt, task.FinishedAt, task.Error, task.WorkspacePath,
            task.CreatePullRequest, task.PullRequestUrl, task.PullRequestBranch,
            HasDiff = !string.IsNullOrWhiteSpace(task.Diff)
        });
    });

    group.MapGet("/{id:guid}/log", async (Guid id, IDbContextFactory<AgentDbContext> factory) =>
    {
        await using var db = await factory.CreateDbContextAsync();
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return task is null ? Results.NotFound() : Results.Text(task.Log ?? "", "text/plain");
    });

    group.MapGet("/{id:guid}/diff", async (Guid id, IDbContextFactory<AgentDbContext> factory) =>
    {
        await using var db = await factory.CreateDbContextAsync();
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return task is null ? Results.NotFound() : Results.Text(task.Diff ?? "", "text/plain");
    });

    group.MapPost("/", async (CreateTaskRequest request, IDbContextFactory<AgentDbContext> factory) =>
    {
        if (!RepositoryUrl.IsValid(request.RepositoryUrl))
            return Results.BadRequest(new { error = "Only public github.com/OWNER/REPO HTTPS URLs are supported." });
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 10000)
            return Results.BadRequest(new { error = "Prompt must have 1–10000 characters." });

        var task = new AgentTask
        {
            Id = Guid.NewGuid(),
            RepositoryUrl = request.RepositoryUrl.Trim().TrimEnd('/'),
            Prompt = request.Prompt.Trim(),
            Status = "queued",
            CreatePullRequest = request.CreatePullRequest,
            CreatedAt = DateTime.UtcNow
        };
        await using var db = await factory.CreateDbContextAsync();
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        return Results.Created($"/api/tasks/{task.Id}", new { task.Id, task.Status });
    });

        return app;
    }
}
