using Qodalis.Cli.Plugin.Users.Services;
using Xunit;

namespace Qodalis.Cli.Tests;

/// <summary>The email rule is shared with web-cli and the other servers (same cases).</summary>
public class EmailValidationTests
{
    [Theory]
    [InlineData("a@x.io")]
    [InlineData("root@localhost")]
    [InlineData("first.last+tag@sub.example.co.uk")]
    [InlineData("o'neil@x.com")]
    [InlineData("x@xn--80ak6aa92e.com")]
    [InlineData("a_b-c@my-host.org")]
    [InlineData("ALICE@EXAMPLE.COM")]
    public void Accepts_valid_email(string email) => Assert.Equal(email, UsersValidation.Email(email));

    [Theory]
    [InlineData("Nicolae L")]
    [InlineData("nicolae")]
    [InlineData("a@b")]
    [InlineData("a@b.c")]
    [InlineData("a..b@x.io")]
    [InlineData(".a@x.io")]
    [InlineData("a.@x.io")]
    [InlineData("a@-x.io")]
    [InlineData("a@x-.io")]
    [InlineData("a@x_y.io")]
    [InlineData("a b@x.io")]
    [InlineData("a@x.io.")]
    [InlineData("a@@x.io")]
    [InlineData("a@x.123")]
    [InlineData("@x.io")]
    [InlineData("a@.x.io")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@x.io")]
    [InlineData("a@localhost.")]
    [InlineData("a@x..io")]
    public void Rejects_invalid_email(string email) => Assert.ThrowsAny<Exception>(() => UsersValidation.Email(email));
}
