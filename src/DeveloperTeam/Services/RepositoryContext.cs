namespace DeveloperTeam.Services;

/// <summary>
/// A tiny local map of tracked/untracked source files. No model call and no file contents.
/// </summary>
public static class RepositoryContext
{
    private static readonly string[] IgnoredSegments =
        [".git", "node_modules", "bin", "obj", "dist", "build", "coverage", ".next", ".venv"];

    public static string FromGitFileList(string? output, int maxFiles = 100, int maxChars = 3500)
    {
        if (string.IsNullOrWhiteSpace(output) || maxFiles <= 0 || maxChars <= 0)
            return "(No source-file map available.)";

        var paths = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => path.Length <= 240 && !path.Contains('\\') &&
                !path.StartsWith('/') && !path.StartsWith("..") &&
                !path.Split('/').Any(segment => IgnoredSegments.Contains(segment, StringComparer.OrdinalIgnoreCase)) &&
                !path.Split('/').Last().StartsWith(".env", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("secret", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("credential", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(maxFiles)
            .ToArray();
        var result = new System.Text.StringBuilder();
        foreach (var path in paths)
        {
            if (result.Length + path.Length + 2 > maxChars) break;
            result.Append("- ").AppendLine(path);
        }
        return result.Length == 0 ? "(No source-file map available.)" : result.ToString();
    }

    public static string ClipHandoff(string? handoff, int maxChars = 2500) =>
        string.IsNullOrWhiteSpace(handoff) ? "" :
        handoff[..Math.Min(handoff.Length, Math.Max(0, maxChars))];
}
