using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");
builder.Services.AddDbContextFactory<AgentDbContext>(options => options.UseNpgsql(connectionString));
var role = builder.Configuration["APP_ROLE"] ?? "api";
if (role.Equals("worker", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddHostedService<AgentWorker>();

var app = builder.Build();
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<AgentDbContext>>().CreateDbContextAsync())
{
    await db.Database.EnsureCreatedAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", role }));

if (role.Equals("api", StringComparison.OrdinalIgnoreCase))
{
    var group = app.MapGroup("/api/tasks");
    group.MapGet("/", async (IDbContextFactory<AgentDbContext> factory) =>
    {
        await using var db = await factory.CreateDbContextAsync();
        return Results.Ok(await db.Tasks.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(100)
            .Select(x => new { x.Id, x.RepositoryUrl, x.Prompt, x.Status, x.CreatedAt, x.StartedAt, x.FinishedAt, x.Error })
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
            CreatedAt = DateTime.UtcNow
        };
        await using var db = await factory.CreateDbContextAsync();
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        return Results.Created($"/api/tasks/{task.Id}", new { task.Id, task.Status });
    });
}

await app.RunAsync();

public record CreateTaskRequest(string RepositoryUrl, string Prompt);

public static class RepositoryUrl
{
    private static readonly Regex Pattern = new(
        @"^https://github\.com/[a-zA-Z0-9][a-zA-Z0-9-]{0,38}/[a-zA-Z0-9_.-]+(?:\.git)?/?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValid(string? url) => url is not null && url.Length <= 250 && Pattern.IsMatch(url);
}

public sealed class AgentTask
{
    public Guid Id { get; set; }
    public string RepositoryUrl { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? WorkspacePath { get; set; }
    public string? Log { get; set; }
    public string? Diff { get; set; }
    public string? Error { get; set; }
}

public sealed class AgentDbContext(DbContextOptions<AgentDbContext> options) : DbContext(options)
{
    public DbSet<AgentTask> Tasks => Set<AgentTask>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<AgentTask>().HasKey(x => x.Id);
        builder.Entity<AgentTask>().HasIndex(x => new { x.Status, x.CreatedAt });
    }
}

public sealed class AgentWorker(
    IDbContextFactory<AgentDbContext> factory,
    ILogger<AgentWorker> logger,
    IConfiguration config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(stoppingToken);
                var next = await db.Tasks.AsNoTracking().Where(t => t.Status == "queued")
                    .OrderBy(t => t.CreatedAt).Select(t => t.Id).FirstOrDefaultAsync(stoppingToken);
                if (next != Guid.Empty)
                {
                    // Atomic claim: protects against two workers executing the same task.
                    var claimed = await db.Tasks.Where(t => t.Id == next && t.Status == "queued")
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "running")
                            .SetProperty(t => t.StartedAt, DateTime.UtcNow), stoppingToken);
                    if (claimed == 1) await RunTaskAsync(next, stoppingToken);
                }
                else await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker loop failure");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task RunTaskAsync(Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var task = await db.Tasks.FirstAsync(t => t.Id == id, token);
        var workspaceRoot = config["WORKSPACE_ROOT"] ?? "/workspaces";
        var workspace = Path.Combine(workspaceRoot, id.ToString("N"));
        task.WorkspacePath = workspace;
        var transcript = new StringBuilder();
        try
        {
            if (string.IsNullOrWhiteSpace(config["OPENAI_API_KEY"]))
                throw new InvalidOperationException("OPENAI_API_KEY is missing in worker environment.");
            Directory.CreateDirectory(workspaceRoot);
            if (Directory.Exists(workspace))
                throw new InvalidOperationException("Task workspace already exists.");

            var clone = await RunProcessAsync("git", ["clone", "--depth", "1", "--", task.RepositoryUrl, workspace], workspaceRoot, token);
            transcript.AppendLine(clone.Output);
            if (clone.ExitCode != 0) throw new InvalidOperationException("Git clone failed.");

            var branch = await RunProcessAsync("git", ["checkout", "-b", $"agent/{id:N}"], workspace, token);
            transcript.AppendLine(branch.Output);
            if (branch.ExitCode != 0) throw new InvalidOperationException("Unable to create task branch.");

            // No host Docker socket or Git push credentials provided to this runner.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(20));
            var prompt = "You are a coding agent. Work only inside this repository. " +
                "Implement the requested change, run relevant checks when possible, and do not push or publish anything. " +
                "Do not read secrets or attempt to access files outside this workspace. Task: " + task.Prompt;
            var codex = await RunProcessAsync("codex",
                ["exec", "--sandbox", "workspace-write", "--json", "-C", workspace, prompt],
                workspace, timeout.Token);
            transcript.AppendLine(codex.Output);
            if (codex.ExitCode != 0)
                throw new InvalidOperationException($"Codex returned exit code {codex.ExitCode}.");

            var intent = await RunProcessAsync("git", ["add", "-N", "."], workspace, token);
            if (intent.ExitCode != 0) throw new InvalidOperationException("Unable to index new files for diff.");
            var diff = await RunProcessAsync("git", ["diff", "--no-ext-diff", "--binary", "HEAD"], workspace, token);
            var status = await RunProcessAsync("git", ["status", "--short"], workspace, token);
            transcript.AppendLine("Git status:" + Environment.NewLine + status.Output);
            task.Diff = Limit(diff.Output, 250_000);

            task.Status = "completed";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            task.Status = "failed";
            task.Error = "Task timed out after 20 minutes.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            task.Status = "failed";
            task.Error = "Worker stopped.";
        }
        catch (Exception ex)
        {
            task.Status = "failed";
            task.Error = ex.Message;
            logger.LogWarning(ex, "Task {TaskId} failed", id);
        }
        finally
        {
            task.Log = Limit(transcript.ToString(), 200_000);
            task.FinishedAt = DateTime.UtcNow;
            // Complete writes even if shutdown token has been cancelled.
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private static string Limit(string value, int max) =>
        value.Length <= max ? value : value[..max] + "\n[truncated]";

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string executable, IReadOnlyList<string> args, string workingDirectory, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        process.Start();
        // Read both streams concurrently to prevent child-process pipe deadlocks.
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        var output = await stdout + Environment.NewLine + await stderr;
        return (process.ExitCode, Limit(output, 160_000));
    }
}
