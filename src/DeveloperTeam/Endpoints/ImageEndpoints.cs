using DeveloperTeam.Data;
using DeveloperTeam.Services;
using Microsoft.EntityFrameworkCore;

namespace DeveloperTeam.Endpoints;

public static class ImageEndpoints
{
    public static IEndpointRouteBuilder MapImageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tasks/{id:guid}/images");

        group.MapGet("/", async (Guid id, IDbContextFactory<AgentDbContext> factory) =>
        {
            await using var db = await factory.CreateDbContextAsync();
            if (!await db.Tasks.AsNoTracking().AnyAsync(t => t.Id == id))
                return Results.NotFound();
            var assets = await db.GeneratedAssets.AsNoTracking().Where(x => x.TaskId == id)
                .OrderBy(x => x.CreatedAt)
                .Select(x => new
                {
                    x.Id, x.Path, x.Status, x.Size, x.Transparent,
                    x.DraftAttempts, x.FinalAttempts, x.Score, x.Review,
                    x.ReservedUsd, x.ImageInputTokens, x.ImageOutputTokens,
                    x.EvaluatorInputTokens, x.EvaluatorOutputTokens, x.Error,
                    DraftUrl = "/api/tasks/" + id + "/images/" + x.Id + "/draft",
                    FinalUrl = "/api/tasks/" + id + "/images/" + x.Id + "/final"
                }).ToListAsync();
            return Results.Ok(assets);
        });

        group.MapGet("/{assetId:guid}/{kind}", async (Guid id, Guid assetId, string kind,
            IDbContextFactory<AgentDbContext> factory, IConfiguration config) =>
        {
            if (kind != "draft" && kind != "final")
                return Results.NotFound();
            await using var db = await factory.CreateDbContextAsync();
            var asset = await db.GeneratedAssets.AsNoTracking()
                .SingleOrDefaultAsync(a => a.Id == assetId && a.TaskId == id);
            if (asset is null) return Results.NotFound();
            var workspace = Path.Combine(config["Worker:WorkspaceRoot"] ?? "/workspaces", id.ToString("N"));
            var file = kind == "draft"
                ? Path.Combine(workspace, ".git", "developer-team-image-drafts", assetId.ToString("N") + ".png")
                : ImageAssetManifest.ResolveSafeOutput(workspace, asset.Path);
            if (!File.Exists(file)) return Results.NotFound();
            return Results.File(file, "image/png", enableRangeProcessing: false);
        });

        return app;
    }
}
