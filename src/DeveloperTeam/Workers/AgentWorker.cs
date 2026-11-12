using System.Diagnostics;
using System.Text;
using DeveloperTeam.Data;
using DeveloperTeam.Domain;
using Microsoft.EntityFrameworkCore;

namespace DeveloperTeam.Workers;

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
        var workspaceRoot = config["Worker:WorkspaceRoot"] ?? "/workspaces";
        var workspace = Path.Combine(workspaceRoot, id.ToString("N"));
        task.WorkspacePath = workspace;
        var transcript = new StringBuilder();
        try
        {
            var apiKey = config["BetterToken:ApiKey"];
            var baseUrl = config["BetterToken:BaseUrl"] ?? "https://bettertoken.ai/v1";
            var model = config["BetterToken:Model"];
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("BetterToken:ApiKey is missing in appsettings.Local.json.");
            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("BetterToken:Model must contain an available model ID.");
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var providerUri) ||
                providerUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("BetterToken:BaseUrl must be a valid HTTPS URL.");
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
            timeout.CancelAfter(TimeSpan.FromMinutes(config.GetValue<int?>("Worker:TaskTimeoutMinutes") ?? 20));
            var prompt = "You are a coding agent. Work only inside this repository. " +
                "Implement the requested change, run relevant checks when possible, and do not push or publish anything. " +
                "Do not read secrets or attempt to access files outside this workspace. Task: " + task.Prompt;
            var codex = await RunProcessAsync("codex",
                ["exec", "--sandbox", "workspace-write", "--json", "-C", workspace,
                    "--model", model,
                    "-c", "model_provider=bettertoken",
                    "-c", "model_providers.bettertoken={name=\\"BetterToken\\",base_url=\\"" + baseUrl + "\\",env_key=\\"BETTERTOKEN_API_KEY\\",wire_api=\\"responses\\"}",
                    prompt],
                workspace, timeout.Token, new Dictionary<string, string> { ["BETTERTOKEN_API_KEY"] = apiKey });
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
        string executable, IReadOnlyList<string> args, string workingDirectory, CancellationToken token, IDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
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
