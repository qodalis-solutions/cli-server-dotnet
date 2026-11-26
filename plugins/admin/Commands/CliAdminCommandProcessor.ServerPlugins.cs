using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Helpers;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Admin.Commands;

// admin plugins search|installed|install|update|remove: server plugins from the marketplace (NuGet packages loaded at the next start)
public sealed partial class CliAdminCommandProcessor
{
    private static ICliCommandProcessor[] ServerPluginProcessors(ServerPluginMarketplace marketplace, IServiceProvider services) =>
    [
        new ServerPluginsSearchProcessor(marketplace),
        new ServerPluginsInstalledProcessor(marketplace),
        new ServerPluginsInstallProcessor(marketplace, services),
        new ServerPluginsUpdateProcessor(marketplace, services),
        new ServerPluginsRemoveProcessor(marketplace, services),
    ];

    private static (string Name, string? Version) PluginSpec(string text)
    {
        var at = text.LastIndexOf('@');
        return at > 0 ? (text[..at], text[(at + 1)..]) : (text, null);
    }

    private static string RestartLine(InstalledServerPlugin r) => r.RestartRequired == true
        ? "Restart the server to apply it: admin restart (or the dashboard)."
        : "No restart needed: the server already runs this.";

    private static string? Str(JsonNode? node, string key) => Text(node?[key]);

