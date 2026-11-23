namespace Qodalis.Cli.Models;

/// <summary>
/// A dashboard module: a page of the admin dashboard built in <c>cli-server-dashboard/modules/&lt;id&gt;</c>
/// (<c>@qodalis/cli-dashboard-module-&lt;id&gt;</c>), embedded in the plugin that owns its API and registered with
/// <c>CliBuilder.AddDashboardModule</c>. It is a <see cref="CliDashboardExtension"/> without the URLs: the entry and
/// stylesheet URLs come from the files, under <see cref="Prefix"/>, carrying their hash.
/// </summary>
public sealed class CliDashboardModule
{
    /// <summary>The extension's id; also the build's folder name (<c>wwwroot/&lt;id&gt;</c>).</summary>
    public required string Id { get; init; }

    /// <summary>The page's title in English.</summary>
    public required string Title { get; init; }

    /// <summary>Titles by language code.</summary>
    public IReadOnlyDictionary<string, string>? Titles { get; init; }

    /// <summary>One emoji, or an image URL.</summary>
    public string? Icon { get; init; }

    /// <summary>Navigation group: <c>overview</c>, <c>operations</c>, <c>data</c>, <c>access</c>, <c>system</c>.</summary>
    public string? Group { get; init; }

    /// <summary>Order within the group.</summary>
    public int? Order { get; init; }

    /// <summary>The page shows only for users holding this permission.</summary>
    public string? Permission { get; init; }

    /// <summary>Slots the module fills besides its page (<c>overview</c>, <c>palette</c>).</summary>
    public IReadOnlyList<string>? Widgets { get; init; }

    /// <summary>The command palette's search path below the page (<c>/?q=</c>).</summary>
    public string? Search { get; init; }

    /// <summary>Where the files are served, outside the API; default <c>/qcli/modules/&lt;id&gt;</c>.</summary>
    public string? Prefix { get; init; }

    /// <summary>The prefix the files are served at.</summary>
    public string ResolvedPrefix => (Prefix ?? $"/qcli/modules/{Id}").TrimEnd('/');
}
