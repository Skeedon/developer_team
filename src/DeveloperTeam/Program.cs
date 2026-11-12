using DeveloperTeam.Data;
using DeveloperTeam.Endpoints;
using DeveloperTeam.Workers;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");
builder.Services.AddDbContextFactory<AgentDbContext>(options => options.UseNpgsql(connectionString));

var role = builder.Configuration["APP_ROLE"] ?? "api";
if (role.Equals("worker", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddHostedService<AgentWorker>();

var app = builder.Build();
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<AgentDbContext>>().CreateDbContextAsync())
    await db.Database.EnsureCreatedAsync();

app.MapGet("/health", () => Results.Ok(new { status = "ok", role }));
if (role.Equals("api", StringComparison.OrdinalIgnoreCase))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapTaskEndpoints();
    app.MapArtifactEndpoints();
}
await app.RunAsync();
