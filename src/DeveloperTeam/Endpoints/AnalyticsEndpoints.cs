using DeveloperTeam.Data;
using DeveloperTeam.Services;
using Microsoft.EntityFrameworkCore;

namespace DeveloperTeam.Endpoints;

public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/analytics", async (string? period, IDbContextFactory<AgentDbContext> factory, CancellationToken ct) =>
        {
            period ??= "7";
            if (period is not ("7" or "30" or "all"))
                return Results.BadRequest(new { error = "Period must be 7, 30 or all." });
            await using var db = await factory.CreateDbContextAsync(ct);
            // One consistent snapshot for totals, chart and recent tasks.
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
            var tasks = AgentAnalytics.InPeriod(db.Tasks.AsNoTracking(), period, DateTime.UtcNow);
            var totals = await AgentAnalytics.Totals(tasks).SingleOrDefaultAsync(ct) ?? new AnalyticsTotals();
            var daily = await AgentAnalytics.Daily(tasks).ToListAsync(ct);
            var recent = await tasks.OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id).Take(10)
                .Select(t => new
                {
                    t.Id, t.Prompt, t.RepositoryUrl, t.CreatedAt, t.Status, t.TestStatus, t.ReviewStatus,
                    DeveloperInput = t.HasDeveloperMetrics ? (long?)t.DeveloperInputTokens : null,
                    DeveloperCachedInput = t.HasDeveloperMetrics ? (long?)t.DeveloperCachedTokens : null,
                    DeveloperOutput = t.HasDeveloperMetrics ? (long?)t.DeveloperOutputTokens : null,
                    ReviewerInput = t.HasReviewerMetrics ? (long?)t.ReviewerInputTokens : null,
                    ReviewerCachedInput = t.HasReviewerMetrics ? (long?)t.ReviewerCachedTokens : null,
                    ReviewerOutput = t.HasReviewerMetrics ? (long?)t.ReviewerOutputTokens : null,
                    t.FixAttempts
                }).ToListAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { period, totals, daily, recent });
        });
        return app;
    }
}
