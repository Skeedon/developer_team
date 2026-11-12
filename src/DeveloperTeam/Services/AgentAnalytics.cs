using DeveloperTeam.Domain;

namespace DeveloperTeam.Services;

public sealed class AnalyticsTotals
{
    public int Total { get; set; }
    public int Successful { get; set; }
    public int Failed { get; set; }
    public double? AverageDurationSeconds { get; set; }
    public long? DeveloperInput { get; set; }
    public long? DeveloperCachedInput { get; set; }
    public long? DeveloperOutput { get; set; }
    public long? DesignerInput { get; set; }
    public long? DesignerCachedInput { get; set; }
    public long? DesignerOutput { get; set; }
    public long? ReviewerInput { get; set; }
    public long? ReviewerCachedInput { get; set; }
    public long? ReviewerOutput { get; set; }
    public int DeveloperMeasuredTasks { get; set; }
    public int ReviewerMeasuredTasks { get; set; }
    public int DesignerMeasuredTasks { get; set; }
    public int? FixAttempts { get; set; }
    public int? TasksHelpedByFixes { get; set; }
    public int FixMeasuredTasks { get; set; }
}

public sealed class DailyTokens
{
    public DateTime Date { get; set; }
    public long? DeveloperInput { get; set; }
    public long? DeveloperCachedInput { get; set; }
    public long? DeveloperOutput { get; set; }
    public long? DesignerInput { get; set; }
    public long? DesignerCachedInput { get; set; }
    public long? DesignerOutput { get; set; }
    public long? ReviewerInput { get; set; }
    public long? ReviewerCachedInput { get; set; }
    public long? ReviewerOutput { get; set; }
}

public static class AgentAnalytics
{
    public static IQueryable<AgentTask> InPeriod(IQueryable<AgentTask> tasks, string period, DateTime now) => period switch
    {
        "7" => tasks.Where(t => t.CreatedAt >= now.AddDays(-7) && t.CreatedAt <= now),
        "30" => tasks.Where(t => t.CreatedAt >= now.AddDays(-30) && t.CreatedAt <= now),
        "all" => tasks.Where(t => t.CreatedAt <= now),
        _ => throw new ArgumentException("Period must be 7, 30 or all.", nameof(period))
    };

    // Keep these as IQueryable expressions: PostgreSQL performs every aggregate.
    public static IQueryable<AnalyticsTotals> Totals(IQueryable<AgentTask> tasks) => tasks.GroupBy(t => 1)
        .Select(g => new AnalyticsTotals
        {
            Total = g.Count(),
            Successful = g.Count(t => t.Status == "completed"),
            Failed = g.Count(t => t.Status == "failed" || t.Status == "publish_failed"),
            AverageDurationSeconds = g.Average(t => t.StartedAt != null && t.FinishedAt >= t.StartedAt
                ? (double?)(t.FinishedAt.Value - t.StartedAt.Value).TotalSeconds : null),
            DeveloperMeasuredTasks = g.Count(t => t.HasDeveloperMetrics),
            DesignerMeasuredTasks = g.Count(t => t.HasDesignerMetrics),
            ReviewerMeasuredTasks = g.Count(t => t.HasReviewerMetrics),
            DeveloperInput = g.Any(t => t.HasDeveloperMetrics) ? g.Sum(t => t.HasDeveloperMetrics ? t.DeveloperInputTokens : 0) : null,
            DeveloperCachedInput = g.Any(t => t.HasDeveloperMetrics) ? g.Sum(t => t.HasDeveloperMetrics ? t.DeveloperCachedTokens : 0) : null,
            DeveloperOutput = g.Any(t => t.HasDeveloperMetrics) ? g.Sum(t => t.HasDeveloperMetrics ? t.DeveloperOutputTokens : 0) : null,
            DesignerInput = g.Any(t => t.HasDesignerMetrics) ? g.Sum(t => t.HasDesignerMetrics ? t.DesignerInputTokens : 0) : null,
            DesignerCachedInput = g.Any(t => t.HasDesignerMetrics) ? g.Sum(t => t.HasDesignerMetrics ? t.DesignerCachedTokens : 0) : null,
            DesignerOutput = g.Any(t => t.HasDesignerMetrics) ? g.Sum(t => t.HasDesignerMetrics ? t.DesignerOutputTokens : 0) : null,
            ReviewerInput = g.Any(t => t.HasReviewerMetrics) ? g.Sum(t => t.HasReviewerMetrics ? t.ReviewerInputTokens : 0) : null,
            ReviewerCachedInput = g.Any(t => t.HasReviewerMetrics) ? g.Sum(t => t.HasReviewerMetrics ? t.ReviewerCachedTokens : 0) : null,
            ReviewerOutput = g.Any(t => t.HasReviewerMetrics) ? g.Sum(t => t.HasReviewerMetrics ? t.ReviewerOutputTokens : 0) : null,
            FixMeasuredTasks = g.Count(t => t.FixAttempts != null),
            FixAttempts = g.Any(t => t.FixAttempts != null) ? g.Sum(t => t.FixAttempts ?? 0) : null,
            TasksHelpedByFixes = g.Any(t => t.FixAttempts != null)
                ? g.Count(t => t.FixAttempts > 0 && t.TestStatus == "passed") : null
        });

    public static IQueryable<DailyTokens> Daily(IQueryable<AgentTask> tasks) => tasks.GroupBy(t => t.CreatedAt.Date)
        .Select(g => new DailyTokens
        {
            Date = g.Key,
            DeveloperInput = g.Any(t => t.HasDeveloperMetrics) ? g.Sum(t => t.HasDeveloperMetrics ? t.DeveloperInputTokens : 0) : null,
            DeveloperCachedInput = g.Any(t => t.HasDeveloperMetrics) ? g.Sum(t => t.HasDeveloperMetrics ? t.DeveloperCachedTokens : 0) : null,
            DeveloperOutput = g.Any(t => t.HasDeveloperMetrics) ? g.Sum(t => t.HasDeveloperMetrics ? t.DeveloperOutputTokens : 0) : null,
            DesignerInput = g.Any(t => t.HasDesignerMetrics) ? g.Sum(t => t.HasDesignerMetrics ? t.DesignerInputTokens : 0) : null,
            DesignerCachedInput = g.Any(t => t.HasDesignerMetrics) ? g.Sum(t => t.HasDesignerMetrics ? t.DesignerCachedTokens : 0) : null,
            DesignerOutput = g.Any(t => t.HasDesignerMetrics) ? g.Sum(t => t.HasDesignerMetrics ? t.DesignerOutputTokens : 0) : null,
            ReviewerInput = g.Any(t => t.HasReviewerMetrics) ? g.Sum(t => t.HasReviewerMetrics ? t.ReviewerInputTokens : 0) : null,
            ReviewerCachedInput = g.Any(t => t.HasReviewerMetrics) ? g.Sum(t => t.HasReviewerMetrics ? t.ReviewerCachedTokens : 0) : null,
            ReviewerOutput = g.Any(t => t.HasReviewerMetrics) ? g.Sum(t => t.HasReviewerMetrics ? t.ReviewerOutputTokens : 0) : null
        }).OrderBy(t => t.Date);
}
