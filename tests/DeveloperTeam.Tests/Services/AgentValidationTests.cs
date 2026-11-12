using DeveloperTeam.Services;
using Xunit;

namespace DeveloperTeam.Tests.Services;

public class AgentValidationTests
{
    [Fact]
    public void ExtractReviewerText_ReturnsFinalAgentMessageOnly()
    {
        var transcript = """
            {"type":"thread.started","thread_id":"abc"}
            {"type":"item.completed","item":{"type":"agent_message","text":"Checking changes."}}
            {"type":"item.completed","item":{"type":"command_execution","aggregated_output":"sensitive command output"}}
            {"type":"item.completed","item":{"type":"agent_message","text":"No actionable findings"}}
            {"type":"turn.completed"}
            """;

        Assert.Equal("No actionable findings", AgentValidation.ExtractReviewerText(transcript));
    }

    [Fact]
    public void ExtractReviewerText_IgnoresNonJsonAndMissingMessage()
    {
        const string transcript = """
            WARNING: setup failed
            {"type":"item.completed","item":{"type":"command_execution","aggregated_output":"noise"}}
            {"type":"turn.completed"}
            """;

        Assert.Equal("", AgentValidation.ExtractReviewerText(transcript));
    }

    [Fact]
    public void ExtractReviewerText_PreservesFindingFormatting()
    {
        const string transcript = """
            {"type":"item.completed","item":{"type":"agent_message","text":"High: src/file.cs:12\\nFix validation."}}
            """;

        Assert.Equal("High: src/file.cs:12\nFix validation.", AgentValidation.ExtractReviewerText(transcript));
    }
}
