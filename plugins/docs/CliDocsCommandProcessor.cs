using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Helpers;

namespace Qodalis.Cli.Plugin.Docs;

/// <summary>
/// <c>docs</c> (= <c>docs list</c>), <c>docs list [--group &lt;g&gt;] [--json]</c>, <c>docs read &lt;slug&gt; [--json]</c>
/// (aliases <c>show</c>, <c>cat</c>): public like the pages, no permission beyond <c>commands:execute</c>.
/// </summary>
public sealed class CliDocsCommandProcessor : CliCommandProcessor
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly CliCommandParameterDescriptor JsonParam =
        new() { Name = "json", Description = "Print the result as JSON", Type = CommandParameterType.Boolean };

    private readonly ListProcessor _list;

    public CliDocsCommandProcessor(IServiceProvider services)
    {
        // No request in a command: {{baseUrl}} is the configured base URL, else empty (links relative to the server root)
        DocsPages Pages() => services.GetRequiredService<DocsPages>();
        _list = new ListProcessor(Pages);
        Processors = [_list, new ReadProcessor(Pages)];
    }

    public override string Command { get; set; } = "docs";
    public override string Description { get; set; } = "Documentation pages: list them, read one as markdown";
    public override string? Module { get; set; } = "docs";
    public override string? Icon { get; set; } = "📖";

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
        _list.HandleAsync(command, cancellationToken);

    private static bool Flag(CliProcessCommand command, string name) =>
        command.Args.TryGetValue(name, out var value) && value?.ToString() is "True" or "true" or "";

    private sealed class ListProcessor(Func<DocsPages> pages) : CliCommandProcessor
    {
        public override string Command { get; set; } = "list";
        public override IEnumerable<string>? Aliases { get; set; } = ["ls"];
        public override string Description { get; set; } = "Lists the documentation pages by group: slug and title";
        public override string? Module { get; set; } = "docs";

        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
        [
            new CliCommandParameterDescriptor { Name = "group", Description = "Only the pages of this group", Type = CommandParameterType.String },
            JsonParam,
        ];

        public override IEnumerable<CliCommandExample>? Examples { get; set; } =
        [
            new("docs list", "Every page"),
            new("docs list --group Guides --json", "The pages of one group as JSON"),
        ];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        {
            string? group = null;
            if (command.Args.TryGetValue("group", out var raw))
            {
                group = raw?.ToString();
                if (group is null or "True" or "true" || group.Trim().Length == 0) return Task.FromResult("Error: --group needs a group name");
            }
            var docs = pages();
            var list = docs.List(docs.Variables(string.Empty));
            if (group != null) list = list.Where(p => string.Equals(p.Group, group, StringComparison.OrdinalIgnoreCase)).ToList();
            if (Flag(command, "json")) return Task.FromResult(JsonSerializer.Serialize(list, Json));
            if (list.Count == 0)
                return Task.FromResult(group != null ? $"No documentation pages in the group {group}." : "No documentation pages.");
            var width = list.Max(p => p.Slug.Length);
            // Grouped in the order each group first appears (the page order)
            var lines = new List<string>();
            foreach (var byGroup in list.GroupBy(p => p.Group))
            {
                if (lines.Count > 0) lines.Add(string.Empty);
                lines.Add(byGroup.Key);
                lines.AddRange(byGroup.Select(p => $"  {p.Slug.PadRight(width)}  {p.Title}"));
            }
            return Task.FromResult(string.Join('\n', lines));
        }
    }

    private sealed class ReadProcessor(Func<DocsPages> pages) : CliCommandProcessor
    {
        public override string Command { get; set; } = "read";
        public override IEnumerable<string>? Aliases { get; set; } = ["show", "cat"];
        public override string Description { get; set; } = "Prints a documentation page as markdown, variables replaced";
        public override string? Module { get; set; } = "docs";
        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } = [JsonParam];
        public override IEnumerable<CliCommandExample>? Examples { get; set; } = [new("docs read getting-started", "The page getting-started")];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        {
            var words = CliValueHelpers.GetPositionals(command);
            if (words.Count == 0) return Task.FromResult("Error: name the page: docs read <slug> (docs list shows them)");
            if (words.Count > 1) return Task.FromResult("Error: one page at a time: docs read <slug>");
            var docs = pages();
            if (docs.Find(words[0]) is not { } page)
                return Task.FromResult($"Error: page not found: {words[0]} (docs list shows the pages)");
            var view = DocsPages.View(page, docs.Variables(string.Empty));
            if (Flag(command, "json")) return Task.FromResult(JsonSerializer.Serialize(view, Json));
            var markdown = view.Markdown.Trim();
            // The title lives in the front matter; shown as a heading unless the page starts with its own
            return Task.FromResult(markdown.StartsWith('#') ? markdown : $"# {view.Title}{(markdown.Length > 0 ? $"\n\n{markdown}" : "")}");
        }
    }
}
