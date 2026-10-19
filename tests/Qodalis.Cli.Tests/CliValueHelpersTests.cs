using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Helpers;
using Qodalis.Cli.Plugin.Weather;
using Qodalis.Cli.Server.Processors;

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

public class CliPositionalsTests
{
    public static TheoryData<string?, string[]> SplitCases => new()
    {
        { "\"a b\" c", ["a b", "c"] },
        { "'a b'", ["a b"] },
        { @"my\ file", ["my file"] },
        { @"""say \""hi\"" \\ ok""", [@"say ""hi"" \ ok"] },
        { @"""keep \n""", [@"keep \n"] },
        { @"'no \' escape'", [@"no \", "escape'"] },
        { "a   b", ["a", "b"] },
        { "\"\"", [""] },
        { "\"a", ["\"a"] },
        { "it's fine", ["it's", "fine"] },
        { "", [] },
        { null, [] },
    };

    [Theory]
    [MemberData(nameof(SplitCases))]
    public void SplitShellWords_FollowsShellRules(string? text, string[] expected)
    {
        Assert.Equal(expected, CliValueHelpers.SplitShellWords(text));
    }

    [Fact]
    public void GetPositionals_PrefersSentPositionals()
    {
        var command = new CliProcessCommand { Value = "\"a b\" c", Positionals = ["x y"] };
        Assert.Equal(["x y"], CliValueHelpers.GetPositionals(command));
        Assert.Equal("x y", CliValueHelpers.GetPositionalText(command));
    }

    [Fact]
    public void GetPositionals_FallsBackToSplittingValue()
    {
        var command = new CliProcessCommand { Value = "\"a b\" c" };
        Assert.Equal(["a b", "c"], CliValueHelpers.GetPositionals(command));
        Assert.Equal("a b c", CliValueHelpers.GetPositionalText(command));
    }

    [Fact]
    public void Positionals_DeserializesFromCamelCaseJson()
    {
        var command = JsonSerializer.Deserialize<CliProcessCommand>(
            """{"command":"echo","value":"\"a b\"","positionals":["a b"]}""")!;
        Assert.Equal(["a b"], command.Positionals);
        var legacy = JsonSerializer.Deserialize<CliProcessCommand>("""{"command":"echo","value":"x"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(legacy.Positionals);
        Assert.Contains("\"positionals\"", JsonSerializer.Serialize(command));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "'say \"hi\"'")]
    [InlineData("it's \"x\"", "\"it's \\\"x\\\"\"")]
    [InlineData(@"C:\dir", @"""C:\\dir""")]
    [InlineData("k=v", "\"k=v\"")]
    [InlineData("ünï<>&", "ünï<>&")]
    public void QuoteShellWord_UsesTheLightestQuoting(string value, string expected)
    {
        Assert.Equal(expected, CliValueHelpers.QuoteShellWord(value));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("a b")]
    [InlineData("")]
    [InlineData("say \"hi\"")]
    [InlineData("it's \"x\"")]
    [InlineData(@"C:\dir\\x")]
    [InlineData(@"\""'\\ '")]
    public void QuoteShellWord_RoundTripsThroughTheSplitter(string value)
    {
        Assert.Equal([value], CliValueHelpers.SplitShellWords(CliValueHelpers.QuoteShellWord(value)));
    }

    private static CliProcessCommand Cmd(string value, List<string>? positionals = null) =>
        new() { Command = "x", Value = value, Positionals = positionals };

    [Fact]
    public async Task Base64Encode_UsesUnquotedText()
    {
        var p = new CliBase64EncodeProcessor();
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("a b"));
        Assert.Equal(expected, await p.HandleAsync(Cmd("\"a b\"")));
        Assert.Equal(expected, await p.HandleAsync(Cmd("\"a b\"", ["a b"])));
        Assert.Equal("a b", await new CliBase64DecodeProcessor().HandleAsync(Cmd($"'{expected}'")));
    }

    [Fact]
    public async Task Hash_UsesUnquotedText()
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("a b"))).ToLower();
        Assert.Equal($"sha256: {hex}", await new CliHashCommandProcessor().HandleAsync(Cmd("\"a b\"")));
    }

    [Fact]
    public async Task Echo_UsesPositionals()
    {
        var p = new Qodalis.Cli.Server.Processors.CliEchoCommandProcessor();
        Assert.Equal("a b c", await p.HandleAsync(Cmd("\"a b\" c")));
        Assert.Equal("a  b", await p.HandleAsync(Cmd("ignored", ["a  b"])));
    }

    [Fact]
    public void Weather_LocationIsUnquoted()
    {
        Assert.Equal("New York", CliWeatherCommandProcessor.GetLocation(Cmd("\"New York\"")));
        Assert.Equal("London", CliWeatherCommandProcessor.GetLocation(Cmd("")));
    }
}