    private sealed class ServerPluginsSearchProcessor(ServerPluginMarketplace marketplace) : CliCommandProcessor
    {
        public override string Command { get; set; } = "search";
        public override string Description { get; set; } =
            "Searches the marketplace's server plugins: this server's package, version, platforms, whether it can be installed";
        public override string? Module { get; set; } = ModuleName;
        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
        [
            new CliCommandParameterDescriptor
            {
                Name = "platform",
                Description = $"Whose plugins: {string.Join(", ", ServerPluginMarketplace.PlatformFilters)} (default this: the ones for this server)",
                Type = CommandParameterType.String,
            },
            JsonParam,
        ];
        public override IEnumerable<CliCommandExample>? Examples { get; set; } =
        [
            new("admin plugins search jobs", "Server plugins about jobs"),
            new("admin plugins search --platform=all", "Every server plugin, also the ones for other platforms"),
        ];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) => MarketplaceCall(async () =>
        {
            var query = string.Join(' ', CliValueHelpers.GetPositionals(command));
            var platform = command.Args.TryGetValue("platform", out var raw) && raw != null ? raw.ToString() : null;
            var result = await marketplace.SearchAsync(query, pageSize: 50, platform: platform, cancellationToken: cancellationToken);
            if (Flag(command, "json")) return result.ToJsonString(Json);
            var items = (result["items"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            if (items.Count == 0) return query.Length > 0 ? $"No server plugins match \"{query}\"." : "The marketplace lists no server plugins.";
            var rows = items.Select(p =>
            {
                var platform = p["platform"] as JsonObject;
                var installed = Str(platform, "installed");
                var update = p["updateAvailable"] is JsonValue u && u.TryGetValue<bool>(out var b) && b;
                var allowed = p["allowed"] is JsonValue a && a.TryGetValue<bool>(out var ok) && ok;
                var code = p["installed"] is JsonObject i && Str(i, "source") == "code" ? i : null;
                var status = code != null
                    ? $"added in code{(Str(code, "version") is { } cv ? $" {cv}" : "")}{(update ? ", newer version in the marketplace" : "")}"
                    : installed != null
                    ? update ? $"installed {installed}, update available" : $"installed {installed}"
                    : allowed ? "installable" : $"blocked: {Str(p, "blockedReason")}{(Str(p, "autoInstallReason") is { } why ? $" ({why})" : "")}";
                var platforms = (platform != null ? new[] { ServerPluginMarketplace.Platform } : [])
                    .Concat((p["otherPlatforms"] as JsonArray)?.Select(Text).OfType<string>() ?? []);
                var publisher = p["publisher"] as JsonObject;
                var verified = publisher?["verified"] is JsonValue v && v.TryGetValue<bool>(out var yes) && yes;
                return new[]
                {
                    Str(p, "name") ?? "-",
                    Str(platform, "package") ?? $"(not for {ServerPluginMarketplace.Platform})",
                    Str(platform, "latest") ?? "-",
                    string.Join(", ", platforms),
                    $"{Str(publisher, "name") ?? "-"}{(verified ? " (verified)" : "")}",
                    status,
                };
            }).ToList();
            var total = result["total"] is JsonValue t && t.TryGetValue<int>(out var n) ? n : items.Count;
            var table = AdminFormat.Table(["Plugin", "Package", "Version", "Platforms", "Publisher", "Status"], rows);
            return total > items.Count ? $"{table}\n{items.Count} of {total} shown" : table;
        });
    }

    private sealed class ServerPluginsInstalledProcessor(ServerPluginMarketplace marketplace) : CliCommandProcessor
    {
        public override string Command { get; set; } = "installed";
        public override string Description { get; set; } =
            "Lists the server plugins installed from the marketplace: package, version, status (loaded, pending restart, failed)";
        public override string? Module { get; set; } = ModuleName;
        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } = [JsonParam];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) => MarketplaceCall(async () =>
        {
            var plugins = await marketplace.InstalledAsync(checkUpdates: true, cancellationToken);
            if (Flag(command, "json")) return ToJson(plugins);
            if (plugins.Count == 0)
                return $"No server plugins installed from the marketplace ({marketplace.Url}, install: {marketplace.Install}; admin plugins search <query> finds some).";
            static string Status(InstalledServerPlugin p) => p.Status switch
            {
                "pending" => "pending restart",
                "removed" => "removed, running until a restart",
                _ => p.Error != null ? $"{p.Status}: {p.Error}" : p.Status,
            };
            return $"{plugins.Count} server plugin(s) from {marketplace.Url} (install: {marketplace.Install})\n" + AdminFormat.Table(
                ["Plugin", "Package", "Version", "Status", "Installed", "By"],
                plugins.Select(p => new[]
                {
                    p.Name, p.Package, $"{p.Version}{(p.UpdateAvailable == true ? " (update available)" : "")}", Status(p),
                    string.IsNullOrEmpty(p.InstalledAt) ? "-" : AdminFormat.ShortTime(p.InstalledAt),
                    string.IsNullOrEmpty(p.InstalledBy) ? "-" : p.InstalledBy,
                }).ToList());
        });
    }

    private sealed class ServerPluginsInstallProcessor(ServerPluginMarketplace marketplace, IServiceProvider services) : CliCommandProcessor
    {
        public override string Command { get; set; } = "install";
        public override string Description { get; set; } =
            "Installs a server plugin from the marketplace (this server's package, the newest compatible version or name@version); runs after a restart";
        public override string? Module { get; set; } = ModuleName;
        public override bool? ValueRequired { get; set; } = true;
        public override IEnumerable<string>? RequiredPermissions { get; set; } = [CliRoutePolicy.PluginsInstall];
        public override IEnumerable<CliCommandExample>? Examples { get; set; } =
        [
            new("admin plugins install weather", "The newest compatible version"),
            new("admin plugins install jobs@1.0.0-beta.31", "A given version"),
        ];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) => MarketplaceCall(async () =>
        {
            var words = CliValueHelpers.GetPositionals(command);
            if (words.Count != 1) return "Error: name one plugin: admin plugins install <name>[@version]";
            var (name, version) = PluginSpec(words[0]);
            var r = await marketplace.InstallPluginAsync(name, version, TerminalActor(services), cancellationToken);
            return $"Installed {r.Name}: {r.Package}@{r.Version}.\n{RestartLine(r)}";
        });
    }

    private sealed class ServerPluginsUpdateProcessor(ServerPluginMarketplace marketplace, IServiceProvider services) : CliCommandProcessor
    {
        public override string Command { get; set; } = "update";
        public override string Description { get; set; } =
            "Updates an installed server plugin to the newest compatible version (or name@version); runs after a restart";
        public override string? Module { get; set; } = ModuleName;
        public override bool? ValueRequired { get; set; } = true;
        public override IEnumerable<string>? RequiredPermissions { get; set; } = [CliRoutePolicy.PluginsInstall];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) => MarketplaceCall(async () =>
        {
            var words = CliValueHelpers.GetPositionals(command);
            if (words.Count != 1) return "Error: name one plugin: admin plugins update <name>[@version]";
            var (name, version) = PluginSpec(words[0]);
            var r = await marketplace.UpdatePluginAsync(name, version, TerminalActor(services), cancellationToken);
            if (r.Changed == false) return $"{r.Name} is already at {r.Version}.";
            return $"Updated {r.Name} from {r.PreviousVersion} to {r.Version}.\n{RestartLine(r)}";
        });
    }

    private sealed class ServerPluginsRemoveProcessor(ServerPluginMarketplace marketplace, IServiceProvider services) : CliCommandProcessor
    {
        public override string Command { get; set; } = "remove";
        public override IEnumerable<string>? Aliases { get; set; } = ["uninstall"];
        public override string Description { get; set; } = "Removes a server plugin installed from the marketplace; it runs until a restart";
        public override string? Module { get; set; } = ModuleName;
        public override bool? ValueRequired { get; set; } = true;
        public override IEnumerable<string>? RequiredPermissions { get; set; } = [CliRoutePolicy.PluginsInstall];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) => MarketplaceCall(async () =>
        {
            var words = CliValueHelpers.GetPositionals(command);
            if (words.Count != 1) return "Error: name one plugin: admin plugins remove <name>";
            var r = await marketplace.RemovePluginAsync(words[0], TerminalActor(services));
            return $"Removed {r.Name} ({r.Package}@{r.Version}).\n{RestartLine(r)}";
        });
    }
}
