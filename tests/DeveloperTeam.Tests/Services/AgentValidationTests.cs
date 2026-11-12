using DeveloperTeam.Services;
using Xunit;
using Microsoft.Extensions.Configuration;

namespace DeveloperTeam.Tests.Services;

public class AgentValidationTests
{
    [Fact]
    public void ExtractReviewerText_ReturnsFinalAgentMessageOnly()
    {
        var transcript = """
            {"type":"thread.started","thread_id":"abc"}
            {"type":"item.completed","item":{"type":"agent_message","text":"Checking changes."}}
            {"type":"item.completed","item":{"type":"command_execution","aggregated_output":"sensitive command output"}}
            {"type":"item.completed","item":{"type":"agent_message","text":"No actionable findings"}}
            {"type":"turn.completed"}
            """;

        Assert.Equal("No actionable findings", AgentValidation.ExtractReviewerText(transcript));
    }

    [Fact]
    public void ExtractReviewerText_IgnoresNonJsonAndMissingMessage()
    {
        const string transcript = """
            WARNING: setup failed
            {"type":"item.completed","item":{"type":"command_execution","aggregated_output":"noise"}}
            {"type":"turn.completed"}
            """;

        Assert.Equal("", AgentValidation.ExtractReviewerText(transcript));
    }

    [Fact]
    public void ExtractReviewerText_PreservesFindingFormatting()
    {
        const string transcript = """
            {"type":"item.completed","item":{"type":"agent_message","text":"High: src/file.cs:12\nFix validation."}}
            """;

        Assert.Equal("High: src/file.cs:12\nFix validation.", AgentValidation.ExtractReviewerText(transcript));
    }

    [Fact]
    public void ExtractReviewerText_PreservesLiteralBackslashN()
    {
        const string transcript = """
            {"type":"item.completed","item":{"type":"agent_message","text":"Preserve the literal \\n sequence."}}
            """;

        Assert.Equal("Preserve the literal \\n sequence.", AgentValidation.ExtractReviewerText(transcript));
    }

