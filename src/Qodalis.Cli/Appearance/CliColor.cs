using System.Text.RegularExpressions;

namespace Qodalis.Cli.Appearance;

/// <summary>
/// The CSS colors the Qodalis UIs (admin dashboard, portal site) accept in a theme: a hex color, a color function with
/// plain numeric arguments, or a named color. Nothing that can end a declaration (<c>; { }</c>, quotes) or load
/// anything (<c>url(</c>, <c>var(</c>).
/// </summary>
public static class CliColor
{
    private const int MaxLength = 100;

    private static readonly Regex[] Patterns =
    {
        new(@"^#(?:[0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(?:rgba?|hsla?|hwb|lab|lch|oklab|oklch)\([0-9a-z.,%+\-\s/]*\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^[a-z]{3,30}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    };

    /// <summary>True when <paramref name="value"/> (trimmed) is an accepted color.</summary>
    public static bool IsValid(string? value)
    {
        if (value == null) return false;
        var v = value.Trim();
        return v.Length > 0 && v.Length <= MaxLength && Patterns.Any(p => p.IsMatch(v));
    }
}
