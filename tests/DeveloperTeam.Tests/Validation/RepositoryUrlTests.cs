using DeveloperTeam.Validation;
using Xunit;

namespace DeveloperTeam.Tests.Validation;

public class RepositoryUrlTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("https://github.com/owner/repo/")]
    [InlineData("https://github.com/owner/repo.git/")]
    [InlineData("https://github.com/a/b")]
    [InlineData("https://github.com/0/1")]
    [InlineData("https://github.com/Owner-123/Repo_123.name-with-dashes")]
    public void IsValid_AcceptsSupportedUrls(string url)
    {
        Assert.True(RepositoryUrl.IsValid(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("https://github.com")]
    [InlineData("https://github.com/")]
    [InlineData("https://github.com/owner")]
    [InlineData("https://github.com/owner/")]
    [InlineData("https://github.com//repo")]
    [InlineData("http://github.com/owner/repo")]
    [InlineData("ssh://git@github.com/owner/repo.git")]
    [InlineData("git@github.com:owner/repo.git")]
    [InlineData("github.com/owner/repo")]
    [InlineData("HTTPS://github.com/owner/repo")]
    [InlineData("https://GitHub.com/owner/repo")]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("https://www.github.com/owner/repo")]
    [InlineData("https://api.github.com/owner/repo")]
    [InlineData("https://github.com.evil.example/owner/repo")]
    [InlineData("https://github.com@evil.example/owner/repo")]
    [InlineData("https://user@github.com/owner/repo")]
    [InlineData("https://github.com:443/owner/repo")]
    [InlineData("https://github.com/-owner/repo")]
    [InlineData("https://github.com/own_er/repo")]
    [InlineData("https://github.com/own.er/repo")]
    [InlineData("https://github.com/владелец/repo")]
    [InlineData("https://github.com/owner/репозиторий")]
    [InlineData("https://github.com/own er/repo")]
    [InlineData("https://github.com/owner/re po")]
    [InlineData("https://github.com/owner/repo%20name")]
    [InlineData("https://github.com/owner/repo?tab=readme")]
    [InlineData("https://github.com/owner/repo#readme")]
    [InlineData("https://github.com/owner/repo/issues")]
    [InlineData("https://github.com/owner/repo//")]
    [InlineData(" https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo ")]
    [InlineData("https://github.com/owner/repo\t")]
    [InlineData("https://github.com/owner/repo\r\n")]
    [InlineData("https://github.com/owner/re\npo")]
    public void IsValid_RejectsUnsupportedUrls(string? url)
    {
        Assert.False(RepositoryUrl.IsValid(url));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(38, true)]
    [InlineData(39, true)]
    [InlineData(40, false)]
    public void IsValid_EnforcesOwnerLengthLimit(int ownerLength, bool expected)
    {
        var url = $"https://github.com/{new string('a', ownerLength)}/repo";

        Assert.Equal(expected, RepositoryUrl.IsValid(url));
    }

    [Theory]
    [InlineData(249, "", true)]
    [InlineData(250, "", true)]
    [InlineData(251, "", false)]
    [InlineData(250, ".git", true)]
    [InlineData(251, ".git", false)]
    [InlineData(250, "/", true)]
    [InlineData(251, "/", false)]
    [InlineData(250, ".git/", true)]
    [InlineData(251, ".git/", false)]
    public void IsValid_EnforcesTotalLengthLimit(int length, string suffix, bool expected)
    {
        const string prefix = "https://github.com/owner/";
        var url = prefix + new string('a', length - prefix.Length - suffix.Length) + suffix;

        Assert.Equal(length, url.Length);
        Assert.Equal(expected, RepositoryUrl.IsValid(url));
    }
}
