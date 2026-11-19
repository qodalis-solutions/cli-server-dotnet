namespace Qodalis.Cli.Plugin.Docs;

/// <summary>
/// A markdown page: its slug (the file name without <c>.md</c>, lower-cased), title, group and order (from the front
/// matter or the defaults) and the markdown without the front matter. Variables (<c>{{ key }}</c>) are left as written
/// here and replaced per request.
/// </summary>
public sealed record DocsPage(string Slug, string Title, string Group, int Order, string Markdown);
