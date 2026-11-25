using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Config;

namespace Qodalis.Cli.Plugin.Portal;

/// <summary>
/// <c>portal</c> (= <c>portal status</c>) <c>[--json]</c>: the portal's path, site, sections and branding. Needs
/// <c>config:read</c> (it shows the portal's settings, as <c>config get portal</c> does); the portal itself stays public.
/// The branding is changed with <c>config set portal.branding.&lt;key&gt;</c>.
/// </summary>
public sealed class CliPortalCommandProcessor : CliCommandProcessor
{
    /// <summary>The module the command belongs to.</summary>
    public const string ModuleName = "portal";

    /// <summary>The last line of the status, and part of the command's description.</summary>
    public const string BrandingHint = "Change the branding with config set portal.branding.<key> <value> (name, logoUrl, themeMode, footerLinks).";

    private static readonly CliCommandParameterDescriptor JsonParam =
        new() { Name = "json", Description = "Print the status as JSON (portal.json and the site)", Type = CommandParameterType.Boolean };

    private readonly StatusProcessor _status;

    public CliPortalCommandProcessor(IServiceProvider services)
    {
        _status = new StatusProcessor(() => services.GetRequiredService<PortalState>());
        Processors = [_status];
    }

    public override string Command { get; set; } = "portal";
    public override string Description { get; set; } = $"Documentation portal: path, site, sections and branding. {BrandingHint}";
    public override string? Module { get; set; } = ModuleName;
    public override string? Icon { get; set; } = "🌐";
    public override IEnumerable<string>? RequiredPermissions { get; set; } = [CliConfigPermissions.Read];
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } = [JsonParam];

    public override IEnumerable<CliCommandExample>? Examples { get; set; } =
    [
        new("portal", "The portal at a glance"),
        new("portal --json", "portal.json and the site as JSON"),
        new("config set portal.branding.name \"RateBell API\"", "Rename the site (no restart)"),
        new("config set portal.branding.themeMode dark", "Open the site in the dark theme"),
    ];

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
        _status.HandleAsync(command, cancellationToken);

    private static bool Flag(CliProcessCommand command, string name) =>
        command.Args.TryGetValue(name, out var value) && value?.ToString() is "True" or "true" or "";

    /// <summary>The status as text: the mount, the site, the sections and the branding.</summary>
    public static string Format(JsonObject status)
    {
        var site = status["site"]!.AsObject();
        var lines = new List<string> { $"Portal: {(string?)site["path"]}" };
        var source = (string?)site["source"];
        if ((bool?)site["found"] == true && source != null)
        {
            var dir = (string?)site["dir"];
            lines.Add($"Site: found, {PortalSiteSources.Title(source)}{(dir != null ? $" ({dir})" : "")}");
        }
        else
        {
            lines.Add("Site: not found, the portal is not served (install @qodalis/cli-server-portal or set SitePath)");
        }

        var docs = status["docs"] as JsonObject;
        var api = status["api"] as JsonObject;
        if (docs == null && api == null)
        {
            lines.Add("Sections: none (the site shows \"Nothing published yet\")");
        }
        else
        {
            lines.Add("Sections:");
            if (docs != null)
            {
                var pageCount = (int?)docs["pageCount"];
                var count = pageCount == null ? "" : $", {pageCount} {(pageCount == 1 ? "page" : "pages")}";
                lines.Add($"  docs  {(string?)docs["title"]}{count} ({(string?)docs["pages"]})");
            }
            if (api != null) lines.Add($"  api   {(string?)api["title"]} ({(string?)api["openapi"]})");
        }

        var links = status["footerLinks"]!.AsArray();
        var linksText = links.Count > 0
            ? string.Join(", ", links.Select(l => $"{(string?)l!["label"]} ({(string?)l["url"]})"))
            : "(none)";
        var logo = (string?)status["logo"];
        var logoText = logo == null
            ? "(none)"
            : logo.StartsWith("data:", StringComparison.Ordinal)
                ? $"{logo[..(logo.IndexOf(',') + 1)]}… ({logo.Length} characters)"
                : logo;
        lines.AddRange(
        [
            "Branding:",
            $"  name         {(string?)status["name"]}",
            $"  logoUrl      {logoText}",
            $"  themeMode    {(string?)status["theme"]!["mode"]}",
            $"  footerLinks  {linksText}",
            "",
            BrandingHint,
        ]);
        return string.Join('\n', lines);
    }

    private sealed class StatusProcessor(Func<PortalState> state) : CliCommandProcessor
    {
        private static readonly JsonSerializerOptions Indented = new(PortalState.JsonOptions) { WriteIndented = true };

        public override string Command { get; set; } = "status";
        public override string Description { get; set; } = "The portal: its path, the site, the sections and the branding";
        public override string? Module { get; set; } = ModuleName;
        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } = [JsonParam];

        public override IEnumerable<CliCommandExample>? Examples { get; set; } =
        [
            new("portal status", "The portal at a glance"),
            new("portal status --json", "portal.json and the site as JSON"),
        ];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        {
            var status = state().Document(string.Empty, status: true);
            return Task.FromResult(Flag(command, "json") ? status.ToJsonString(Indented) : Format(status));
        }
    }
}

/// <summary>
/// The portal module: describes the <c>portal</c> command (registered on its own with dependency injection, joined by
/// its <see cref="ICliCommandProcessor.Module"/>) on the dashboard's plugins page and in <c>admin plugins</c>.
/// </summary>
public sealed class PortalModule : CliModule
{
    public override string Name => CliPortalCommandProcessor.ModuleName;

    public override string Version { get; } = Qodalis.Cli.Models.CliInstalledPlugin.FromAssembly(typeof(PortalModule).Assembly).Version ?? "1.0.0";

    public override string Description => "The documentation portal in the terminal: path, site, sections and branding";

    public override string? Icon => "🌐";

    public override IEnumerable<ICliCommandProcessor> Processors => [];
}
