using System.Text.RegularExpressions;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>
/// The name and icon an application ships the admin dashboard under: its title and icon (browser tab, sidebar,
/// sign-in page) and the name of its terminal (the CLI's welcome banner). Served with the color scheme at
/// <c>/qcli/admin/theme.json</c>; anything left null keeps the Qodalis default (Qodalis Cli Admin, the Qodalis
/// Solutions logo, Web CLI).
/// </summary>
public class AdminBranding
{
    private const int MaxNameLength = 80;
    private const int MaxIconLength = 200_000;

    private static readonly Regex DataImage = new(
        @"^data:image/(?:png|jpeg|gif|webp|svg\+xml|x-icon|vnd\.microsoft\.icon);base64,[A-Za-z0-9+/]+={0,2}$",
        RegexOptions.CultureInvariant);

    /// <summary>The dashboard's name, e.g. <c>RateBell</c> (default <c>Qodalis Cli Admin</c>).</summary>
    public string? Name { get; set; }

    /// <summary>
    /// The dashboard's icon instead of the Qodalis Solutions logo: an absolute http(s) URL, a root-relative path
    /// (<c>/favicon.svg</c>) or a base64 <c>data:image/...</c> URL.
    /// </summary>
    public string? IconUrl { get; set; }

    /// <summary>The terminal's name in its welcome banner, e.g. <c>RateBell CLI</c> (default <c>Web CLI</c>).</summary>
    public string? CliName { get; set; }

    /// <summary>
    /// Validates the branding and returns its part of <c>theme.json</c>:
    /// <c>{ "name": ..., "icon": ..., "cli": { "name": ... } }</c> with only the set values, or null when nothing
    /// is set.
    /// </summary>
    /// <exception cref="ArgumentException">A name is too long or has control characters, or the icon is not an accepted URL.</exception>
    public Dictionary<string, object>? ToDocument()
    {
        var document = new Dictionary<string, object>();
        if (Text(Name, nameof(Name)) is { } name) document["name"] = name;
        if (Icon(IconUrl) is { } icon) document["icon"] = icon;
        if (Text(CliName, nameof(CliName)) is { } cliName) document["cli"] = new Dictionary<string, object> { ["name"] = cliName };
        return document.Count > 0 ? document : null;
    }

    private static string? Text(string? value, string property)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > MaxNameLength)
            throw new ArgumentException($"Admin branding {property} is longer than {MaxNameLength} characters.");
        if (text.Any(char.IsControl))
            throw new ArgumentException($"Admin branding {property} may not contain control characters.");
        return text;
    }

    private static string? Icon(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > MaxIconLength)
            throw new ArgumentException($"Admin branding {nameof(IconUrl)} is longer than {MaxIconLength} characters.");
        var rootRelative = text.StartsWith('/') && (text.Length == 1 || (text[1] != '/' && text[1] != '\\'))
            && !text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
        var web = Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        if (!rootRelative && !web && !DataImage.IsMatch(text))
            throw new ArgumentException(
                $"Admin branding {nameof(IconUrl)} must be an absolute http(s) URL, a root-relative path or a base64 data:image URL.");
        return text;
    }
}
