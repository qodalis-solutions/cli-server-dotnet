using System.Text.Json;
using System.Text.RegularExpressions;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>
/// Color scheme mode of the admin dashboard.
/// </summary>
public enum AdminThemeMode
{
    /// <summary>The dark scheme (default).</summary>
    Dark,
    /// <summary>The light scheme.</summary>
    Light,
    /// <summary>Follows the browser's <c>prefers-color-scheme</c>.</summary>
    System,
}

/// <summary>
/// Color overrides for one scheme of the admin dashboard. Every property is a
/// CSS color (hex, <c>rgb()</c>/<c>hsl()</c>/<c>oklch()</c>... or a named color);
/// <c>null</c> keeps the dashboard default.
/// </summary>
public class AdminThemeColors
{
    /// <summary>Primary buttons, active navigation, toggles and focus rings.</summary>
    public string? Primary { get; set; }
    /// <summary>Primary buttons on hover.</summary>
    public string? PrimaryHover { get; set; }
    /// <summary>Text and icons drawn on <see cref="Primary"/>.</summary>
    public string? OnPrimary { get; set; }
    /// <summary>Highlighted text: links, command names, active items.</summary>
    public string? Accent { get; set; }
    /// <summary>Page background.</summary>
    public string? Background { get; set; }
    /// <summary>Start of the page background gradient (set it to <see cref="Background"/> for a flat page).</summary>
    public string? BackgroundAccent { get; set; }
    /// <summary>Sidebar background.</summary>
    public string? Sidebar { get; set; }
    /// <summary>Cards, panels and tables.</summary>
    public string? Surface { get; set; }
    /// <summary>Opaque surface of dialogs and dropdown menus.</summary>
    public string? Popover { get; set; }
    /// <summary>Inputs, secondary buttons and chips.</summary>
    public string? Control { get; set; }
    /// <summary>Inputs and secondary buttons on hover.</summary>
    public string? ControlHover { get; set; }
    /// <summary>Borders and dividers.</summary>
    public string? Border { get; set; }
    /// <summary>Backdrop behind dialogs.</summary>
    public string? Overlay { get; set; }
    /// <summary>Headings and values.</summary>
    public string? Heading { get; set; }
    /// <summary>Body text.</summary>
    public string? Text { get; set; }
    /// <summary>Secondary text and labels.</summary>
    public string? Muted { get; set; }
    /// <summary>Hints, placeholders and timestamps.</summary>
    public string? Subtle { get; set; }
    /// <summary>Success states.</summary>
    public string? Success { get; set; }
    /// <summary>Warning states.</summary>
    public string? Warning { get; set; }
    /// <summary>Errors and destructive actions.</summary>
    public string? Error { get; set; }
    /// <summary>Informational states.</summary>
    public string? Info { get; set; }

    internal IEnumerable<(string Token, string? Value)> Entries()
    {
        yield return ("primary", Primary);
        yield return ("primaryHover", PrimaryHover);
        yield return ("onPrimary", OnPrimary);
        yield return ("accent", Accent);
        yield return ("background", Background);
        yield return ("backgroundAccent", BackgroundAccent);
        yield return ("sidebar", Sidebar);
        yield return ("surface", Surface);
        yield return ("popover", Popover);
        yield return ("control", Control);
        yield return ("controlHover", ControlHover);
        yield return ("border", Border);
        yield return ("overlay", Overlay);
        yield return ("heading", Heading);
        yield return ("text", Text);
        yield return ("muted", Muted);
        yield return ("subtle", Subtle);
        yield return ("success", Success);
        yield return ("warning", Warning);
        yield return ("error", Error);
        yield return ("info", Info);
    }
}

/// <summary>
/// Color scheme of the admin dashboard, served at <c>/qcli/admin/theme.json</c>
/// and applied before the dashboard's first render. Tokens left unset keep the
/// dashboard defaults.
/// </summary>
public class AdminTheme
{
    /// <summary>Scheme to show: dark (default), light, or system to follow the browser.</summary>
    public AdminThemeMode Mode { get; set; } = AdminThemeMode.Dark;
    /// <summary>Overrides for the dark scheme.</summary>
    public AdminThemeColors Dark { get; set; } = new();
    /// <summary>Overrides for the light scheme.</summary>
    public AdminThemeColors Light { get; set; } = new();

    private const int MaxColorLength = 100;

    // A hex color, a color function with plain numeric arguments, or a named color:
    // nothing that can end the declaration (; { } quotes) or load anything (url(, var().
    private static readonly Regex[] ColorPatterns =
    {
        new(@"^#(?:[0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(?:rgba?|hsla?|hwb|lab|lch|oklab|oklch)\([0-9a-z.,%+\-\s/]*\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^[a-z]{3,30}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    };

    /// <summary>True when <paramref name="value"/> is a color the dashboard accepts.</summary>
    public static bool IsColor(string? value)
    {
        if (value == null) return false;
        var v = value.Trim();
        return v.Length > 0 && v.Length <= MaxColorLength && ColorPatterns.Any(p => p.IsMatch(v));
    }

    /// <summary>
    /// Validates the theme and returns the <c>theme.json</c> document:
    /// <c>{ "mode": "dark", "dark": { ... }, "light": { ... } }</c> with only the set tokens.
    /// </summary>
    /// <exception cref="ArgumentException">A value is not a plain color, or the mode is undefined.</exception>
    public Dictionary<string, object> ToDocument()
    {
        if (!Enum.IsDefined(Mode))
            throw new ArgumentException($"Invalid admin theme mode '{Mode}' (expected Dark, Light or System).");

        return new Dictionary<string, object>
        {
            ["mode"] = Mode.ToString().ToLowerInvariant(),
            ["dark"] = Validate(Dark, "dark"),
            ["light"] = Validate(Light, "light"),
        };
    }

    /// <summary>The validated theme as the JSON served at <c>/qcli/admin/theme.json</c>.</summary>
    public string ToJson() => JsonSerializer.Serialize(ToDocument());

    private static Dictionary<string, string> Validate(AdminThemeColors? colors, string scheme)
    {
        var result = new Dictionary<string, string>();
        if (colors == null) return result;
        foreach (var (token, value) in colors.Entries())
        {
            if (value == null) continue;
            if (!IsColor(value))
                throw new ArgumentException(
                    $"Invalid admin theme color for \"{scheme}.{token}\": \"{value}\" (use a hex, rgb()/hsl()/oklch() or named color).");
            result[token] = value.Trim();
        }
        return result;
    }
}
