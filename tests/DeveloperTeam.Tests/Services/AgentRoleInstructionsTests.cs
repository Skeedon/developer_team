using DeveloperTeam.Services;
using Xunit;

namespace DeveloperTeam.Tests.Services;

public class AgentRoleInstructionsTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("auto", true)]
    [InlineData("developer", true)]
    [InlineData("DESIGNER", true)]
    [InlineData("reviewer", false)]
    [InlineData("", false)]
    public void SupportsOnlyKnownRoles(string? role, bool expected) =>
        Assert.Equal(expected, AgentRoleInstructions.IsSupported(role));

    [Fact]
    public void DefaultsToAutoForNewTasksAndDeveloperForAmbiguousWork()
    {
        Assert.Equal("auto", AgentRoleInstructions.Normalize(null));
        Assert.Equal(new[] { "developer" }, AgentRoleInstructions.Plan(null, "Fix a bug"));
        Assert.Contains("senior software developer", AgentRoleInstructions.BuildPrompt(null, "Fix a bug"));
    }

    [Theory]
    [InlineData("Улучши CSS и адаптивность интерфейса", "designer")]
    [InlineData("Добавь API endpoint для статусов задач", "developer")]
    [InlineData("Исправь ошибку обработки очереди", "developer")]
    [InlineData("Сделай редизайн интерфейса и добавь API endpoint", "designer,developer")]
    public void AutoRouting_ChoosesNecessaryParticipants(string task, string expected)
    {
        Assert.Equal(expected, string.Join(",", AgentRoleInstructions.Plan("auto", task)));
    }

    [Fact]
    public void ExplicitRoleOverridesAutomaticRouting()
    {
        Assert.Equal(new[] { "designer" }, AgentRoleInstructions.Plan("designer", "Fix API"));
        Assert.Equal(new[] { "developer" }, AgentRoleInstructions.Plan("developer", "Improve UI CSS"));
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
