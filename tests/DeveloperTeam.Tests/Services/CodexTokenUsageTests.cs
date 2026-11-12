using DeveloperTeam.Services;
using Xunit;

namespace DeveloperTeam.Tests.Services;

public class CodexTokenUsageTests
{
    [Fact]
    public void ParsesCompletedTurnsAndIgnoresUnrelatedEvents()
    {
        var input = """
            {"type":"thread.started"}
            {"type":"turn.completed","usage":{"input_tokens":150,"cached_input_tokens":80,"output_tokens":50}}
            {"type":"item.completed","item":{"type":"agent_message","text":"Review."}}
            {"type":"turn.completed","usage":{"input_tokens":10,"cached_input_tokens":0,"output_tokens":3}}
            """;
        var usage = CodexTokenUsage.FromNdjson(input);
        Assert.Equal(160, usage.InputTokens);
        Assert.Equal(80, usage.CachedInputTokens);
        Assert.Equal(53, usage.OutputTokens);
    }

    [Fact]
    public void LongProcessOutput_StillKeepsFinalUsageEvent()
    {
        var filler = string.Join("\n", Enumerable.Repeat(
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\",\"aggregated_output\":\"lots of logs\"}}", 5000));
        var usageEvent = "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":12345,\"cached_input_tokens\":10000,\"output_tokens\":321}}";
        var output = filler + "\n" + usageEvent + "\n";
        Assert.True(output.Length > 160_000);

        var bounded = CodexTokenUsage.PreserveLifecycleTail(output, 160_000);
        Assert.True(bounded.Length <= 160_000);
        var usage = CodexTokenUsage.FromNdjson(bounded);
        Assert.True(usage.HasMetrics);
        Assert.Equal(12345, usage.InputTokens);
        Assert.Equal(10000, usage.CachedInputTokens);
        Assert.Equal(321, usage.OutputTokens);
    }

    [Fact]
    public void ShortOutput_IsUnchanged()
    {
        const string json = "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1,\"cached_input_tokens\":0,\"output_tokens\":1}}";
        Assert.Equal(json, CodexTokenUsage.PreserveLifecycleTail(json, 160_000));
    }

    [Fact]
    public void MissingUsageFieldsDoNotCauseErrors()
    {
        var usage = CodexTokenUsage.FromNdjson("{\"type\":\"turn.completed\",\"usage\":{}}");
        Assert.Equal(default, usage);
    }
}
