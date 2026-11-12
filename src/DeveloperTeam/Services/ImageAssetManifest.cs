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

    public static IReadOnlyList<Asset> Parse(string json, int maxAssets = 6)
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
                path.Split('/').Any(x => x is "." or ".." || x.StartsWith('.') ||
                    x.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                    x.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                    x.Contains("credential", StringComparison.OrdinalIgnoreCase)) ||
                path.Contains("//", StringComparison.Ordinal) ||
                !new[] { "1024x1024", "1536x1024", "1024x1536" }.Contains(size) ||
                prompt.Length is < 20 or > 3000)
                throw new InvalidOperationException("Invalid image path, size, or prompt.");
            // Designer-authored requirements are guidance, not a fixed-size protocol.
            // The whole manifest is already limited to 16 KB above. Rejecting >6
            // requirements or a 251-character sentence blocks good tasks before
            // generation and provides no safety benefit.
            var criteria = ReadCriteria(item, prompt);
            // A generative image model cannot reliably solve multi-car sprite atlas
            // geometry and exact pixel placement. Avoid charging for impossible work.
            // Each car must be a separate PNG; layout is the frontend's responsibility.
            if (LooksLikeCar(path, prompt, criteria) && RequestsCarAtlas(prompt, criteria))
                throw new InvalidOperationException(
                    "A single car image cannot contain a multi-car sprite sheet, cell atlas, " +
                    "or exact pixel coordinates. Declare ONE PNG asset per car and arrange " +
                    "the sprites using CSS/Canvas after generation. No image API call was made.");
            if (assets.Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Duplicate image output path.");
            assets.Add(new Asset(path, prompt, criteria, size, transparent));
        }
        return assets;
    }


    private static string[] ReadCriteria(JsonElement item, string prompt)
    {
        // Accept both a single textual criterion and an array. Trim empty entries,
        // deduplicate repetitions, and keep all distinct substantive instructions.
        // The outer 16 KB manifest cap bounds the amount sent to the vision model.
        var result = new List<string>();
        if (item.TryGetProperty("criteria", out var value))
        {
            if (value.ValueKind == JsonValueKind.String)
                result.Add(value.GetString() ?? "");
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in value.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.String)
                        result.Add(entry.GetString() ?? "");
                    else if (entry.ValueKind == JsonValueKind.Object)
                    {
                        // Tolerate a common LLM variation:
                        // "criteria": [{"description": "Strict side view"}].
                        foreach (var key in new[] { "description", "text", "requirement" })
                        {
                            if (entry.TryGetProperty(key, out var description) &&
                                description.ValueKind == JsonValueKind.String)
                            {
                                result.Add(description.GetString() ?? "");
                                break;
                            }
                        }
                    }
                }
            }
        }

        var normalized = result.Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // No explicit requirements? Use broad, useful composition checks rather
        // than rejecting a task. The full detailed prompt remains available.
        return normalized.Length == 0
            ? ["The primary subject matches the image prompt.",
               "The main composition, perspective and visible elements match the image prompt."]
            : normalized;
    }

    private static bool LooksLikeCar(string path, string prompt, IEnumerable<string> criteria)
    {
        var text = path + " " + prompt + " " + string.Join(" ", criteria);
        return Regex.IsMatch(text, @"\b(cars?|vehicles?|automobiles?|автомобил\w*|машин\w*)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool RequestsCarAtlas(string prompt, IEnumerable<string> criteria)
    {
        var text = prompt + " " + string.Join(" ", criteria);
        return Regex.IsMatch(text,
            @"\b(sprite[\s_-]?sheet|sprite[\s_-]?atlas|atlas|grid\s+of\s+(?:cars|vehicles)|" +
            @"(?:multiple|several|all|three|four|five|six)\s+(?:cars|vehicles)|" +
            @"cars?\s+in\s+(?:separate\s+)?cells|(?:каждой|каждую)\s+машин\w*\s+в\s+ячейк\w*|" +
            @"атлас\w*|спрайт[\s-]?лист\w*|сетк\w*\s+машин\w*|" +
            @"\b[xy]\s*=\s*\d{1,4})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
        if (new FileInfo(full).LinkTarget is not null ||
            (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidOperationException("Symbolic-link asset files are forbidden.");
        return full;
    }
}
