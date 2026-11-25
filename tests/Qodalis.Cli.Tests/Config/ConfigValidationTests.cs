using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions.Config;

namespace Qodalis.Cli.Tests.Config;

/// <summary>Config providers: value validation (every type and rule), terminal input parsing, durations, type inference, permission matching and contexts.</summary>
public class ConfigValidationTests
{
    private static string? V(string type, JsonNode? value, CliConfigValidation? validation = null, JsonNode? defaultValue = null) =>
        CliConfigValues.Validate(type, validation, defaultValue, value);

    private static JsonNode? J(string json) => JsonNode.Parse(json);

    [Fact]
    public void String()
    {
        Assert.Null(V("string", "x"));
        Assert.Null(V("string", ""));
        Assert.Equal("must be a string", V("string", 1));
        Assert.Equal("must be a string", V("string", J("[\"a\"]")));
    }

    [Fact]
    public void Secret()
    {
        Assert.Null(V("secret", "s3cr3t"));
        Assert.Equal("must be a string", V("secret", 42));
    }

    [Fact]
    public void Number()
    {
        Assert.Null(V("number", 0));
        Assert.Null(V("number", -1.5));
        Assert.Equal("must be a number", V("number", "1"));
        Assert.Equal("must be a number", V("number", true));
    }

    [Fact]
    public void Boolean()
    {
        Assert.Null(V("boolean", true));
        Assert.Null(V("boolean", false));
        Assert.Equal("must be true or false", V("boolean", "true"));
        Assert.Equal("must be true or false", V("boolean", 1));
    }

    [Fact]
    public void StringList()
    {
        Assert.Null(V("string[]", J("[]")));
        Assert.Null(V("string[]", J("[\"a\",\"b\"]")));
        Assert.Equal("must be a list of strings", V("string[]", "a"));
        Assert.Equal("must be a list of strings", V("string[]", J("[\"a\",1]")));
    }

    [Fact]
    public void Enum()
    {
        var rules = new CliConfigValidation { Options = ["a", "b"] };
        Assert.Null(V("enum", "a", rules));
        Assert.Equal("must be one of: a, b", V("enum", "c", rules));
        Assert.Equal("must be a string", V("enum", 1, rules));
        Assert.Equal("must be one of: ", V("enum", "a"));
    }

    [Fact]
    public void Json_AnyJsonValue()
    {
        foreach (var value in new[] { "{\"a\":[1,{\"b\":null}]}", "[1,\"x\"]", "\"text\"", "3", "true" })
            Assert.Null(V("json", J(value)));
    }

    [Fact]
    public void Duration_IntegerAndUnit()
    {
        foreach (var value in new[] { "500ms", "30s", "15m", "12h", "7d", "0s" })
            Assert.Null(V("duration", value));
        foreach (var value in new JsonNode?[] { "1.5h", "10", "h", "5w", "-1s", "1h30m", 60_000 })
            Assert.Equal("must be a duration such as 500ms, 30s, 15m, 12h or 7d", V("duration", value));
    }

    [Fact]
    public void Url_AbsoluteWithAScheme()
    {
        Assert.Null(V("url", "https://example.com/path?q=1"));
        Assert.Null(V("url", "postgres://db:5432/x"));
        Assert.Null(V("url", ""));
        Assert.Equal("must be an absolute URL", V("url", "/relative"));
        Assert.Equal("must be an absolute URL", V("url", "mailto:a@b.c"));
        Assert.Equal("must be an absolute URL", V("url", "not a url"));
        Assert.Equal("must be a string", V("url", 5));
    }

    [Fact]
    public void UnknownType_IsRefused() => Assert.Equal("unknown type color", V("color", "red"));

    [Fact]
    public void CoversEveryEntryType() =>
        Assert.Equal(["string", "number", "boolean", "string[]", "enum", "secret", "json", "duration", "url"], CliConfigEntryTypes.All);

