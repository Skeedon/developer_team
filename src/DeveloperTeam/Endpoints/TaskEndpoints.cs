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
            .Select(x => new { x.Id, x.RepositoryUrl, x.BaseBranch, x.Prompt, x.Status, x.CreatedAt, x.StartedAt, x.FinishedAt, x.Error, x.CreatePullRequest, x.PullRequestUrl, x.TestStatus, x.ReviewStatus, x.DeveloperInputTokens, x.DeveloperOutputTokens, x.ReviewerInputTokens, x.ReviewerOutputTokens })
            .ToListAsync());
    });

    group.MapGet("/{id:guid}", async (Guid id, IDbContextFactory<AgentDbContext> factory) =>
    {
        await using var db = await factory.CreateDbContextAsync();
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return task is null ? Results.NotFound() : Results.Ok(new
        {
            task.Id, task.RepositoryUrl, task.BaseBranch, task.Prompt, task.Status, task.CreatedAt,
            task.StartedAt, task.FinishedAt, task.Error, task.WorkspacePath,
            task.CreatePullRequest, task.PullRequestUrl, task.PullRequestBranch,
            task.TestStatus, task.ReviewStatus,
            task.DeveloperInputTokens, task.DeveloperCachedTokens, task.DeveloperOutputTokens,
            task.ReviewerInputTokens, task.ReviewerCachedTokens, task.ReviewerOutputTokens,
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

    group.MapGet("/{id:guid}/initial-diff", async (Guid id, IDbContextFactory<AgentDbContext> factory) =>
    {
        await using var db = await factory.CreateDbContextAsync();
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return task is null ? Results.NotFound() : Results.Text(task.InitialDiff ?? "", "text/plain");
    });

    group.MapGet("/reviewer-memory", async (string repositoryUrl,
        IDbContextFactory<AgentDbContext> factory) =>
    {
        if (!RepositoryUrl.IsValid(repositoryUrl))
            return Results.BadRequest(new { error = "Invalid repository URL." });
        await using var db = await factory.CreateDbContextAsync();
        var key = DeveloperTeam.Services.ReviewerMemoryKey.FromUrl(repositoryUrl);
        var memory = await db.ReviewerMemories.AsNoTracking()
            .Where(x => x.RepositoryKey == key)
            .Select(x => new { x.Notes, x.UpdatedAt }).SingleOrDefaultAsync();
        return memory is null ? Results.Ok(new { notes = "", updatedAt = (DateTime?)null })
            : Results.Ok(new { notes = memory.Notes, updatedAt = (DateTime?)memory.UpdatedAt });
    });

    group.MapGet("/{id:guid}/checks", async (Guid id, IDbContextFactory<AgentDbContext> factory) =>
    {
        await using var db = await factory.CreateDbContextAsync();
        var task = await db.Tasks.AsNoTracking().Where(t => t.Id == id)
            .Select(t => new { t.TestStatus, t.TestLog, t.ReviewStatus, t.ReviewLog })
            .SingleOrDefaultAsync();
        return task is null ? Results.NotFound() : Results.Ok(task);
    });

    group.MapPost("/{id:guid}/retry-publish", async (Guid id, IDbContextFactory<AgentDbContext> factory) =>
    {
        await using var db = await factory.CreateDbContextAsync();
        // Atomic state transition; legacy publication failures used "failed".
        var changed = await db.Tasks
            .Where(t => t.Id == id && t.CreatePullRequest &&
                (t.Status == "failed" || t.Status == "publish_failed") &&
                t.Diff != null && t.Diff != "" && t.PullRequestUrl == null)
            .ExecuteUpdateAsync(x => x.SetProperty(t => t.Status, "publish_queued")
                .SetProperty(t => t.Error, (string?)null));
        if (changed == 1)
            return Results.Accepted($"/api/tasks/{id}", new { id, status = "publish_queued" });
        return await db.Tasks.AsNoTracking().AnyAsync(t => t.Id == id)
            ? Results.Conflict(new { error = "Retry requires a failed PR task with a saved diff and no PR." })
            : Results.NotFound();
    });

    group.MapPost("/", async (CreateTaskRequest request, IDbContextFactory<AgentDbContext> factory) =>
    {
        if (!RepositoryUrl.IsValid(request.RepositoryUrl))
            return Results.BadRequest(new { error = "Only public github.com/OWNER/REPO HTTPS URLs are supported." });
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 10000)
            return Results.BadRequest(new { error = "Prompt must have 1–10000 characters." });

        var sourceBranch = request.BaseBranch?.Trim();
        if (!string.IsNullOrEmpty(sourceBranch) &&
            (sourceBranch.Length > 150 || sourceBranch.StartsWith('-') || sourceBranch.StartsWith('.') ||
             sourceBranch.EndsWith('.') || sourceBranch.EndsWith('/') ||
             sourceBranch.Contains("..") || sourceBranch.Contains("//") ||
             sourceBranch.Contains("@{") || sourceBranch.Contains("/.") ||
             sourceBranch.EndsWith(".lock") ||
             sourceBranch.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '/' or '.'))))
            return Results.BadRequest(new { error = "Invalid Git source branch." });

        var task = new AgentTask
        {
            Id = Guid.NewGuid(),
            RepositoryUrl = request.RepositoryUrl.Trim().TrimEnd('/'),
            Prompt = request.Prompt.Trim(),
            BaseBranch = string.IsNullOrWhiteSpace(sourceBranch) ? null : sourceBranch,
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