    [Theory]
    [InlineData(null, 9, 2, "failed")]
    [InlineData(null, 9, 2, "failed", true)]
    [InlineData(null, 0, 0, "passed", false, true)]
    [InlineData("0", 9, 0, "failed")]
    [InlineData("1", 9, 1, "failed")]
    [InlineData("3", 9, 3, "failed")]
    [InlineData(null, 0, 0, "passed")]
    [InlineData(null, 1, 1, "passed")]
    [InlineData(null, 2, 2, "passed")]
    public async Task FixLoop_BoundsAttemptsAndReviewsFinalState(
        string? limit, int failures, int expectedFixes, string expectedStatus, bool correctionFails = false, bool reviewerFails = false)
    {
        var workspace = Path.Combine(Environment.CurrentDirectory, "obj", "validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "Example.Tests.csproj"), "<Project />");
        try
        {
            var events = new List<string>();
            string? stage = null;
            var stages = new List<string>();
            var tests = 0;
            var fixes = 0;
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:MaxFixAttempts"] = limit,
                ["OpenAI:ApiKey"] = "test-key",
                ["OpenAI:Model"] = "test-model"
            }).Build();
            Task<(int, string)> Run(string exe, IReadOnlyList<string> args, string cwd,
                CancellationToken ct, IDictionary<string, string>? env)
            {
                Assert.Equal(workspace, cwd);
                if (exe == "dotnet")
                {
                    Assert.Equal("testing", stage);
                    events.Add("test");
                    return Task.FromResult((++tests <= failures ? 1 : 0, "test output " + tests));
                }
                Assert.Equal("review", stage);
                events.Add("review");
                Assert.Contains("read-only", args);
                return Task.FromResult((reviewerFails ? 1 : 0, """
                    {"type":"item.completed","item":{"type":"agent_message","text":"No actionable findings"}}
                    """));
            }
            var result = await AgentValidation.RunAsync(workspace, config, Run, CancellationToken.None,
                (attempt, error, ct) =>
                {
                    Assert.Equal("testing", stage);
                    events.Add("fix");
                    Assert.Equal(++fixes, attempt);
                    Assert.Contains("test output " + attempt, error);
                    if (correctionFails) throw new TimeoutException("Developer timed out");
                    return Task.FromResult("correction " + attempt);
                }, recordStage: (value, _) =>
                {
                    stage = value;
                    stages.Add(value);
                    return Task.CompletedTask;
                });
            Assert.Equal(new[] { "testing", "review" }, stages);
            Assert.Equal(expectedFixes, fixes);
            Assert.Equal(expectedFixes + 1, tests);
            Assert.Equal(expectedStatus, result.TestStatus);
            Assert.Equal(reviewerFails ? "failed" : "reviewed", result.ReviewStatus);
            Assert.Equal(Enumerable.Repeat(new[] { "test", "fix" }, fixes).SelectMany(x => x)
                .Concat(new[] { "test", "review" }), events);
            for (var attempt = 1; attempt <= fixes; attempt++)
            {
                Assert.Contains($"Fix attempt {attempt}: failure reason:", result.TestLog);
                Assert.Contains(correctionFails ? "Developer correction failed: Developer timed out" : "correction " + attempt,
                    result.TestLog);
                Assert.Contains($"Test attempt {attempt}:", result.TestLog);
            }
        }
        finally { Directory.Delete(workspace, true); }
    }

    [Fact]
    public async Task NoTestProject_SkipsFixesAndStillReviews()
    {
        var workspace = Path.Combine(Environment.CurrentDirectory, "obj", "validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:ApiKey"] = "test-key",
                ["OpenAI:Model"] = "test-model"
            }).Build();
            var reviews = 0;
            var result = await AgentValidation.RunAsync(workspace, config,
                (exe, args, _, _, _) =>
                {
                    Assert.Equal("codex", exe);
                    Assert.Contains("read-only", args);
                    reviews++;
                    return Task.FromResult((0, """
                        {"type":"item.completed","item":{"type":"agent_message","text":"No actionable findings"}}
                        """));
                }, CancellationToken.None,
                (_, _, _) => throw new InvalidOperationException("Unexpected fix"));
            Assert.Equal("not_configured", result.TestStatus);
            Assert.Equal("reviewed", result.ReviewStatus);
            Assert.Equal(1, reviews);
        }
        finally { Directory.Delete(workspace, true); }
    }

    [Fact]
    public async Task Cancellation_StopsWithoutAnotherFixOrReview()
    {
        var workspace = Path.Combine(Environment.CurrentDirectory, "obj", "validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "Example.Tests.csproj"), "<Project />");
        try
        {
            using var cancelled = new CancellationTokenSource();
            var commands = 0;
            var resultLog = "";
            string? stage = null;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AgentValidation.RunAsync(
                workspace, new ConfigurationBuilder().Build(),
                (_, _, _, _, _) =>
                {
                    commands++;
                    return Task.FromResult((1, "failing test"));
                }, cancelled.Token,
                (_, _, _) =>
                {
                    cancelled.Cancel();
                    throw new OperationCanceledException(cancelled.Token);
                }, (_, log) => resultLog = log,
                recordStage: (value, _) => { stage = value; return Task.CompletedTask; }));
            Assert.Equal("testing", stage);
            Assert.Equal(1, commands);
            Assert.Contains("Test attempt 0: failed", resultLog);
        }
        finally { Directory.Delete(workspace, true); }
    }

    [Fact]
    public async Task PostFixNewFiles_AreRegisteredBeforeReviewerRuns()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "review-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "Example.Tests.csproj"), "<Project />");
        try
        {
            bool registered = false;
            int testAttempts = 0;
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:ApiKey"] = "test-key",
                ["OpenAI:Model"] = "test-model",
                ["Worker:MaxFixAttempts"] = "1"
            }).Build();

            var result = await AgentValidation.RunAsync(workspace, config,
                (exe, args, cwd, _, _) =>
                {
                    if (exe == "dotnet")
                        return Task.FromResult((++testAttempts == 1 ? 1 : 0, "test error"));
                    Assert.Equal("codex", exe);
                    Assert.True(registered, "New files must be registered before Reviewer runs.");
                    Assert.True(File.Exists(Path.Combine(workspace, "NewFile.cs")));
                    return Task.FromResult((0, "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"No actionable findings\"}}"));
                }, CancellationToken.None,
                (_, _, _) =>
                {
                    File.WriteAllText(Path.Combine(workspace, "NewFile.cs"), "namespace Example;");
                    return Task.FromResult("Added NewFile.cs");
                },
                null,
                ct =>
                {
                    Assert.True(File.Exists(Path.Combine(workspace, "NewFile.cs")));
                    registered = true;
                    return Task.CompletedTask;
                });

            Assert.Equal("passed", result.TestStatus);
            Assert.Equal("reviewed", result.ReviewStatus);
            Assert.Equal(2, testAttempts);
        }
        finally { Directory.Delete(workspace, true); }
    }

    [Fact]
    public async Task InfrastructureFailure_DoesNotInvokeDeveloperFix()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "infra-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "Example.Tests.csproj"), "<Project />");
        try
        {
            var fixes = 0;
            var testRuns = 0;
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:ApiKey"] = "test-key",
                ["OpenAI:Model"] = "test-model"
            }).Build();

            var result = await AgentValidation.RunAsync(workspace, config,
                (exe, args, cwd, _, env) =>
                {
                    if (exe == "dotnet")
                    {
                        testRuns++;
                        Assert.Equal("/tmp/dotnet-home", env!["HOME"]);
                        return Task.FromResult((1, "NuGet-Migrations: errno == EACCES"));
                    }
                    return Task.FromResult((0, "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Review complete\"}}"));
                }, CancellationToken.None,
                (_, _, _) =>
                {
                    fixes++;
                    return Task.FromResult("unexpected correction");
                });

            Assert.Equal("failed", result.TestStatus);
            Assert.Equal(1, testRuns);
            Assert.Equal(0, fixes);
            Assert.Equal("reviewed", result.ReviewStatus);
        }
        finally { Directory.Delete(workspace, true); }
    }
}
