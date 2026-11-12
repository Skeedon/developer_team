using System.Text.Json;

namespace DeveloperTeam.Services;

// Codex --json emits a turn.completed event with usage per invocation.
public readonly record struct CodexTokenUsage(long InputTokens, long CachedInputTokens, long OutputTokens)
{
    public static CodexTokenUsage FromNdjson(string output)
    {
        long input = 0, cached = 0, outputCount = 0;
        foreach (var line in output.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "turn.completed" ||
                    !root.TryGetProperty("usage", out var usage)) continue;
                input += Number(usage, "input_tokens");
                cached += Number(usage, "cached_input_tokens");
                outputCount += Number(usage, "output_tokens");
            }
            catch (JsonException) { }
        }
        return new CodexTokenUsage(input, cached, outputCount);
    }

    private static long Number(JsonElement value, string property) =>
        value.TryGetProperty(property, out var number) && number.TryGetInt64(out var result)
            ? Math.Max(0, result) : 0;

    public static CodexTokenUsage operator +(CodexTokenUsage a, CodexTokenUsage b) =>
        new(a.InputTokens + b.InputTokens, a.CachedInputTokens + b.CachedInputTokens,
            a.OutputTokens + b.OutputTokens);
}
