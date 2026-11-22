using DocAgent.Infrastructure.Email;

namespace DocAgent.UnitTests;

/// <summary>Plan A5 / OI-9: the emailed confirmation link opens the UI page when Email:ConfirmationUrlBase is set.</summary>
public sealed class ConfirmationLinkTests
{
    // Identity hands the sender an HTML-encoded absolute link to its own endpoint.
    private const string IdentityLink =
        "https://api.example/api/v1/auth/confirmEmail?userId=3f2b1c9e-0000-4000-8000-000000000001&amp;code=Q0ZESjhA-_x";

    [Fact]
    public void Without_a_base_the_api_link_is_kept_and_decoded()
    {
        Assert.Equal(
            "https://api.example/api/v1/auth/confirmEmail?userId=3f2b1c9e-0000-4000-8000-000000000001&code=Q0ZESjhA-_x",
            ConfirmationLink.Rewrite(IdentityLink, null));
        Assert.Equal(ConfirmationLink.Rewrite(IdentityLink, null), ConfirmationLink.Rewrite(IdentityLink, " "));
    }

    [Fact]
    public void With_a_base_the_link_points_at_the_ui_and_keeps_userId_and_code()
    {
        var link = ConfirmationLink.Rewrite(IdentityLink, "https://ui.example/confirm");
        Assert.Equal("https://ui.example/confirm?userId=3f2b1c9e-0000-4000-8000-000000000001&code=Q0ZESjhA-_x", link);
    }

    [Fact]
    public void Existing_query_on_the_base_is_preserved_and_special_characters_are_escaped()
    {
        var withCode = "https://api.example/api/v1/auth/confirmEmail?userId=u1&code=a%2Bb%20c";
        var link = ConfirmationLink.Rewrite(withCode, "https://ui.example/en/confirm?src=mail");
        Assert.Equal("https://ui.example/en/confirm?src=mail&userId=u1&code=a%2Bb%20c", link);
    }

    [Fact]
    public void Changed_email_is_carried_along()
    {
        var link = ConfirmationLink.Rewrite(
            "https://api.example/api/v1/auth/confirmEmail?userId=u1&code=c&changedEmail=new%40example.com", "https://ui.example/confirm");
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(link).Query);
        Assert.Equal("new@example.com", query["changedEmail"].ToString());
    }

    [Theory]
    [InlineData("https://api.example/api/v1/auth/confirmEmail?userId=u1")]          // no code
    [InlineData("https://api.example/api/v1/auth/confirmEmail?code=c")]             // no user
    [InlineData("https://api.example/api/v1/auth/confirmEmail?userId=a&userId=b&code=c")] // ambiguous
    [InlineData("not a url")]
    public void Unexpected_links_are_left_alone(string identityLink)
    {
        Assert.Equal(identityLink, ConfirmationLink.Rewrite(identityLink, "https://ui.example/confirm"));
    }

    [Theory]
    [InlineData("https://ui.example/confirm", true)]
    [InlineData("http://localhost:3000/confirm", true)]
    [InlineData("/confirm", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ftp://ui.example/confirm", false)]
    [InlineData("https://ui.example/confirm#frag", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Base_must_be_an_absolute_http_url(string? urlBase, bool valid)
    {
        Assert.Equal(valid, ConfirmationLink.IsValidBase(urlBase));
    }

    [Fact]
    public void An_invalid_base_falls_back_to_the_api_link()
    {
        Assert.Equal(
            ConfirmationLink.Rewrite(IdentityLink, null),
            ConfirmationLink.Rewrite(IdentityLink, "javascript:alert(1)"));
    }
}
