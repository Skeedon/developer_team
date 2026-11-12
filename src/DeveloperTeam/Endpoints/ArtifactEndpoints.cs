using System.IO.Compression;
using DeveloperTeam.Data;
using Microsoft.EntityFrameworkCore;

namespace DeveloperTeam.Endpoints;

public static class ArtifactEndpoints
{
    // Never accept an arbitrary path from the request or from database records.
    // Workspaces are rooted at a server-controlled directory and named by task GUID.
    public static IEndpointRouteBuilder MapArtifactEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tasks/{id:guid}/download", async (
            Guid id,
            IDbContextFactory<AgentDbContext> dbFactory,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var task = await db.Tasks.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.Status })
                .SingleOrDefaultAsync(ct);
            if (task is null) return Results.NotFound(new { error = "Task not found." });
            if (task.Status != "completed")
                return Results.Conflict(new { error = "The task has not completed successfully." });

            var root = configuration["Worker:WorkspaceRoot"] ?? "/workspaces";
            var workspace = Path.Combine(root, id.ToString("N"));
            if (!Directory.Exists(workspace))
                return Results.NotFound(new { error = "Workspace not found or already cleaned up." });

            // Limit RAM consumption; reject symlinks and intentionally exclude .git.
            const long maxBytes = 50L * 1024 * 1024;
            const int maxFiles = 1500;
            long total = 0;
            var entries = new List<(string FullPath, string RelativePath)>();
            var dirs = new Stack<string>();
            dirs.Push(workspace);
            while (dirs.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = dirs.Pop();
                foreach (var child in Directory.EnumerateFileSystemEntries(dir))
                {
                    var name = Path.GetFileName(child);
                    var attrs = File.GetAttributes(child);
                    if (attrs.HasFlag(FileAttributes.ReparsePoint))
                        continue;
                    if (attrs.HasFlag(FileAttributes.Directory))
                    {
                        if (name is ".git" or "node_modules" or "bin" or "obj" or ".next")
                            continue;
                        dirs.Push(child);
                        continue;
                    }
                    if (name is ".env" or "appsettings.Local.json" || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var info = new FileInfo(child);
                    total += info.Length;
                    if (total > maxBytes || entries.Count >= maxFiles)
                        return Results.Problem("Workspace exceeds download limits (50 MiB, 1500 files).", statusCode: 413);
                    entries.Add((child, Path.GetRelativePath(workspace, child).Replace('\\', '/')));
                }
            }

            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (file, relative) in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(relative, CompressionLevel.Fastest);
                    await using var output = entry.Open();
                    await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                        bufferSize: 64 * 1024, useAsync: true);
                    await input.CopyToAsync(output, ct);
                }
            }
            return Results.File(buffer.ToArray(), "application/zip", $"developer-team-{id:N}.zip");
        });
        return app;
    }
}
