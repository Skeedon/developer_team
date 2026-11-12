using DeveloperTeam.Services;
using Xunit;

namespace DeveloperTeam.Tests.Services;

public class AgentRoleInstructionsTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("developer", true)]
    [InlineData("DESIGNER", true)]
    [InlineData("reviewer", false)]
    [InlineData("", false)]
    public void SupportsOnlyKnownRoles(string? role, bool expected) =>
        Assert.Equal(expected, AgentRoleInstructions.IsSupported(role));

    [Fact]
    public void DefaultsToDeveloperForLegacyTasks()
    {
        Assert.Equal("developer", AgentRoleInstructions.Normalize(null));
        Assert.Contains("senior software developer", AgentRoleInstructions.BuildPrompt(null, "Fix a bug"));
    }

    [Fact]
    public void DesignerEditsUiWithoutGeneratingImages()
    {
        var prompt = AgentRoleInstructions.BuildPrompt("designer", "Make the garage responsive");
        Assert.Contains("UI/UX designer", prompt);
        Assert.Contains("Image generation is NOT connected", prompt);
        Assert.Contains("Make the garage responsive", prompt);
        Assert.Contains("do not push or publish", prompt);
    }
}
