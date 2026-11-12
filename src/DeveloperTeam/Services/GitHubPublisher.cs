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
            "commit", "-m", "Agent: " + task.Id.ToString("N"));

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