    [Fact]
    public void MinMax_StringLength()
    {
        var rules = new CliConfigValidation { Min = 2, Max = 4 };
        Assert.Equal("must be at least 2 characters", V("string", "a", rules));
        Assert.Equal("must be at most 4 characters", V("string", "abcde", rules));
        Assert.Null(V("string", "abc", rules));
    }

    [Fact]
    public void MinMax_SecretAndUrlLength()
    {
        Assert.Equal("must be at least 8 characters", V("secret", "short", new CliConfigValidation { Min = 8 }));
        Assert.Equal("must be at most 10 characters", V("url", "https://example.com", new CliConfigValidation { Max = 10 }));
    }

    [Fact]
    public void MinMax_NumberValue()
    {
        var rules = new CliConfigValidation { Min = 1, Max = 10 };
        Assert.Equal("must be at least 1", V("number", 0, rules));
        Assert.Equal("must be at most 10", V("number", 11, rules));
        Assert.Null(V("number", 1, rules));
        Assert.Null(V("number", 10, rules));
    }

    [Fact]
    public void MinMax_StringListItemCount()
    {
        var rules = new CliConfigValidation { Min = 1, Max = 2 };
        Assert.Equal("must have at least 1 items", V("string[]", J("[]"), rules));
        Assert.Equal("must have at most 2 items", V("string[]", J("[\"a\",\"b\",\"c\"]"), rules));
    }

    [Fact]
    public void MinMax_DurationInMilliseconds()
    {
        var rules = new CliConfigValidation { Min = 60_000, Max = 86_400_000 };
        Assert.Equal("must be at least 1m", V("duration", "30s", rules));
        Assert.Equal("must be at most 1d", V("duration", "2d", rules));
        Assert.Null(V("duration", "1h", rules));
    }

    [Fact]
    public void Pattern_StringItemsUrlSecret()
    {
        Assert.Null(V("string", "abc", new CliConfigValidation { Pattern = "^[a-z]+$" }));
        Assert.Equal("must match ^[a-z]+$", V("string", "ABC", new CliConfigValidation { Pattern = "^[a-z]+$" }));
        Assert.Equal("'x' must match ^\\d+$", V("string[]", J("[\"1\",\"x\"]"), new CliConfigValidation { Pattern = "^\\d+$" }));
        Assert.Equal("must match ^https:", V("url", "http://a.b", new CliConfigValidation { Pattern = "^https:" }));
        Assert.Equal("must match ^sk_", V("secret", "pk_1", new CliConfigValidation { Pattern = "^sk_" }));
    }

    [Fact]
    public void InvalidPattern_NeverMatches() =>
        Assert.Equal("must match (", V("string", "x", new CliConfigValidation { Pattern = "(" }));

    [Fact]
    public void Options_StringAndEachItem()
    {
        var rules = new CliConfigValidation { Options = ["x", "y"] };
        Assert.Equal("must be one of: x, y", V("string", "z", rules));
        Assert.Null(V("string", "x", rules));
        Assert.Equal("'z' must be one of: x, y", V("string[]", J("[\"x\",\"z\"]"), rules));
    }

    [Fact]
    public void Required_RefusesEmptyValues()
    {
        var rules = new CliConfigValidation { Required = true };
        Assert.Equal("is required", V("string", "", rules));
        Assert.Equal("is required", V("string[]", J("[]"), rules));
        Assert.Equal("is required", V("secret", "", rules));
        Assert.Null(V("string", "x", rules));
    }

    [Fact]
    public void NullResets_AllowedUnlessRequiredWithoutADefault()
    {
        Assert.Null(V("string", null));
        Assert.Equal("is required", V("string", null, new CliConfigValidation { Required = true }));
        Assert.Null(V("string", null, new CliConfigValidation { Required = true }, "d"));
        Assert.Null(V("number", null, new CliConfigValidation { Min = 5 }));
    }

