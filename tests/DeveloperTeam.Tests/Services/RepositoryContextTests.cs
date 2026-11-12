using DeveloperTeam.Services;
using Xunit;

namespace DeveloperTeam.Tests.Services;

public class RepositoryContextTests
{
    [Fact]
    public void FileMap_IsBoundedAndOmitsDependenciesAndSecrets()
    {
        const string files = "src/Program.cs\nsrc/UI/App.tsx\nnode_modules/a.js\n.env\nsrc/secrets.json\nbin/Debug/x.dll\n";
        var map = RepositoryContext.FromGitFileList(files);
        Assert.Contains("src/Program.cs", map);
        Assert.Contains("src/UI/App.tsx", map);
        Assert.DoesNotContain("node_modules", map);
        Assert.DoesNotContain(".env", map);
        Assert.DoesNotContain("secrets", map);
        Assert.DoesNotContain("bin/Debug", map);
    }

    [Fact]
    public void FileMap_BoundsLengthAndFileCount()
    {
        var files = string.Join('\n', Enumerable.Range(0, 500).Select(i => $"src/File{i:D3}.cs"));
        var map = RepositoryContext.FromGitFileList(files, maxFiles: 8, maxChars: 120);
        Assert.True(map.Length <= 120);
        Assert.True(map.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length <= 8);
    }

    [Fact]
    public void Handoff_IsOptionalAndBounded()
    {
        Assert.Equal("", RepositoryContext.ClipHandoff(null));
        Assert.Equal("abc", RepositoryContext.ClipHandoff("abcdef", 3));
    }
}
