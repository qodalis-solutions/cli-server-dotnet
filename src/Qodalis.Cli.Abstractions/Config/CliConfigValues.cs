using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Qodalis.Cli.Abstractions.Config;

/// <summary>
/// Validation, parsing and formatting of config values (JSON), the same rules on every server.
/// </summary>
public static class CliConfigValues
{
    private static readonly Regex DurationPattern = new(@"^(\d+)(ms|s|m|h|d)$", RegexOptions.CultureInvariant);
    private static readonly Regex UrlScheme = new(@"^[a-z][a-z0-9+.-]*://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly (string Unit, long Ms)[] Units = [("d", 86_400_000), ("h", 3_600_000), ("m", 60_000), ("s", 1_000)];

    private static readonly HashSet<string> True = ["true", "yes", "on", "1"];
    private static readonly HashSet<string> False = ["false", "no", "off", "0"];

    /// <summary>
    /// A <c>duration</c> setting's value in milliseconds: a whole number and one unit, <c>ms</c>, <c>s</c>, <c>m</c>,
    /// <c>h</c> or <c>d</c> (<c>500ms</c>, <c>30s</c>, <c>15m</c>, <c>12h</c>, <c>7d</c>); null when it is not one.
    /// </summary>
    public static long? ParseDuration(JsonNode? value) =>
        value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? ParseDuration(v.GetValue<string>()) : null;

    /// <inheritdoc cref="ParseDuration(JsonNode?)"/>
    public static long? ParseDuration(string? text)
    {
        if (text == null) return null;
        var match = DurationPattern.Match(text.Trim());
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return null;
        var unit = match.Groups[2].Value switch
        {
            "ms" => 1L,
            "s" => 1_000L,
            "m" => 60_000L,
            "h" => 3_600_000L,
            _ => 86_400_000L,
        };
        try
        {
            return checked(n * unit);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>Milliseconds as a duration in the largest unit that divides them (<c>90000</c> → <c>90s</c>, <c>3600000</c> → <c>1h</c>).</summary>
    public static string FormatDuration(double ms)
    {
        if (double.IsNaN(ms) || double.IsInfinity(ms) || ms < 0) throw new ArgumentOutOfRangeException(nameof(ms), $"invalid duration: {ms}");
        var whole = (long)Math.Round(ms);
        if (whole == 0) return "0s";
        foreach (var (unit, size) in Units)
        {
            if (whole % size == 0) return $"{whole / size}{unit}";
        }
        return $"{whole}ms";
    }

    /// <summary>A duration of a <see cref="TimeSpan"/> (<see cref="FormatDuration(double)"/>).</summary>
    public static string FormatDuration(TimeSpan span) => FormatDuration(span.TotalMilliseconds);

    /// <summary>True for null, <c>''</c> and <c>[]</c>.</summary>
    public static bool IsEmpty(JsonNode? value) =>
        value == null
        || (value is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().Length == 0)
        || (value is JsonArray a && a.Count == 0);

    /// <summary>The string of a JSON string; null for anything else.</summary>
    public static string? AsString(JsonNode? value) =>
        value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>The number of a JSON number; null for anything else.</summary>
    public static double? AsNumber(JsonNode? value)
    {
        if (value is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return null;
        return double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
    }

    /// <summary>The boolean of a JSON boolean; null for anything else.</summary>
    public static bool? AsBoolean(JsonNode? value) =>
        value is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValueKind() == JsonValueKind.True : null;

    /// <summary>The items of a JSON array of strings; null for anything else.</summary>
    public static List<string>? AsStringList(JsonNode? value)
    {
        if (value is not JsonArray array) return null;
        var items = new List<string>(array.Count);
        foreach (var item in array)
        {
            var s = AsString(item);
            if (s == null) return null;
            items.Add(s);
        }
        return items;
    }

    /// <summary>True for a whole number.</summary>
    public static bool IsInteger(JsonNode? value) => AsNumber(value) is { } d && Math.Abs(d % 1) == 0;

    /// <summary>A JSON array of strings.</summary>
    public static JsonArray StringList(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());

    /// <summary>A JSON value of a .NET value (strings, numbers, booleans, string lists, nodes).</summary>
    public static JsonNode? From(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        IEnumerable<string> list => StringList(list),
        _ => JsonSerializer.SerializeToNode(value),
    };

    /// <summary>A copy of a JSON value (so callers never share arrays or objects with a store).</summary>
    public static JsonNode? Clone(JsonNode? value) => value?.DeepClone();

    /// <summary>True when two JSON values are equal.</summary>
    public static bool AreEqual(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    private static bool Matches(string pattern, string value)
    {
        try
        {
            return Regex.IsMatch(value, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string Num(double d) => d.ToString(CultureInfo.InvariantCulture);

    private static bool IsJson(JsonNode? value, int depth = 0)
    {
        if (depth > 32) return false;
        return value switch
        {
            null => true,
            JsonArray a => a.All(v => IsJson(v, depth + 1)),
            JsonObject o => o.All(p => IsJson(p.Value, depth + 1)),
            JsonValue v => v.GetValueKind() != JsonValueKind.Number || AsNumber(v) != null,
            _ => false,
        };
    }

    /// <summary>
    /// Checks a new value against an entry's type and validation rules; the error message, or null when valid. A null
    /// value resets the setting: refused only for a <c>required</c> entry without a default. The rules:
    /// <list type="bullet">
    /// <item><c>string</c>, <c>secret</c>: a string; <c>min</c>/<c>max</c> its length, <c>pattern</c>, <c>options</c>.</item>
    /// <item><c>url</c>: an absolute URL (<c>scheme://...</c>); <c>min</c>/<c>max</c> length, <c>pattern</c>.</item>
    /// <item><c>number</c>: a finite number; <c>min</c>/<c>max</c> its value.</item>
    /// <item><c>boolean</c>: true or false. <c>enum</c>: one of <c>options</c>.</item>
    /// <item><c>string[]</c>: an array of strings; <c>min</c>/<c>max</c> the item count, <c>pattern</c> and <c>options</c> each item.</item>
    /// <item><c>duration</c>: <c>&lt;integer&gt;&lt;ms|s|m|h|d&gt;</c>; <c>min</c>/<c>max</c> in milliseconds.</item>
    /// <item><c>json</c>: any JSON value. <c>required</c>: not <c>''</c>, not <c>[]</c>.</item>
    /// </list>
    /// </summary>
    public static string? Validate(string type, CliConfigValidation? validation, JsonNode? defaultValue, JsonNode? value)
    {
        var rules = validation ?? new CliConfigValidation();
        if (value == null)
            return rules.Required == true && IsEmpty(defaultValue) ? "is required" : null;
        if (rules.Required == true && IsEmpty(value)) return "is required";

        string? LengthRules(string text)
        {
            if (rules.Min is { } min && text.Length < min) return $"must be at least {Num(min)} characters";
            if (rules.Max is { } max && text.Length > max) return $"must be at most {Num(max)} characters";
            if (rules.Pattern != null && !Matches(rules.Pattern, text)) return $"must match {rules.Pattern}";
            return null;
        }

        switch (type)
        {
            case CliConfigEntryTypes.String:
            case CliConfigEntryTypes.Secret:
            {
                if (AsString(value) is not { } text) return "must be a string";
                var error = LengthRules(text);
                if (error != null) return error;
                if (rules.Options is { Count: > 0 } options && text != "" && !options.Contains(text))
                    return $"must be one of: {string.Join(", ", options)}";
                return null;
            }
            case CliConfigEntryTypes.Url:
            {
                if (AsString(value) is not { } text) return "must be a string";
                if (text == "") return null;
                if (!Uri.TryCreate(text, UriKind.Absolute, out _) || !UrlScheme.IsMatch(text)) return "must be an absolute URL";
                return LengthRules(text);
            }
            case CliConfigEntryTypes.Number:
            {
                if (AsNumber(value) is not { } n) return "must be a number";
                if (rules.Min is { } min && n < min) return $"must be at least {Num(min)}";
                if (rules.Max is { } max && n > max) return $"must be at most {Num(max)}";
                return null;
            }
            case CliConfigEntryTypes.Boolean:
                return AsBoolean(value) != null ? null : "must be true or false";
            case CliConfigEntryTypes.Enum:
            {
                if (AsString(value) is not { } text) return "must be a string";
                var options = rules.Options ?? [];
                return options.Contains(text) ? null : $"must be one of: {string.Join(", ", options)}";
            }
            case CliConfigEntryTypes.StringList:
            {
                if (AsStringList(value) is not { } items) return "must be a list of strings";
                if (rules.Min is { } min && items.Count < min) return $"must have at least {Num(min)} items";
                if (rules.Max is { } max && items.Count > max) return $"must have at most {Num(max)} items";
                foreach (var item in items)
                {
                    if (rules.Pattern != null && !Matches(rules.Pattern, item)) return $"'{item}' must match {rules.Pattern}";
                    if (rules.Options is { Count: > 0 } options && !options.Contains(item))
                        return $"'{item}' must be one of: {string.Join(", ", options)}";
                }
                return null;
            }
            case CliConfigEntryTypes.Duration:
            {
                if (ParseDuration(value) is not { } ms) return "must be a duration such as 500ms, 30s, 15m, 12h or 7d";
                if (rules.Min is { } min && ms < min) return $"must be at least {FormatDuration(min)}";
                if (rules.Max is { } max && ms > max) return $"must be at most {FormatDuration(max)}";
                return null;
            }
            case CliConfigEntryTypes.Json:
                return IsJson(value) ? null : "must be a JSON value";
            default:
                return $"unknown type {type}";
        }
    }

    /// <summary>Validates a value against an entry (<see cref="Validate(string, CliConfigValidation?, JsonNode?, JsonNode?)"/>).</summary>
    public static string? Validate(CliConfigEntry entry, JsonNode? value) =>
        Validate(entry.Type, entry.Validation, entry.Default, value);

    /// <summary>
    /// A value typed in the terminal (<c>config set cors.cors.allowedOrigins a,b</c>) as the entry's type:
    /// <c>number</c> parsed, <c>boolean</c> from true/false/yes/no/on/off/1/0, <c>string[]</c> from a JSON array or a
    /// comma-separated list, <c>json</c> parsed, anything else as typed. Throws <see cref="FormatException"/> with the
    /// reason when the text does not parse.
    /// </summary>
    public static JsonNode? ParseInput(string type, string text)
    {
        var trimmed = text.Trim();
        switch (type)
        {
            case CliConfigEntryTypes.Number:
            {
                if (trimmed == "" || !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n))
                    throw new FormatException("must be a number");
                return n % 1 == 0 && Math.Abs(n) < 9e15 ? JsonValue.Create((long)n) : JsonValue.Create(n);
            }
            case CliConfigEntryTypes.Boolean:
            {
                var lower = trimmed.ToLowerInvariant();
                if (True.Contains(lower)) return JsonValue.Create(true);
                if (False.Contains(lower)) return JsonValue.Create(false);
                throw new FormatException("must be true or false");
            }
            case CliConfigEntryTypes.StringList:
            {
                if (trimmed.StartsWith('['))
                {
                    JsonNode? parsed;
                    try
                    {
                        parsed = JsonNode.Parse(trimmed);
                    }
                    catch (JsonException)
                    {
                        throw new FormatException("must be a JSON array or a comma-separated list");
                    }
                    if (parsed is not JsonArray) throw new FormatException("must be a JSON array or a comma-separated list");
                    return parsed;
                }
                return StringList(trimmed == "" ? [] : trimmed.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));
            }
            case CliConfigEntryTypes.Json:
                try
                {
                    return JsonNode.Parse(trimmed);
                }
                catch (JsonException)
                {
                    throw new FormatException("must be JSON");
                }
            default:
                return JsonValue.Create(text);
        }
    }

    /// <summary>The entry type of a value of a free key (a <c>custom</c> section): number, boolean, string[], json or string.</summary>
    public static string InferType(JsonNode? value) => value switch
    {
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => CliConfigEntryTypes.Boolean,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => CliConfigEntryTypes.Number,
        JsonArray => AsStringList(value) != null ? CliConfigEntryTypes.StringList : CliConfigEntryTypes.Json,
        JsonObject => CliConfigEntryTypes.Json,
        _ => CliConfigEntryTypes.String,
    };
}
