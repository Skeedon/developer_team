using DeveloperTeam.Data;
using DeveloperTeam.Domain;
using DeveloperTeam.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DeveloperTeam.Tests.Services;

public class AgentAnalyticsTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AggregatesRolesStatusesDurationAndFixes()
    {
        var tasks = new[]
        {
            new AgentTask { Status = "completed", CreatedAt = Now, StartedAt = Now.AddSeconds(-20), FinishedAt = Now,
                HasDeveloperMetrics = true, DeveloperInputTokens = 100, DeveloperCachedTokens = 40, DeveloperOutputTokens = 10,
                HasReviewerMetrics = true, ReviewerInputTokens = 30, ReviewerCachedTokens = 5, ReviewerOutputTokens = 3,
                FixAttempts = 2, TestStatus = "passed" },
            new AgentTask { Status = "failed", CreatedAt = Now, StartedAt = Now.AddSeconds(-40), FinishedAt = Now,
                HasDeveloperMetrics = true, DeveloperInputTokens = 50, DeveloperCachedTokens = 20, DeveloperOutputTokens = 5,
                FixAttempts = 1, TestStatus = "failed" },
            new AgentTask { Status = "publish_failed", CreatedAt = Now, FixAttempts = 1, TestStatus = "passed" },
            new AgentTask { Status = "running", CreatedAt = Now }
        }.AsQueryable();
        var result = AgentAnalytics.Totals(tasks).Single();
        Assert.Equal(4, result.Total);
        Assert.Equal(1, result.Successful);
        Assert.Equal(2, result.Failed);
        Assert.Equal(30d, result.AverageDurationSeconds);
        Assert.Equal(150L, result.DeveloperInput);
        Assert.Equal(60L, result.DeveloperCachedInput);
        Assert.Equal(15L, result.DeveloperOutput);
        Assert.Equal(30L, result.ReviewerInput);
        Assert.Equal(5L, result.ReviewerCachedInput);
        Assert.Equal(3L, result.ReviewerOutput);
        Assert.Equal(4, result.FixAttempts);
        Assert.Equal(2, result.TasksHelpedByFixes);
        Assert.Equal(2, result.DeveloperMeasuredTasks);
    }

    [Fact]
    public void DelayedPublicationRetry_DoesNotInflateAverageExecutionDuration()
    {
        var start = Now.AddMinutes(-2);
        var first = new AgentTask
        {
            Status = "publish_failed", CreatedAt = start.AddMinutes(-1),
            StartedAt = start, FinishedAt = Now
        };
        var other = new AgentTask
        {
            Status = "completed", CreatedAt = start,
            StartedAt = Now.AddMinutes(-1), FinishedAt = Now
        };

        // The PR is republished seven days after the coding work finished.
        first.Status = "completed";
        first.RecordPublicationRetry(Now.AddDays(7));

        Assert.Equal(Now, first.FinishedAt);
        Assert.Equal(Now.AddDays(7), first.LastPublicationRetryAt);
        var totals = AgentAnalytics.Totals(new[] { first, other }.AsQueryable()).Single();
        Assert.Equal(90d, totals.AverageDurationSeconds);
    }

    [Fact]
    public void DesignerTokensAreIndependentFromDeveloperAndReviewer()
    {
        var tasks = new[]
        {
            new AgentTask { CreatedAt = Now, Participants = "designer,developer,reviewer",
                HasDesignerMetrics = true, DesignerInputTokens = 140, DesignerCachedTokens = 90, DesignerOutputTokens = 12,
                HasDeveloperMetrics = true, DeveloperInputTokens = 220, DeveloperCachedTokens = 150, DeveloperOutputTokens = 21,
                HasReviewerMetrics = true, ReviewerInputTokens = 80, ReviewerCachedTokens = 40, ReviewerOutputTokens = 5 },
            new AgentTask { CreatedAt = Now, Participants = "developer,reviewer",
                HasDeveloperMetrics = true, DeveloperInputTokens = 10, DeveloperOutputTokens = 2 }
        }.AsQueryable();
        var totals = AgentAnalytics.Totals(tasks).Single();
        Assert.Equal(1, totals.DesignerMeasuredTasks);
        Assert.Equal(140L, totals.DesignerInput);
        Assert.Equal(90L, totals.DesignerCachedInput);
        Assert.Equal(12L, totals.DesignerOutput);
        Assert.Equal(230L, totals.DeveloperInput);
        Assert.Equal(80L, totals.ReviewerInput);
        var daily = AgentAnalytics.Daily(tasks).Single();
        Assert.Equal(140L, daily.DesignerInput);
        Assert.Equal(12L, daily.DesignerOutput);
    }

    [Fact]
    public void HistoricalMetricsStayUnknownAndMeasuredZerosStayZero()
    {
        var legacy = new AgentTask { CreatedAt = Now.AddDays(-1) };
        var result = AgentAnalytics.Totals(new[] { legacy }.AsQueryable()).Single();
        Assert.Null(result.DeveloperInput);
        Assert.Null(result.DeveloperCachedInput);
        Assert.Null(result.DeveloperOutput);
        Assert.Null(result.ReviewerInput);
        Assert.Null(result.ReviewerCachedInput);
        Assert.Null(result.ReviewerOutput);
        Assert.Null(result.FixAttempts);
        Assert.Null(result.TasksHelpedByFixes);
        Assert.Null(result.AverageDurationSeconds);
        var measured = new AgentTask { CreatedAt = Now, HasDeveloperMetrics = true, FixAttempts = 0 };
        var daily = AgentAnalytics.Daily(new[] { legacy, measured }.AsQueryable()).ToArray();
        Assert.Equal(2, daily.Length);
        Assert.Null(daily[0].DeveloperInput);
        Assert.Equal(0L, daily[1].DeveloperInput);
        Assert.Null(daily[1].ReviewerInput);
        Assert.Equal(0, AgentAnalytics.Totals(new[] { measured }.AsQueryable()).Single().FixAttempts);
        Assert.Empty(AgentAnalytics.Totals(Array.Empty<AgentTask>().AsQueryable()));
    }

    [Theory]
    [InlineData("7", 7)]
    [InlineData("30", 30)]
    public void PeriodUsesInclusiveUtcBoundary(string period, int days)
    {
        var tasks = new[] { new AgentTask { CreatedAt = Now.AddDays(-days) },
            new AgentTask { CreatedAt = Now.AddDays(-days).AddTicks(-1) },
            new AgentTask { CreatedAt = Now.AddTicks(1) } }.AsQueryable();
        Assert.Single(AgentAnalytics.InPeriod(tasks, period, Now));
        Assert.Equal(2, AgentAnalytics.InPeriod(tasks, "all", Now).Count());
        Assert.Throws<ArgumentException>(() => AgentAnalytics.InPeriod(tasks, "invalid", Now));
    }

    [Fact]
    public void QueriesTranslateToPostgresAggregatesWithoutConnecting()
    {
        using var db = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>()
            .UseNpgsql("Host=localhost;Database=analytics_translation").Options);
        var tasks = AgentAnalytics.InPeriod(db.Tasks, "7", Now);
        var totals = AgentAnalytics.Totals(tasks).ToQueryString();
        Assert.Contains("sum(", totals, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("avg(", totals, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", totals);
        var daily = AgentAnalytics.Daily(tasks).ToQueryString();
        Assert.Contains("date_trunc", daily);
        Assert.Contains("GROUP BY", daily);
        Assert.Contains("sum(", daily, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UsageDistinguishesMissingMetricsFromReportedZero()
    {
        Assert.False(CodexTokenUsage.FromNdjson("invalid\n{\"type\":\"turn.completed\"}").HasMetrics);
        var zero = CodexTokenUsage.FromNdjson("""
            {"type":"turn.completed","usage":{"input_tokens":0,"cached_input_tokens":0,"output_tokens":0}}
            """);
        Assert.True(zero.HasMetrics);
        Assert.Equal(0, zero.InputTokens);
        Assert.True((default(CodexTokenUsage) + zero).HasMetrics);
    }
}
