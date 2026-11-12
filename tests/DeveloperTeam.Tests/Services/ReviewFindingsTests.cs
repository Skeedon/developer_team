using DeveloperTeam.Services;
using Xunit;

namespace DeveloperTeam.Tests.Services;

public class ReviewFindingsTests
{
    [Theory]
    [InlineData("reviewed", "No actionable findings", false)]
    [InlineData("reviewed", " No actionable findings. ", false)]
    [InlineData("reviewed", "[P2] Fix data corruption — src/Game.cs:3", true)]
    [InlineData("reviewed", "", false)]
    [InlineData("failed", "[P1] Unconfirmed finding", false)]
    public void OnlyConfirmedActionableReviewNeedsRepair(string status, string report, bool expected) =>
        Assert.Equal(expected, AgentValidation.HasActionableFindings(status, report));
}
