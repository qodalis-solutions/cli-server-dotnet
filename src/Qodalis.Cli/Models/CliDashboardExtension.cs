using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Qodalis.Cli.Models;

/// <summary>
/// A page a plugin adds to the admin dashboard: the dashboard lists it in its navigation and, when it is opened,
/// loads <see cref="Entry"/> (an ES module the plugin serves) and mounts it (see the dashboard's
/// <c>docs/extensions.md</c>). Registered with <c>CliBuilder.AddDashboardExtension</c>; served by the admin plugin at
/// <c>GET /api/v1/qcli/dashboard/extensions</c>.
/// </summary>
public partial class CliDashboardExtension
{
    /// <summary>The navigation groups; any other value becomes <c>system</c>.</summary>
    public static readonly IReadOnlyList<string> Groups = ["overview", "operations", "data", "access", "system"];

    /// <summary><c>^[a-z0-9][a-z0-9-]{0,39}$</c>, unique; also the page's path segment (<c>/x/&lt;id&gt;</c>).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>1-40 characters.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>Titles by language code (<c>de</c>, <c>pt-BR</c>).</summary>
    [JsonPropertyName("titles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Titles { get; init; }

    /// <summary>One emoji, or an http(s) or root-relative image URL.</summary>
    [JsonPropertyName("icon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; init; }

    /// <summary>Navigation group: <c>overview</c>, <c>operations</c>, <c>data</c>, <c>access</c>, <c>system</c> (default).</summary>
    [JsonPropertyName("group")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Group { get; init; }

    /// <summary>Order within the group (default 100).</summary>
    [JsonPropertyName("order")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Order { get; init; }

    /// <summary>The page shows only for users holding this permission (wildcards match).</summary>
    [JsonPropertyName("permission")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Permission { get; init; }

    /// <summary>The module's URL: a root-relative path (<c>/...</c>, not <c>//</c>) or an http(s) URL.</summary>
    [JsonPropertyName("entry")]
    public required string Entry { get; init; }

    /// <summary>Stylesheets (same rules as <see cref="Entry"/>), at most 5.</summary>
    [JsonPropertyName("styles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Styles { get; init; }

    /// <summary>Shown in the page's error panel.</summary>
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }

    /// <summary>
    /// Slots the module fills besides its page (see <see cref="WidgetSlots"/>): <c>overview</c> shows its cards on
    /// the dashboard's Overview, <c>palette</c> adds entries to its command palette (e.g. jump to a job). Left out of
    /// the JSON when empty.
    /// </summary>
    [JsonPropertyName("widgets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Widgets { get; init; }

    /// <summary>
    /// A path below the page ending where search text goes (<c>/?q=</c>): the dashboard's command palette offers
    /// "Search &lt;title&gt; for …" and opens <c>/x/&lt;id&gt;&lt;search&gt;&lt;text&gt;</c>.
    /// </summary>
    [JsonPropertyName("search")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Search { get; init; }

    /// <summary>The slots a module can fill (<see cref="Widgets"/>).</summary>
    public static readonly IReadOnlyList<string> WidgetSlots = ["overview", "palette"];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex LanguagePattern();

    [GeneratedRegex(@"[\s\u0000-\u001f\u007f]")]
    private static partial Regex WhitespaceOrControl();

    [GeneratedRegex(@"[\s<>""'/\\]")]
    private static partial Regex NotInEmoji();

    [GeneratedRegex("^https?://[^/]", RegexOptions.IgnoreCase)]
    private static partial Regex HttpUrl();

    private static ArgumentException Invalid(string reason) => new($"invalid dashboard extension: {reason}");

    private static bool IsUrl(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 2048 || WhitespaceOrControl().IsMatch(value)) return false;
        if (value.StartsWith('/')) return !value.StartsWith("//", StringComparison.Ordinal) && !value.StartsWith("/\\", StringComparison.Ordinal);
        return HttpUrl().IsMatch(value);
    }

    private static bool IsIcon(string value)
    {
        if (IsUrl(value)) return true;
        if (value.Length > 32 || NotInEmoji().IsMatch(value)) return false;
        // One grapheme: a flag or family emoji is several code points
        return new StringInfo(value).LengthInTextElements == 1;
    }

    /// <summary>
    /// Validates the extension and returns a copy (an unknown group becomes <c>system</c>). Throws
    /// <see cref="ArgumentException"/> <c>invalid dashboard extension: &lt;reason&gt;</c>.
    /// </summary>
    public CliDashboardExtension Validate()
    {
        if (Id is null || !IdPattern().IsMatch(Id)) throw Invalid($"id must match {IdPattern()}");
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 40) throw Invalid($"{Id}: title must be 1-40 characters");
        if (!IsUrl(Entry)) throw Invalid($"{Id}: entry must be a root-relative path or an http(s) URL");
        if (Titles is not null && Titles.Any(t => !LanguagePattern().IsMatch(t.Key) || string.IsNullOrWhiteSpace(t.Value) || t.Value.Length > 40))
            throw Invalid($"{Id}: titles must map language codes to titles of 1-40 characters");
        if (Icon is not null && !IsIcon(Icon)) throw Invalid($"{Id}: icon must be an emoji or an image URL");
        if (Permission is not null && (string.IsNullOrWhiteSpace(Permission) || Permission.Length > 100))
            throw Invalid($"{Id}: permission must be a permission name");
        if (Styles is not null && (Styles.Count > 5 || !Styles.All(IsUrl)))
            throw Invalid($"{Id}: styles must be at most 5 root-relative paths or http(s) URLs");
        if (Version is not null && (Version.Length == 0 || Version.Length > 50)) throw Invalid($"{Id}: version must be 1-50 characters");
        if (Widgets is not null && Widgets.Any(w => !WidgetSlots.Contains(w)))
            throw Invalid($"{Id}: widgets must be among {string.Join(", ", WidgetSlots)}");
        if (Search is not null && (!Search.StartsWith('/') || Search.Length > 200 || WhitespaceOrControl().IsMatch(Search)))
            throw Invalid($"{Id}: search must be a path below the page starting with /");
        return new CliDashboardExtension
        {
            Id = Id,
            Title = Title,
            Titles = Titles is null ? null : new Dictionary<string, string>(Titles),
            Icon = Icon,
            Group = Group is null ? null : Groups.Contains(Group) ? Group : "system",
            Order = Order,
            Permission = Permission,
            Entry = Entry,
            Styles = Styles?.ToList(),
            Version = Version,
            Widgets = Widgets is { Count: > 0 } ? Widgets.Distinct().ToList() : null,
            Search = Search,
        };
    }
}

/// <summary>The dashboard extensions registered on this host, in registration order (see <c>CliBuilder.AddDashboardExtension</c>).</summary>
public class CliDashboardExtensions(IReadOnlyList<CliDashboardExtension> extensions)
{
    /// <summary>Every registered extension.</summary>
    public IReadOnlyList<CliDashboardExtension> All { get; } = extensions;
}