    [Fact]
    public void ParseInput_Number()
    {
        Assert.Equal(42, CliConfigValues.AsNumber(CliConfigValues.ParseInput("number", " 42 ")));
        Assert.Equal(-1.5, CliConfigValues.AsNumber(CliConfigValues.ParseInput("number", "-1.5")));
        Assert.Equal("must be a number", Assert.Throws<FormatException>(() => CliConfigValues.ParseInput("number", "abc")).Message);
        Assert.Equal("must be a number", Assert.Throws<FormatException>(() => CliConfigValues.ParseInput("number", "")).Message);
    }

    [Fact]
    public void ParseInput_Boolean()
    {
        foreach (var t in new[] { "true", "YES", "on", "1" }) Assert.True(CliConfigValues.AsBoolean(CliConfigValues.ParseInput("boolean", t)));
        foreach (var t in new[] { "false", "No", "off", "0" }) Assert.False(CliConfigValues.AsBoolean(CliConfigValues.ParseInput("boolean", t)));
        Assert.Equal("must be true or false", Assert.Throws<FormatException>(() => CliConfigValues.ParseInput("boolean", "maybe")).Message);
    }

    [Fact]
    public void ParseInput_StringList_CommaSeparatedOrJsonArray()
    {
        List<string>? L(string text) => CliConfigValues.AsStringList(CliConfigValues.ParseInput("string[]", text));
        Assert.Equal(["a", "b", "c"], L("a, b ,c"));
        Assert.Equal(["a,b", "c"], L("[\"a,b\", \"c\"]"));
        Assert.Equal([], L(""));
        Assert.Equal(["a", "b"], L("a,,b"));
        Assert.Equal("must be a JSON array or a comma-separated list",
            Assert.Throws<FormatException>(() => CliConfigValues.ParseInput("string[]", "[oops")).Message);
        Assert.Throws<FormatException>(() => CliConfigValues.ParseInput("string[]", "["));
    }

    [Fact]
    public void ParseInput_Json()
    {
        Assert.True(JsonNode.DeepEquals(J("{\"a\":1}"), CliConfigValues.ParseInput("json", "{\"a\":1}")));
        Assert.Null(CliConfigValues.ParseInput("json", "null"));
        Assert.Equal("must be JSON", Assert.Throws<FormatException>(() => CliConfigValues.ParseInput("json", "{a")).Message);
    }

    [Fact]
    public void ParseInput_EverythingElseAsTyped()
    {
        Assert.Equal(" spaced ", CliConfigValues.AsString(CliConfigValues.ParseInput("string", " spaced ")));
        Assert.Equal("p@ss word", CliConfigValues.AsString(CliConfigValues.ParseInput("secret", "p@ss word")));
        Assert.Equal("30m", CliConfigValues.AsString(CliConfigValues.ParseInput("duration", "30m")));
        Assert.Equal("a", CliConfigValues.AsString(CliConfigValues.ParseInput("enum", "a")));
        Assert.Equal("https://x", CliConfigValues.AsString(CliConfigValues.ParseInput("url", "https://x")));
    }

    [Fact]
    public void Durations_ParseOneUnit()
    {
        Assert.Equal(500, CliConfigValues.ParseDuration("500ms"));
        Assert.Equal(30_000, CliConfigValues.ParseDuration("30s"));
        Assert.Equal(900_000, CliConfigValues.ParseDuration("15m"));
        Assert.Equal(7_200_000, CliConfigValues.ParseDuration("2h"));
        Assert.Equal(604_800_000, CliConfigValues.ParseDuration("7d"));
        Assert.Equal(3_600_000, CliConfigValues.ParseDuration(" 1h "));
        Assert.Null(CliConfigValues.ParseDuration("1h30m"));
        Assert.Null(CliConfigValues.ParseDuration((JsonNode)1000));
    }

