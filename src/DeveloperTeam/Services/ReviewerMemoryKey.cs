namespace DeveloperTeam.Services;

public static class ReviewerMemoryKey
{
    public static string FromUrl(string url)
    {
        var uri = new Uri(url.Trim(), UriKind.Absolute);
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        return "github.com" + path.ToLowerInvariant();
    }
}
