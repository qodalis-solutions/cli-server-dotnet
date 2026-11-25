namespace Qodalis.Cli.Plugin.Portal;

/// <summary>Options of the portal plugin (<c>cli.AddPortal(o => ...)</c>), validated when <c>UseQodalisPortal()</c> runs.</summary>
public sealed class PortalOptions
{
    /// <summary>
    /// Keeps the <c>portal</c> config provider's changes (the branding) in this store (its own) instead of the server's
    /// default one; the host's store for the provider's id (<c>cli.SetConfigStore(store, "portal")</c>) still wins.
    /// </summary>
    public Qodalis.Cli.Abstractions.Config.ICliConfigStore? ConfigStore { get; set; }

    /// <summary>
    /// Where the site is mounted: starts with <c>/</c>, no trailing <c>/</c>; <c>/</c> itself mounts it at the root
    /// (it then answers only its own paths, never the host's routes). Default <c>/qcli/portal</c>.
    /// </summary>
    public string Path { get; set; } = "/qcli/portal";

    /// <summary>The portal's name (header, browser tab): 1-80 characters without control characters. Default <c>Documentation</c>.</summary>
    public string Name { get; set; } = "Documentation";

    /// <summary>The header's logo: an http(s) URL, a root-relative path or a base64 <c>data:image/…</c> URL (up to 200 000 characters); null = none.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>The colour scheme, as the admin dashboard's theme (portal default mode: system).</summary>
    public PortalTheme Theme { get; set; } = new();

    /// <summary>Links in the footer (at most 20; label 1-80 characters, URL http(s) or root-relative).</summary>
    public List<PortalLink> FooterLinks { get; } = [];

    /// <summary>
    /// A folder holding the built site (<c>index.html</c>, <c>assets/</c>), used instead of the npm package, the sibling
    /// <c>cli-server-dashboard/portal/dist</c> and the embedded copy. When set, only this folder is used.
    /// </summary>
    public string? SitePath { get; set; }
}

/// <summary>A footer link of the portal.</summary>
public sealed record PortalLink(string Label, string Url);

/// <summary>Colour scheme mode of the portal.</summary>
public enum PortalThemeMode
{
    /// <summary>Follows the browser's <c>prefers-color-scheme</c> (default).</summary>
    System,
    /// <summary>The dark scheme.</summary>
    Dark,
    /// <summary>The light scheme.</summary>
    Light,
}

/// <summary>The portal's colour scheme: tokens left unset keep the site's defaults.</summary>
public sealed class PortalTheme
{
    /// <summary>Scheme to show. Default <see cref="PortalThemeMode.System"/>.</summary>
    public PortalThemeMode Mode { get; set; } = PortalThemeMode.System;

    /// <summary>Overrides for the dark scheme.</summary>
    public PortalThemeColors Dark { get; set; } = new();

    /// <summary>Overrides for the light scheme.</summary>
    public PortalThemeColors Light { get; set; } = new();
}

/// <summary>
/// Colour overrides for one scheme of the portal: the admin dashboard's 21 tokens. Every property is a CSS colour
/// (hex, <c>rgb()</c>/<c>hsl()</c>/<c>oklch()</c>... or a named colour); <c>null</c> keeps the site's default.
/// </summary>
public sealed class PortalThemeColors
{
    /// <summary>Primary buttons, active navigation and focus rings.</summary>
    public string? Primary { get; set; }
    /// <summary>Primary buttons on hover.</summary>
    public string? PrimaryHover { get; set; }
    /// <summary>Text and icons drawn on <see cref="Primary"/>.</summary>
    public string? OnPrimary { get; set; }
    /// <summary>Highlighted text: links, active items.</summary>
    public string? Accent { get; set; }
    /// <summary>Page background.</summary>
    public string? Background { get; set; }
    /// <summary>Start of the page background gradient.</summary>
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
    /// <summary>Headings.</summary>
    public string? Heading { get; set; }
    /// <summary>Body text.</summary>
    public string? Text { get; set; }
    /// <summary>Secondary text and labels.</summary>
    public string? Muted { get; set; }
    /// <summary>Hints and placeholders.</summary>
    public string? Subtle { get; set; }
    /// <summary>Success states.</summary>
    public string? Success { get; set; }
    /// <summary>Warning states.</summary>
    public string? Warning { get; set; }
    /// <summary>Errors.</summary>
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
