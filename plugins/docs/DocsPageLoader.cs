using System.Globalization;
using System.Text.RegularExpressions;

namespace Qodalis.Cli.Plugin.Docs;

/// <summary>
/// Reads pages: folders of <c>*.md</c> files and pages given in code, with the front matter parsed line by line
/// (<c>key: value</c>; <c>title</c>, <c>group</c>, <c>order</c>; no YAML library).
/// </summary>
internal static partial class DocsPageLoader
{
    public const string DefaultGroup = "Guides";

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SlugRegex();

    public static bool IsValidSlug(string? slug) => slug != null && SlugRegex().IsMatch(slug);

    /// <summary>Every page of <paramref name="options"/>, in page order; throws <see cref="ArgumentException"/> on any C9 error.</summary>
    public static IReadOnlyList<DocsPage> Load(DocsOptions options, string contentRoot)
    {
        var pages = new List<DocsPage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(DocsPage page)
        {
            if (!seen.Add(page.Slug)) throw new ArgumentException($"duplicate docs page: {page.Slug}");
            pages.Add(page);
        }

        foreach (var source in options.Sources)
        {
            if (source.Folder == null)
            {
                Add(Parse(source.Slug!, source.Markdown!));
                continue;
            }

            var folder = Path.GetFullPath(Path.Combine(contentRoot, source.Folder));
            if (!Directory.Exists(folder)) throw new ArgumentException($"docs folder not found: {source.Folder}");
            foreach (var file in Directory.EnumerateFiles(folder).OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                if (!name.EndsWith(".md", StringComparison.Ordinal) || name.StartsWith('.') || name.StartsWith('_')) continue;
                var slug = name[..^".md".Length].ToLowerInvariant();
                if (!IsValidSlug(slug)) throw new ArgumentException($"invalid docs page name: {name}");
                Add(Parse(slug, File.ReadAllText(file)));
            }
        }

        return Sort(pages);
    }

    /// <summary>Page order: <c>order</c>, then <c>title</c> (ordinal, case-insensitive), then <c>slug</c>.</summary>
    public static IReadOnlyList<DocsPage> Sort(IEnumerable<DocsPage> pages) => pages
        .OrderBy(p => p.Order)
        .ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
        .ThenBy(p => p.Slug, StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// Parses one page. Front matter only when the first line is exactly <c>---</c>, up to the next line that is
    /// exactly <c>---</c> (without one, the whole text is the body). A line ending's <c>\r</c> is not part of the line.
    /// </summary>
    public static DocsPage Parse(string slug, string text)
    {
        string? title = null, group = null;
        var order = 0;
        var body = text;

        var lines = SplitLines(text);
        if (lines.Count > 0 && lines[0].Line == "---")
        {
            var close = lines.FindIndex(1, l => l.Line == "---");
            if (close > 0)
            {
                foreach (var (line, _) in lines.Skip(1).Take(close - 1))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    var key = line[..colon].Trim();
                    var value = Unquote(line[(colon + 1)..].Trim());
                    switch (key)
                    {
                        case "title":
                            title = value;
                            break;
                        case "group":
                            group = value;
                            break;
                        case "order":
                            if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out order))
                                throw new ArgumentException($"invalid order in docs page: {slug}");
                            break;
                    }
                }
                body = text[lines[close].End..];
            }
        }

        if (string.IsNullOrEmpty(title))
        {
            title = SplitLines(body).Select(l => l.Line).FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim();
            if (string.IsNullOrEmpty(title)) title = slug;
        }

        return new DocsPage(slug, title, string.IsNullOrEmpty(group) ? DefaultGroup : group, order, body);
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0] ? value[1..^1] : value;

    /// <summary>Lines without their ending, each with the offset just after its ending.</summary>
    private static List<(string Line, int End)> SplitLines(string text)
    {
        var lines = new List<(string, int)>();
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline + 1;
            var line = text[start..(newline < 0 ? text.Length : newline)];
            if (line.EndsWith('\r')) line = line[..^1];
            lines.Add((line, end));
            start = end;
        }
        return lines;
    }
}
