using Microsoft.Extensions.Hosting;

namespace Qodalis.Cli.Plugin.Docs;

/// <summary>A page in the list (<c>GET &lt;BasePath&gt;/pages</c>, <c>docs list</c>): its title with the variables replaced.</summary>
public sealed record DocsPageSummary(string Slug, string Title, string Group, int Order);

/// <summary>A page with its markdown (<c>GET &lt;BasePath&gt;/pages/&lt;slug&gt;</c>, <c>docs read</c>), variables replaced.</summary>
public sealed record DocsPageView(string Slug, string Title, string Group, int Order, string Markdown);

/// <summary>
/// The docs plugin's pages, read once (on first use) from <see cref="DocsOptions"/>: shared by the pages routes and the
/// <c>docs</c> command, which list and show them the same way.
/// </summary>
public sealed class DocsPages
{
    private readonly DocsOptions _options;
    private readonly Lazy<(IReadOnlyList<DocsPage> All, Dictionary<string, DocsPage> BySlug)> _pages;

    /// <summary>Reads the pages of <paramref name="options"/>, folders relative to the host's content root.</summary>
    public DocsPages(DocsOptions options, IHostEnvironment? environment = null)
    {
        _options = options;
        var contentRoot = environment?.ContentRootPath ?? Directory.GetCurrentDirectory();
        _pages = new(() =>
        {
            var all = DocsPageLoader.Load(options, contentRoot);
            return (all, all.ToDictionary(p => p.Slug, StringComparer.Ordinal));
        });
    }

    /// <summary>Every page in page order. Throws <see cref="ArgumentException"/> for a page error.</summary>
    public IReadOnlyList<DocsPage> All => _pages.Value.All;

    /// <summary>The page with this slug, or <c>null</c>.</summary>
    public DocsPage? Find(string slug) => _pages.Value.BySlug.GetValueOrDefault(slug);

    /// <summary>
    /// The variables: the host's, <c>name</c>, and <c>baseUrl</c> (the configured base URL, else <paramref name="requestBaseUrl"/>,
    /// without a trailing <c>/</c>).
    /// </summary>
    public Dictionary<string, string> Variables(string requestBaseUrl) =>
        new(_options.Variables, StringComparer.Ordinal)
        {
            [DocsVariables.BaseUrl] = (_options.BaseUrl ?? requestBaseUrl).TrimEnd('/'),
            [DocsVariables.Name] = _options.Name,
        };

    /// <summary>The pages as listed (<c>GET /pages</c> and <c>docs list</c>).</summary>
    public List<DocsPageSummary> List(IReadOnlyDictionary<string, string> variables) =>
        All.Select(p => new DocsPageSummary(p.Slug, DocsVariables.Replace(p.Title, variables), p.Group, p.Order)).ToList();

    /// <summary>One page as served (<c>GET /pages/&lt;slug&gt;</c> and <c>docs read</c>).</summary>
    public static DocsPageView View(DocsPage page, IReadOnlyDictionary<string, string> variables) =>
        new(page.Slug, DocsVariables.Replace(page.Title, variables), page.Group, page.Order, DocsVariables.Replace(page.Markdown, variables));
}
