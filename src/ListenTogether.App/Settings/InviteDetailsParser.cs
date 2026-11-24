using System.Text.RegularExpressions;

namespace ListenTogether.App.Settings;

/// <summary>Parses the plain-text invite format copied by the host.</summary>
public static partial class InviteDetailsParser
{
    [GeneratedRegex(@"(?im)^\s*Server\s*:\s*(?<url>\S+)\s*$")]
    private static partial Regex ServerLineRegex();

    [GeneratedRegex(@"(?im)^\s*Invite\s+code\s*:\s*(?<code>[A-Z0-9]{8})\s*$")]
    private static partial Regex InviteCodeLineRegex();

    public static bool TryParse(string? text, out InviteDetails? details)
    {
        details = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var serverMatches = ServerLineRegex().Matches(text);
        var codeMatches = InviteCodeLineRegex().Matches(text);
        if (serverMatches.Count != 1 || codeMatches.Count != 1)
            return false;

        var serverMatch = serverMatches[0];
        var codeMatch = codeMatches[0];
        if (!serverMatch.Success || !codeMatch.Success ||
            !Uri.TryCreate(serverMatch.Groups["url"].Value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrWhiteSpace(uri.UserInfo) ||
            !string.IsNullOrWhiteSpace(uri.Query) ||
            !string.IsNullOrWhiteSpace(uri.Fragment) ||
            uri.AbsolutePath != "/")
        {
            return false;
        }

        details = new InviteDetails(uri.ToString().TrimEnd('/'),
            codeMatch.Groups["code"].Value.ToUpperInvariant());
        return true;
    }
}

public sealed record InviteDetails(string ServerUrl, string InviteCode);
