using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeveloperTeam.Domain;

namespace DeveloperTeam.Services;

public sealed class GitHubPublisher(HttpClient client, IConfiguration configuration)
{
    public record PublishedPullRequest(string Url, string Branch);

    public async Task<PublishedPullRequest> PublishAsync(AgentTask task, string workspace, CancellationToken ct)
    {
        var match = Regex.Match(task.RepositoryUrl,
            @"^https://github\.com/(?<owner>[a-zA-Z0-9][a-zA-Z0-9-]{0,38})/(?<repo>[a-zA-Z0-9_.-]+?)(?:\.git)?/?$");
        if (!match.Success) throw new InvalidOperationException("Unsupported GitHub repository URL.");
        var owner = match.Groups["owner"].Value;
        var repo = match.Groups["repo"].Value;
        var token = configuration["GitHub:Token"];
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("GitHub:Token is required to create a Pull Request.");

        var commitMessage = "Agent: " + task.Id.ToString("N");
        var currentCommit = (await GitAsync(workspace, null, ct, "log", "-1", "--format=%s")).Trim();
        if (currentCommit != commitMessage)
        {
            var status = await GitAsync(workspace, null, ct, "status", "--porcelain");
            if (string.IsNullOrWhiteSpace(status))
                throw new InvalidOperationException("No changes were produced; PR not created.");

            await GitAsync(workspace, null, ct, "-c", "core.hooksPath=/dev/null", "add", "-A");
            var staged = await GitAsync(workspace, null, ct, "diff", "--cached", "--name-only");
            if (string.IsNullOrWhiteSpace(staged))
                throw new InvalidOperationException("No staged changes; PR not created.");

            await GitAsync(workspace, null, ct,
                "-c", "core.hooksPath=/dev/null",
                "-c", "user.name=Developer Team Bot",
                "-c", "user.email=developer-team-bot@users.noreply.github.com",
                "commit", "-m", commitMessage);
        }

        var branch = "agent/" + task.Id.ToString("N");
        var askPassPath = Path.Combine(Path.GetTempPath(), "github-askpass-" + Guid.NewGuid().ToString("N") + ".sh");
        await File.WriteAllTextAsync(askPassPath,
            "#!/bin/sh\ncase \"$1\" in\n *Username*) printf 'x-access-token\\n' ;;\n *Password*) printf '%s\\n' \"$GITHUB_TOKEN\" ;;\nesac\n", ct);
        File.SetUnixFileMode(askPassPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var env = new Dictionary<string, string>
            {
                ["GITHUB_TOKEN"] = token,
                ["GIT_ASKPASS"] = askPassPath,
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GIT_CONFIG_GLOBAL"] = "/dev/null"
            };
            // Never push to an agent-controlled remote URL.
            var destination = $"https://github.com/{owner}/{repo}.git";
            await GitAsync(workspace, env, ct, "-c", "core.hooksPath=/dev/null",
                "push", destination, $"HEAD:refs/heads/{branch}");
        }
        finally { File.Delete(askPassPath); }

        var api = $"https://api.github.com/repos/{owner}/{repo}";
        var baseBranch = task.BaseBranch;
        if (string.IsNullOrWhiteSpace(baseBranch))
            throw new InvalidOperationException("Source branch was not resolved.");

        // If PR creation succeeded before an earlier worker failure, return its URL.
        using (var check = await ApiAsync(HttpMethod.Get,
            api + "/pulls?state=all&head=" + Uri.EscapeDataString(owner + ":" + branch) +
            "&base=" + Uri.EscapeDataString(baseBranch) + "&per_page=30", token, null, ct))
        using (var existing = JsonDocument.Parse(await check.Content.ReadAsStringAsync(ct)))
        {
            if (existing.RootElement.GetArrayLength() > 0)
            {
                var found = existing.RootElement[0].GetProperty("html_url").GetString();
                if (!string.IsNullOrEmpty(found)) return new PublishedPullRequest(found, branch);
            }
        }

        var title = task.Prompt.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (title.Length > 90) title = title[..90] + "…";
        var body = "Automated draft PR by Developer Team.\n\nTask: "
            + task.Prompt + "\n\nChanges have NOT been independently reviewed or tested.";
        using var response = await ApiAsync(HttpMethod.Post, api + "/pulls", token,
            new { title = "Agent: " + title, head = branch, @base = baseBranch, body, draft = true }, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return new PublishedPullRequest(
            json.RootElement.GetProperty("html_url").GetString()
                ?? throw new InvalidOperationException("Missing PR URL."),
            branch);
    }

    public async Task PostReviewReportAsync(AgentTask task, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task.PullRequestUrl)) return;
        var match = Regex.Match(task.PullRequestUrl,
            @"^https://github\.com/(?<owner>[a-zA-Z0-9-]+)/(?<repo>[a-zA-Z0-9_.-]+)/pull/(?<number>[0-9]+)$");
        if (!match.Success) throw new InvalidOperationException("Unexpected PR URL.");
        var token = configuration["GitHub:Token"]
            ?? throw new InvalidOperationException("GitHub:Token missing.");
        var api = $"https://api.github.com/repos/{match.Groups["owner"].Value}/{match.Groups["repo"].Value}/issues/{match.Groups["number"].Value}/comments";
        var review = task.ReviewLog ?? "Reviewer did not run.";
        var tests = task.TestLog ?? "Test runner did not run.";
        var body = "### Developer Team — independent checks\n\n" +
            $"**Tests:** {task.TestStatus ?? "not run"}\n\n" +
            $"**Reviewer:** {task.ReviewStatus ?? "not run"}\n\n" +
            "<details><summary>Test output</summary>\n\n```text\n" +
            tests.Replace("```", "` ` `") + "\n```\n</details>\n\n" +
            "<details><summary>Reviewer findings</summary>\n\n" +
            review + "\n</details>\n\n" +
            "Automated feedback is informational; a human must review before merging.";
        using var response = await ApiAsync(HttpMethod.Post, api, token,
            new { body = body[..Math.Min(body.Length, 50000)] }, ct);
    }

    private async Task<HttpResponseMessage> ApiAsync(
        HttpMethod method, string uri, string token, object? payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("DeveloperTeam/0.2");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (payload is not null) request.Content = JsonContent.Create(payload);
        var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new InvalidOperationException($"GitHub API error HTTP {status}: {error[..Math.Min(error.Length, 300)]}");
        }
        return response;
    }

    private static async Task<string> GitAsync(string workspace, Dictionary<string,string>? env,
        CancellationToken ct, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (env is not null)
            foreach (var (key, value) in env) start.Environment[key] = value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Git operation failed: " + error[..Math.Min(error.Length, 300)]);
        return output;
    }
}