    [Fact]
    public void Durations_FormatInTheLargestUnitThatDivides()
    {
        Assert.Equal("0s", CliConfigValues.FormatDuration(0));
        Assert.Equal("1500ms", CliConfigValues.FormatDuration(1500));
        Assert.Equal("90s", CliConfigValues.FormatDuration(90_000));
        Assert.Equal("30m", CliConfigValues.FormatDuration(1_800_000));
        Assert.Equal("1d", CliConfigValues.FormatDuration(86_400_000));
        Assert.Equal("1h", CliConfigValues.FormatDuration(TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CliConfigValues.FormatDuration(-1));
    }

    [Fact]
    public void Durations_RoundTrip()
    {
        foreach (var text in new[] { "250ms", "45s", "5m", "6h", "30d" })
            Assert.Equal(text, CliConfigValues.FormatDuration(CliConfigValues.ParseDuration(text)!.Value));
    }

    [Fact]
    public void InfersTheTypeOfAFreeValue()
    {
        Assert.Equal("boolean", CliConfigValues.InferType(true));
        Assert.Equal("number", CliConfigValues.InferType(3));
        Assert.Equal("string[]", CliConfigValues.InferType(J("[\"a\"]")));
        Assert.Equal("json", CliConfigValues.InferType(J("[1]")));
        Assert.Equal("json", CliConfigValues.InferType(J("{\"a\":1}")));
        Assert.Equal("string", CliConfigValues.InferType("x"));
        Assert.Equal("string", CliConfigValues.InferType(null));
    }

    [Fact]
    public void ClonesAndComparesJsonValues()
    {
        var value = J("{\"a\":[1,2]}");
        var copy = CliConfigValues.Clone(value);
        Assert.True(CliConfigValues.AreEqual(value, copy));
        Assert.NotSame(value, copy);
        Assert.True(CliConfigValues.AreEqual(J("[\"a\"]"), J("[\"a\"]")));
        Assert.False(CliConfigValues.AreEqual(J("[\"a\"]"), J("[\"b\"]")));
        Assert.True(CliConfigValues.AreEqual(1, 1));
        Assert.False(CliConfigValues.AreEqual(null, J("{}")));
    }

    [Fact]
    public void PermissionMatching_WildcardsCaseInsensitive()
    {
        Assert.True(CliConfigContext.PermissionMatches("*", "config:write"));
        Assert.True(CliConfigContext.PermissionMatches("config:*", "config:read"));
        Assert.True(CliConfigContext.PermissionMatches("*:read", "config:read"));
        Assert.False(CliConfigContext.PermissionMatches("*:read", "config:write"));
        Assert.True(CliConfigContext.PermissionMatches("CONFIG:READ", "config:read"));
        Assert.False(CliConfigContext.PermissionMatches("config", "config:read"));
        Assert.True(CliConfigContext.HasCliPermission(["files:read", "config:read"], "config:read"));
        Assert.False(CliConfigContext.HasCliPermission(null, "config:read"));
    }

    [Fact]
    public void Contexts_WithoutACallerEverythingIsAllowed()
    {
        Assert.True(CliConfigContext.For(null).HasPermission("config:write"));
        var ctx = CliConfigContext.For(new CliConfigCaller(null, "ann", ["config:read"]), "api");
        Assert.True(ctx.HasPermission("config:read"));
        Assert.False(ctx.HasPermission("config:write"));
        Assert.Equal("api", ctx.Via);
        Assert.Equal("ann", ctx.Caller?.UserName);
        Assert.False(CliConfigContext.For(new CliConfigCaller(null, "bob", null)).HasPermission("config:read"));
    }

    [Fact]
    public void Errors_SerializeAsTheApiAnswersThem()
    {
        Assert.Equal("{\"error\":\"version conflict\",\"version\":\"4\"}", new CliConfigException(409, "version conflict", null, "4").ToJson().ToJsonString());
        Assert.Equal("{\"error\":\"validation failed\",\"errors\":{\"a\":\"bad\"}}",
            new CliConfigException(400, "validation failed", new Dictionary<string, string> { ["a"] = "bad" }).ToJson().ToJsonString());
        Assert.Equal("4", new CliConfigConflictException("4").CurrentVersion);
    }
}
