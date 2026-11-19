namespace Qodalis.Cli.Plugin.Docs;

/// <summary>Options of the docs plugin (<c>cli.AddDocs(o => ...)</c>).</summary>
public sealed class DocsOptions
{
    private readonly List<DocsSource> _sources = [];

    /// <summary>
    /// Where the pages are served (<c>&lt;base&gt;/pages</c>, <c>&lt;base&gt;/pages/&lt;slug&gt;</c>). Starts with
    /// <c>/</c>, no trailing <c>/</c>, not <c>/</c> itself. Default <c>/qcli/docs</c>.
    /// </summary>
    public string BasePath { get; set; } = "/qcli/docs";

    /// <summary>The guides section's label on the portal. Default <c>Guides</c>.</summary>
    public string Title { get; set; } = "Guides";

    /// <summary>The built-in variable <c>{{ name }}</c>. Default <c>Documentation</c>.</summary>
    public string Name { get; set; } = "Documentation";

    /// <summary>The built-in variable <c>{{ baseUrl }}</c>; null = the request's <c>scheme://host</c> plus path base. No trailing slash.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// The host's variables, replaced in titles and markdown (<c>{{ support }}</c>). Keys match
    /// <c>[A-Za-z][A-Za-z0-9_.-]*</c>; <c>baseUrl</c> and <c>name</c> are built in.
    /// </summary>
    public IDictionary<string, string> Variables { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Serves every <c>*.md</c> file directly in <paramref name="path"/> (relative to the content root; no subfolders,
    /// names starting with <c>.</c> or <c>_</c> ignored). The folder is read once, when <c>UseQodalisDocs()</c> runs.
    /// </summary>
    public DocsOptions AddFolder(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _sources.Add(new DocsSource(path, null, null));
        return this;
    }

    /// <summary>Serves <paramref name="markdown"/> (with optional front matter) as the page <paramref name="slug"/>.</summary>
    public DocsOptions AddPage(string slug, string markdown)
    {
        if (!DocsPageLoader.IsValidSlug(slug))
            throw new ArgumentException($"invalid docs page slug: {slug}");
        ArgumentNullException.ThrowIfNull(markdown);
        _sources.Add(new DocsSource(null, slug, markdown));
        return this;
    }

    internal IReadOnlyList<DocsSource> Sources => _sources;

    internal sealed record DocsSource(string? Folder, string? Slug, string? Markdown);
}
