using System.Text.RegularExpressions;

namespace DeveloperTeam.Validation;

public static class RepositoryUrl
{
    private static readonly Regex Pattern = new(
        @"^https://github\.com/[a-zA-Z0-9][a-zA-Z0-9-]{0,38}/[a-zA-Z0-9_.-]+(?:\.git)?/?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValid(string? url) => url is not null && url.Length <= 250 && Pattern.IsMatch(url);
}
