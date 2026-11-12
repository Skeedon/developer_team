using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeveloperTeam.Services;

// The manifest is authored by an agent and therefore always treated as untrusted input.
public static class ImageAssetManifest
{
    public sealed record Asset(string Path, string Prompt, string[] Criteria, string Size, bool Transparent);
    private static readonly Regex AllowedPath = new(
        @"^(?:public/assets|wwwroot/assets|assets)/[a-zA-Z0-9][a-zA-Z0-9_./-]{0,155}\.png$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<Asset> Parse(string json, int maxAssets = 1)
    {
        if (json.Length > 16000) throw new InvalidOperationException("Image manifest exceeds 16 KB.");
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("assets", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Image manifest must contain an assets array.");
        if (arr.GetArrayLength() > maxAssets)
            throw new InvalidOperationException("Too many image assets requested for this task.");
        var assets = new List<Asset>();
        foreach (var item in arr.EnumerateArray())
        {
            var path = item.GetProperty("path").GetString() ?? "";
            var prompt = item.GetProperty("prompt").GetString() ?? "";
            var size = item.TryGetProperty("size", out var s) ? s.GetString() ?? "" : "1024x1024";
            var transparent = item.TryGetProperty("transparent", out var tr) && tr.ValueKind == JsonValueKind.True;
            if (!AllowedPath.IsMatch(path) || path.Contains("..", StringComparison.Ordinal) ||
                path.Split('/').Any(x => x is "." or "..") ||
                path.Contains("//", StringComparison.Ordinal) ||
                !new[] { "1024x1024", "1536x1024", "1024x1536" }.Contains(size) ||
                prompt.Length is < 20 or > 3000)
                throw new InvalidOperationException("Invalid image path, size, or prompt.");
            var criteria = item.TryGetProperty("criteria", out var cr) && cr.ValueKind == JsonValueKind.Array
                ? cr.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                : [];
            if (criteria.Length is < 1 or > 6 || criteria.Any(x => x.Length is < 3 or > 250))
                throw new InvalidOperationException("Image criteria must contain 1–6 short requirements.");
            if (assets.Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Duplicate image output path.");
            assets.Add(new Asset(path, prompt, criteria, size, transparent));
        }
        return assets;
    }

    public static string ResolveSafeOutput(string workspace, string relative)
    {
        // Don't let a symlink in the agent's repository redirect image writes outside workspace.
        var full = Path.GetFullPath(Path.Combine(workspace, relative));
        var root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException("Asset would escape task workspace.");
        var cursor = new DirectoryInfo(Path.GetDirectoryName(full)!);
        while (cursor.FullName.StartsWith(root, StringComparison.Ordinal))
        {
            if (cursor.LinkTarget is not null) throw new InvalidOperationException("Symbolic-link output paths are forbidden.");
            cursor = cursor.Parent!;
        }
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Symbolic-link asset files are forbidden.");
        return full;
    }
}
