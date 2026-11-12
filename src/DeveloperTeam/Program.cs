using DeveloperTeam.Data;
using DeveloperTeam.Endpoints;
using DeveloperTeam.Validation;
using DeveloperTeam.Workers;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
AppSettingsValidation.Validate(builder.Configuration);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");
builder.Services.AddDbContextFactory<AgentDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddHttpClient<DeveloperTeam.Services.GitHubPublisher>();

var role = builder.Configuration["APP_ROLE"] ?? "api";
if (role.Equals("worker", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddHostedService<AgentWorker>();

var app = builder.Build();
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<AgentDbContext>>().CreateDbContextAsync())
{
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "ReviewerMemories" (
            "RepositoryKey" text PRIMARY KEY,
            "Notes" text NOT NULL,
            "UpdatedAt" timestamp with time zone NOT NULL
        );
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "Role" text NOT NULL DEFAULT 'developer';
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "LastPublicationRetryAt" timestamp with time zone NULL;
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "DeveloperInputTokens" bigint NOT NULL DEFAULT 0;
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "DeveloperCachedTokens" bigint NOT NULL DEFAULT 0;
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "DeveloperOutputTokens" bigint NOT NULL DEFAULT 0;
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "ReviewerInputTokens" bigint NOT NULL DEFAULT 0;
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "ReviewerCachedTokens" bigint NOT NULL DEFAULT 0;
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "ReviewerOutputTokens" bigint NOT NULL DEFAULT 0;
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "HasDeveloperMetrics" boolean NOT NULL DEFAULT FALSE;
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "HasReviewerMetrics" boolean NOT NULL DEFAULT FALSE;
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "FixAttempts" integer NULL;
        UPDATE "Tasks" SET "HasDeveloperMetrics" = TRUE
            WHERE NOT "HasDeveloperMetrics" AND ("DeveloperInputTokens" > 0 OR "DeveloperCachedTokens" > 0 OR "DeveloperOutputTokens" > 0);
        UPDATE "Tasks" SET "HasReviewerMetrics" = TRUE
            WHERE NOT "HasReviewerMetrics" AND ("ReviewerInputTokens" > 0 OR "ReviewerCachedTokens" > 0 OR "ReviewerOutputTokens" > 0);
        CREATE INDEX IF NOT EXISTS "IX_Tasks_CreatedAt" ON "Tasks" ("CreatedAt");
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "InitialDiff" text NULL;
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "TestStatus" text NULL;
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "TestLog" text NULL;
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "ReviewStatus" text NULL;
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "ReviewLog" text NULL;
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "BaseBranch" text NULL;
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "CreatePullRequest" boolean NOT NULL DEFAULT FALSE;
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "PullRequestUrl" text NULL;
        ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "PullRequestBranch" text NULL;
        """);
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", role }));
if (role.Equals("api", StringComparison.OrdinalIgnoreCase))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapTaskEndpoints();
    app.MapAnalyticsEndpoints();
    app.MapArtifactEndpoints();
}
await app.RunAsync();
