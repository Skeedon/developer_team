using DeveloperTeam.Services;
using Xunit;

namespace DeveloperTeam.Tests.Services;

public class ReviewerMemoryTests
{
    [Fact]
    public void RepositoryKey_NormalizesUrlVariants()
    {
        Assert.Equal(ReviewerMemoryKey.FromUrl("https://github.com/Skeedon/developer_team"),
            ReviewerMemoryKey.FromUrl("https://github.com/skeedon/developer_team.git/"));
    }

    [Fact]
    public void ParseReviewResponse_SeparatesFindingsAndMemory()
    {
        var json = """{"findings":"P2: check validation","updatedMemory":"ASP.NET Core APIs, EF Core, worker jobs."}""";
        var result = AgentValidation.ParseReviewResponse(json);
        Assert.Equal("P2: check validation", result.Findings);
        Assert.Equal("ASP.NET Core APIs, EF Core, worker jobs.", result.UpdatedMemory);
    }

    [Fact]
    public void ParseReviewResponse_DoesNotStoreSecretLikeMemory()
    {
        var json = """{"findings":"No actionable findings","updatedMemory":"github_pat_sensitive"}""";
        Assert.Null(AgentValidation.ParseReviewResponse(json).UpdatedMemory);
    }

    [Fact]
    public void ParseReviewResponse_LegacyResponsePreservesFindings()
    {
        var result = AgentValidation.ParseReviewResponse("No actionable findings");
        Assert.Equal("No actionable findings", result.Findings);
        Assert.Null(result.UpdatedMemory);
    }
}
