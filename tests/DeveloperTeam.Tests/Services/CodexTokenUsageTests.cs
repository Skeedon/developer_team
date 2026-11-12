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
    public void MissingUsageFieldsDoNotCauseErrors()
    {
        var usage = CodexTokenUsage.FromNdjson("{\"type\":\"turn.completed\",\"usage\":{}}");
        Assert.Equal(default, usage);
    }
}
