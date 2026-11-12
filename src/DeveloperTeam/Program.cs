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
    app.MapArtifactEndpoints();
}
await app.RunAsync();
