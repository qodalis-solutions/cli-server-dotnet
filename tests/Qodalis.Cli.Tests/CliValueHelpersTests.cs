using Qodalis.Cli.Abstractions.Helpers;

namespace Qodalis.Cli.Tests;

public class CliValueHelpersTests
{
    [Theory]
    [InlineData("\"hello world\"", "hello world")]
    [InlineData("'a b'", "a b")]
    [InlineData("\"a --b\"", "a --b")]
    [InlineData("hello world", "hello world")]
    [InlineData("\"a", "\"a")]
    [InlineData("\"a'", "\"a'")]
    [InlineData("-", "-")]
    [InlineData("\"a\" \"b\"", "a b")]
    [InlineData("\"a   b\"  c", "a   b c")]
    [InlineData("it's fine", "it's fine")]
    [InlineData("\"", "\"")]
    [InlineData("\"\"", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void JoinShellWords_MatchesBrowserEcho(string? value, string expected)
    {
        Assert.Equal(expected, CliValueHelpers.JoinShellWords(value));
    }
}
