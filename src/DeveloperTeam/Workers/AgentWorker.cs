using System.Diagnostics;
using System.Text;
using DeveloperTeam.Data;
using DeveloperTeam.Domain;
using Microsoft.EntityFrameworkCore;

namespace DeveloperTeam.Workers;

public sealed class AgentWorker(
    IDbContextFactory<AgentDbContext> factory,
    ILogger<AgentWorker> logger,
    IConfiguration config,
    DeveloperTeam.Services.GitHubPublisher publisher) : BackgroundService
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
                else
                {
                    var retry = await db.Tasks.AsNoTracking().Where(t => t.Status == "publish_queued")
                        .OrderBy(t => t.CreatedAt).Select(t => t.Id).FirstOrDefaultAsync(stoppingToken);
                    if (retry == Guid.Empty)
                        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                    else
                    {
                        var claimed = await db.Tasks.Where(t => t.Id == retry && t.Status == "publish_queued")
                            .ExecuteUpdateAsync(x => x.SetProperty(t => t.Status, "publishing"), stoppingToken);
                        if (claimed == 1) await RetryPublishAsync(retry, stoppingToken);
                    }
                }
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
        var workspaceRoot = config["Worker:WorkspaceRoot"] ?? "/workspaces";
        var workspace = Path.Combine(workspaceRoot, id.ToString("N"));
        task.WorkspacePath = workspace;
        var transcript = new StringBuilder();
        try
        {
            var apiKey = config["OpenAI:ApiKey"];
            var model = config["OpenAI:Model"];
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("OpenAI:ApiKey is missing in appsettings.Local.json.");
            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("OpenAI:Model must contain an API-accessible Codex model ID.");
            Directory.CreateDirectory(workspaceRoot);
            if (Directory.Exists(workspace))
                throw new InvalidOperationException("Task workspace already exists.");

            var cloneArgs = new List<string> { "clone", "--depth", "1" };
            if (!string.IsNullOrWhiteSpace(task.BaseBranch))
                cloneArgs.AddRange(["--branch", task.BaseBranch, "--single-branch"]);
            cloneArgs.AddRange(["--", task.RepositoryUrl, workspace]);
            var clone = await RunProcessAsync("git", cloneArgs, workspaceRoot, token);
            transcript.AppendLine(clone.Output);
            if (clone.ExitCode != 0) throw new InvalidOperationException("Git clone failed.");

            var sourceBranch = await RunProcessAsync("git", ["branch", "--show-current"], workspace, token);
            if (sourceBranch.ExitCode != 0 || string.IsNullOrWhiteSpace(sourceBranch.Output))
                throw new InvalidOperationException("Unable to resolve the source branch.");
            task.BaseBranch = sourceBranch.Output.Trim();
            transcript.AppendLine("Source branch: " + task.BaseBranch);
            var branch = await RunProcessAsync("git", ["checkout", "-b", $"agent/{id:N}"], workspace, token);
            transcript.AppendLine(branch.Output);
            if (branch.ExitCode != 0) throw new InvalidOperationException("Unable to create task branch.");

            // No host Docker socket or Git push credentials provided to this runner.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(config.GetValue<int?>("Worker:TaskTimeoutMinutes") ?? 20));
            var prompt = "You are a coding agent. Work only inside this repository. " +
                "Implement the requested change, run relevant checks when possible, and do not push or publish anything. " +
                "Do not read secrets or attempt to access files outside this workspace. Task: " + task.Prompt;
            var codex = await RunProcessAsync("codex",
                ["exec", "--sandbox", "workspace-write", "--json", "--ephemeral",
                    "-C", workspace, "--model", model,
                    "-c", "model_provider=\"openai\"",
                    prompt],
                workspace, timeout.Token,
                new Dictionary<string, string>
                {
                    ["CODEX_API_KEY"] = apiKey,
                    ["OPENAI_BASE_URL"] = "https://api.openai.com/v1"
                });
            transcript.AppendLine(codex.Output);
            if (codex.ExitCode != 0)
                throw new InvalidOperationException($"Codex returned exit code {codex.ExitCode}.");

            var intent = await RunProcessAsync("git", ["add", "-N", "."], workspace, token);
            if (intent.ExitCode != 0) throw new InvalidOperationException("Unable to index new files for diff.");
            var diff = await RunProcessAsync("git", ["diff", "--no-ext-diff", "--binary", "HEAD"], workspace, token);
            var status = await RunProcessAsync("git", ["status", "--short"], workspace, token);
            transcript.AppendLine("Git status:" + Environment.NewLine + status.Output);
            task.Diff = Limit(diff.Output, 250_000);

            if (task.CreatePullRequest)
            {
                task.Status = "publishing";
                DeveloperTeam.Services.GitHubPublisher.PublishedPullRequest published;
                try { published = await publisher.PublishAsync(task, workspace, token); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    task.Status = "publish_failed";
                    throw;
                }
                task.PullRequestUrl = published.Url;
                task.PullRequestBranch = published.Branch;
                transcript.AppendLine("Draft pull request: " + published.Url);
            }
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
            if (task.Status != "publish_failed") task.Status = "failed";
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

    // Publish a saved workspace without executing Codex or spending API tokens.
    private async Task RetryPublishAsync(Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var task = await db.Tasks.FirstAsync(t => t.Id == id, token);
        var workspace = Path.Combine(config["Worker:WorkspaceRoot"] ?? "/workspaces", id.ToString("N"));
        try
        {
            if (string.IsNullOrWhiteSpace(task.Diff) || !Directory.Exists(Path.Combine(workspace, ".git")))
                throw new InvalidOperationException("Saved agent workspace or diff is missing.");
            if (string.IsNullOrWhiteSpace(task.BaseBranch))
                throw new InvalidOperationException("Source branch is missing.");

            var result = await publisher.PublishAsync(task, workspace, token);
            task.PullRequestUrl = result.Url;
            task.PullRequestBranch = result.Branch;
            task.Status = "completed";
            task.Error = null;
            task.Log = Limit((task.Log ?? "") + "\nPR publication retried: " + result.Url, 200_000);
        }
        catch (Exception ex)
        {
            task.Status = "publish_failed";
            task.Error = ex.Message;
            task.Log = Limit((task.Log ?? "") + "\nPR publication retry failed: " + ex.Message, 200_000);
            logger.LogWarning(ex, "Retry publishing task {TaskId} failed", id);
        }
        finally
        {
            task.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private static string Limit(string value, int max) =>
        value.Length <= max ? value : value[..max] + "\n[truncated]";

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string executable, IReadOnlyList<string> args, string workingDirectory, CancellationToken token, IDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = start };
        process.Start();
        process.StandardInput.Close(); // Codex exec must receive EOF in non-interactive Docker runs.
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
