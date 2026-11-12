using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace DeveloperTeam.Services;

/// <summary>Sequential, bounded checks after a coding turn. Reviewer is read-only and never publishes.</summary>
public static class AgentValidation
{
    public delegate Task<(int ExitCode, string Output)> CommandRunner(
        string executable, IReadOnlyList<string> args, string workingDirectory,
        CancellationToken token, IDictionary<string, string>? environment);

    public sealed record Result(string TestStatus, string TestLog, string ReviewStatus, string ReviewLog);

    public static async Task<Result> RunAsync(
        string workspace, IConfiguration config, CommandRunner run, CancellationToken ct,
        Func<int, string, CancellationToken, Task<string>>? fix = null,
        Action<string, string>? recordTests = null,
        Func<CancellationToken, Task>? prepareReview = null)
    {
        var maxAttempts = config.GetValue<int?>("Worker:MaxFixAttempts") ?? 2;
        if (maxAttempts < 0) throw new ArgumentOutOfRangeException(nameof(config), "Worker:MaxFixAttempts must be nonnegative.");
        var history = new StringBuilder();
        var tests = await RunTestsAsync(workspace, run, ct);
        Record(0);
        for (var attempt = 1; tests.Status == "failed" && !IsInfrastructureFailure(tests.Log) && fix is not null && attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            history.AppendLine($"Fix attempt {attempt}: failure reason:\n{Clip(tests.Log)}");
            recordTests?.Invoke(tests.Status, history.ToString());
            try
            {
                var changes = await fix(attempt, tests.Log, ct);
                history.AppendLine("Developer corrections:\n" + Clip(changes));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                history.AppendLine("Developer correction failed: " + Clip(ex.Message));
            }
            recordTests?.Invoke(tests.Status, history.ToString());
            // Even a failed Developer process may have changed files; always verify them.
            tests = await RunTestsAsync(workspace, run, ct);
            Record(attempt);
            if (attempt == maxAttempts) break; // Avoid overflow for int.MaxValue.
        }
        history.AppendLine("Final test status: " + tests.Status);
        recordTests?.Invoke(tests.Status, history.ToString());
        if (prepareReview is not null) await prepareReview(ct);
        var review = await RunReviewAsync(workspace, config, run, ct);
        return new Result(tests.Status, history.ToString(), review.Status, review.Log);

        void Record(int attempt)
        {
            history.AppendLine($"Test attempt {attempt}: {tests.Status}\n{Clip(tests.Log)}");
            recordTests?.Invoke(tests.Status, history.ToString());
        }
    }

    // Do not spend extra Codex calls trying to fix a broken test environment.
    public static bool IsInfrastructureFailure(string log) =>
        log.Contains("NuGet-Migrations", StringComparison.OrdinalIgnoreCase) ||
        log.Contains("Access to the path '/.dotnet' is denied", StringComparison.OrdinalIgnoreCase) ||
        (log.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) &&
         log.Contains("dotnet", StringComparison.OrdinalIgnoreCase));

    private static string Clip(string text) => text.Length <= 12_000 ? text : text[..6_000] + "\n[truncated]\n" + text[^6_000:];

    private static async Task<(string Status, string Log)> RunTestsAsync(
        string workspace, CommandRunner run, CancellationToken ct)
    {
        try
        {
            // Do not execute arbitrary repo-provided scripts. This first iteration handles .NET only.
            var solutions = Directory.EnumerateFiles(workspace, "*.sln", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(workspace, "*.slnx", SearchOption.TopDirectoryOnly))
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            var testProjects = Directory.EnumerateFiles(workspace, "*.csproj", SearchOption.AllDirectories)
                .Where(p => !Path.GetRelativePath(workspace, p).Split(Path.DirectorySeparatorChar).Any(s => s is "obj" or "bin" or ".git"))
                .Where(p => p.Contains("Test", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            if (testProjects.Count == 0)
                return ("not_configured", "No .NET test project detected. Automatic test execution was skipped.");

            // Run the root solution if present; otherwise run each test project.
            var targets = solutions.Count > 0 ? solutions.Take(1) : testProjects.AsEnumerable();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(6));
            var output = new StringBuilder();
            foreach (var target in targets)
            {
                var relative = Path.GetRelativePath(workspace, target);
                output.AppendLine("$ dotnet test " + relative);
                var check = await run("dotnet", ["test", relative, "--nologo", "--verbosity", "minimal"],
                    workspace, timeout.Token,
                    new Dictionary<string, string>
                    {
                        ["HOME"] = "/tmp/dotnet-home",
                        ["XDG_DATA_HOME"] = "/tmp/dotnet-home/.local/share",
                        ["DOTNET_CLI_HOME"] = "/tmp/dotnet-home",
                        ["NUGET_PACKAGES"] = "/tmp/nuget-packages",
                        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
                    });
                output.AppendLine(check.Output);
                if (check.ExitCode != 0)
                    return ("failed", output.ToString());
            }
            return ("passed", output.ToString());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ("failed", "dotnet test timed out after 6 minutes.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ("failed", ex.Message);
        }
    }

    private static async Task<(string Status, string Log)> RunReviewAsync(
        string workspace, IConfiguration config, CommandRunner run, CancellationToken ct)
    {
        var key = config["OpenAI:ApiKey"];
        var model = config["OpenAI:ReviewerModel"] ?? config["OpenAI:Model"];
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(model))
            return ("not_configured", "OpenAI reviewer credentials or model are missing.");

        const string prompt = """
            You are a SEPARATE code reviewer. Review the current uncommitted changes
            relative to HEAD in this repository. Read the changed files and relevant context.
            DO NOT modify files, run tests, commit, push, or use any credentials.
            Look for functional bugs, security issues, regressions, missing validation
            and insufficient test coverage. Explain only actionable findings with file
            names and locations, ordered by severity. If you find none, say "No actionable findings".
            """;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var result = await run("codex",
                ["exec", "--sandbox", "read-only", "--json", "--ephemeral",
                    "-C", workspace, "--model", model,
                    "-c", "model_provider=\"openai\"", prompt],
                workspace, timeout.Token,
                new Dictionary<string, string>
                {
                    ["CODEX_API_KEY"] = key,
                    ["OPENAI_BASE_URL"] = "https://api.openai.com/v1"
                });
            var summary = ExtractReviewerText(result.Output);
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(summary)
                ? ("reviewed", summary)
                : ("failed", "Reviewer exited with code " + result.ExitCode + ": " +
                    (string.IsNullOrWhiteSpace(summary) ? "No final reviewer message was returned." : summary));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ("failed", "Reviewer timed out after 5 minutes.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ("failed", ex.Message);
        }
    }
    // Codex --json emits NDJSON lifecycle events. Only the final agent message
    // belongs in the human-readable report, not command traces or tool output.
    public static string ExtractReviewerText(string output)
    {
        string? lastMessage = null;
        foreach (var line in output.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (!root.TryGetProperty("type", out var kind) ||
                    kind.GetString() != "item.completed" ||
                    !root.TryGetProperty("item", out var item) ||
                    !item.TryGetProperty("type", out var itemType) ||
                    itemType.GetString() != "agent_message" ||
                    !item.TryGetProperty("text", out var text))
                    continue;
                var message = text.GetString();
                if (!string.IsNullOrWhiteSpace(message)) lastMessage = message.Trim();
            }
            catch (JsonException) { /* Non-JSON stderr lines are not review findings. */ }
        }
        return lastMessage ?? "";
    }
}
