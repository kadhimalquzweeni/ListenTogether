using ListenTogether.App.Settings;

namespace ListenTogether.Tests.Settings;

public sealed class InviteDetailsParserTests
{
    [Fact]
    public void TryParse_ParsesHostCopiedInviteDetails()
    {
        const string text = "ListenTogether invite\nServer: https://203.0.113.42\nInvite code: AB12CD34";

        var parsed = InviteDetailsParser.TryParse(text, out var details);

        Assert.True(parsed);
        Assert.Equal(new InviteDetails("https://203.0.113.42", "AB12CD34"), details);
    }

    [Fact]
    public void TryParse_NormalizesServerUrlAndInviteCode()
    {
        const string text = "Server: https://listen.example.com/\nInvite code: ab12cd34";

        Assert.True(InviteDetailsParser.TryParse(text, out var details));

        Assert.Equal("https://listen.example.com", details!.ServerUrl);
        Assert.Equal("AB12CD34", details.InviteCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Server: https://example.com")]
    [InlineData("Invite code: AB12CD34")]
    [InlineData("Server: file:///tmp/server\nInvite code: AB12CD34")]
    [InlineData("Server: https://user:password@example.com\nInvite code: AB12CD34")]
    [InlineData("Server: https://example.com/path\nInvite code: AB12CD34")]
    [InlineData("Server: https://example.com?token=secret\nInvite code: AB12CD34")]
    [InlineData("Server: https://example.com\nInvite code: TOO-SHORT")]
    [InlineData("Server: https://example.com\nInvite code: AB12CD3!")]
    public void TryParse_RejectsInvalidOrIncompleteDetails(string text)
    {
        Assert.False(InviteDetailsParser.TryParse(text, out var details));
        Assert.Null(details);
    }

    [Fact]
    public void TryParse_RejectsMultipleServerLines()
    {
        const string text = "Server: https://one.example\nServer: https://two.example\nInvite code: AB12CD34";

        Assert.False(InviteDetailsParser.TryParse(text, out _));
    }

    [Fact]
    public void TryParse_RejectsMultipleInviteCodes()
    {
        const string text = "Server: https://one.example\nInvite code: AB12CD34\nInvite code: EF56GH78";

        Assert.False(InviteDetailsParser.TryParse(text, out _));
    }
}
