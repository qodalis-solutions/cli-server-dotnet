using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Helpers;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Admin.Commands;

public sealed partial class CliAdminCommandProcessor
{
    /// <summary>Who runs the command: the signed-in caller, else <c>anonymous</c> (a server without sign-in).</summary>
    private static DashboardMarketplaceActor TerminalActor(IServiceProvider services) =>
        services.GetService<IHttpContextAccessor>()?.HttpContext?.GetCliPrincipal() is { } principal
            ? new DashboardMarketplaceActor(principal.UserId, principal.UserName)
            : DashboardMarketplaceActor.Anonymous;

    /// <summary>Runs a marketplace call, turning its refusals into <c>Error: &lt;reason&gt;</c>.</summary>
    private static async Task<string> MarketplaceCall(Func<Task<string>> call)
    {
        try
        {
            return await call();
        }
        catch (DashboardMarketplaceException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private sealed class DashboardProcessor : CliCommandProcessor
    {
        private readonly DashboardListProcessor _list;

        public DashboardProcessor(DashboardMarketplace marketplace, IServiceProvider services)
        {
            _list = new DashboardListProcessor(marketplace);
            Processors =
            [
                _list,
                new DashboardSearchProcessor(marketplace),
                new DashboardInstallProcessor(marketplace, services),
                new DashboardUpdateProcessor(marketplace, services),
                new DashboardRemoveProcessor(marketplace, services),
            ];
        }

        public override string Command { get; set; } = "dashboard";
        public override string Description { get; set; } = "Dashboard plugins from the marketplace: list the installed ones, search, install, update, remove";
        public override string? Module { get; set; } = ModuleName;
        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } = [JsonParam];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
            _list.HandleAsync(command, cancellationToken);
    }

    private sealed class DashboardListProcessor(DashboardMarketplace marketplace) : CliCommandProcessor
    {
        public override string Command { get; set; } = "list";
        public override IEnumerable<string>? Aliases { get; set; } = ["ls"];
        public override string Description { get; set; } = "Lists the dashboard plugins installed from the marketplace: page id, package, version, who installed them";
        public override string? Module { get; set; } = ModuleName;
        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } = [JsonParam];

        public override async Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        {
            var plugins = await marketplace.InstalledAsync(checkUpdates: true, cancellationToken);
            if (Flag(command, "json")) return ToJson(plugins);
            if (plugins.Count == 0)
                return $"No dashboard plugins installed (marketplace {marketplace.Url}, install: {marketplace.Install}; admin dashboard search <query> finds some).";
            return $"{plugins.Count} dashboard plugin(s) from {marketplace.Url} (install: {marketplace.Install})\n" + AdminFormat.Table(
                ["ID", "Package", "Version", "Installed", "By"],
                plugins.Select(p => new[]
                {
                    p.Id, p.Name, $"{p.Version}{(p.UpdateAvailable == true ? " (update available)" : "")}",
                    AdminFormat.ShortTime(p.InstalledAt), string.IsNullOrEmpty(p.InstalledBy) ? "-" : p.InstalledBy,
                }).ToList());
        }
    }

    private sealed class DashboardSearchProcessor(DashboardMarketplace marketplace) : CliCommandProcessor
    {
        public override string Command { get; set; } = "search";
        public override string Description { get; set; } = "Searches the marketplace's dashboard plugins: package, version, publisher, whether it can be installed";
        public override string? Module { get; set; } = ModuleName;
        public override bool? AcceptsRawInput => true;
        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } = [JsonParam];
        public override IEnumerable<CliCommandExample>? Examples { get; set; } = [new("admin dashboard search tickets", "Dashboard plugins about tickets")];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) => MarketplaceCall(async () =>
        {
            var query = string.Join(' ', CliValueHelpers.GetPositionals(command));
            var result = await marketplace.SearchAsync(query, pageSize: 50, cancellationToken: cancellationToken);
            if (Flag(command, "json")) return result.ToJsonString(Json);
            var items = result["items"]!.AsArray().OfType<JsonObject>().ToList();
            if (items.Count == 0) return query.Length > 0 ? $"No dashboard plugins match \"{query}\"." : "The marketplace lists no dashboard plugins.";
            static string Status(JsonObject p)
            {
                string status;
                if (p["installed"] is JsonObject installed)
                {
                    var version = Text(installed["version"]);
                    status = p["updateAvailable"]?.GetValue<bool>() == true ? $"installed {version}, update available" : $"installed {version}";
                }
                else
                {
                    status = p["allowed"]?.GetValue<bool>() == true ? "installable" : $"blocked: {Text(p["blockedReason"])}";
                }
                if (p["requires"] is JsonObject requires && requires["installed"]?.GetValue<bool>() != true)
                    status += $"; needs server plugin {Text(requires["serverPackage"])}";
                return status;
            }
            var rows = items.Select(p => new[]
            {
                Text(p["name"])!,
                Text(p["version"]) ?? Text(p["latest"]) ?? "-",
                $"{Text(p["publisher"]?["name"]) ?? "-"}{(p["publisher"]?["verified"] is JsonValue v && v.TryGetValue<bool>(out var verified) && verified ? " (verified)" : "")}",
                Status(p),
            }).ToList();
            var table = AdminFormat.Table(["Package", "Version", "Publisher", "Status"], rows);
            var total = result["total"] is JsonValue t && t.TryGetValue<double>(out var n) ? n : items.Count;
            return total > items.Count ? $"{table}\n{items.Count} of {total} shown" : table;
        });
    }

    private sealed class DashboardInstallProcessor(DashboardMarketplace marketplace, IServiceProvider services) : CliCommandProcessor
    {
        public override string Command { get; set; } = "install";
        public override string Description { get; set; } = "Installs a dashboard plugin from the marketplace (the newest version this dashboard runs, or name@version)";
        public override string? Module { get; set; } = ModuleName;
        public override bool? ValueRequired { get; set; } = true;
        public override IEnumerable<string>? RequiredPermissions { get; set; } = [CliRoutePolicy.DashboardInstall];
        public override IEnumerable<CliCommandExample>? Examples { get; set; } =
        [
            new("admin dashboard install @qodalis/cli-dashboard-module-jobs", "The newest version"),
            new("admin dashboard install @acme/cli-dashboard-tickets@1.2.0", "A given version"),
        ];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) => MarketplaceCall(async () =>
        {
            var words = CliValueHelpers.GetPositionals(command);
            if (words.Count != 1) return "Error: name one package: admin dashboard install <name>[@version]";
            var spec = words[0];
            var at = spec.LastIndexOf('@');
            var (name, version) = at > 0 ? (spec[..at], spec[(at + 1)..]) : (spec, null);
            var plugin = await marketplace.InstallPluginAsync(name, version, TerminalActor(services), cancellationToken);
            return $"Installed {plugin.Name}@{plugin.Version}: the dashboard page \"{plugin.Title}\" (/x/{plugin.Id}).";
        });
    }

    private sealed class DashboardUpdateProcessor(DashboardMarketplace marketplace, IServiceProvider services) : CliCommandProcessor
    {
        public override string Command { get; set; } = "update";
        public override string Description { get; set; } = "Updates an installed dashboard plugin to the newest version this dashboard runs";
        public override string? Module { get; set; } = ModuleName;
        public override bool? ValueRequired { get; set; } = true;
        public override IEnumerable<string>? RequiredPermissions { get; set; } = [CliRoutePolicy.DashboardInstall];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) => MarketplaceCall(async () =>
        {
            var words = CliValueHelpers.GetPositionals(command);
            if (words.Count != 1) return "Error: name one package: admin dashboard update <name>";
            var result = await marketplace.UpdatePluginAsync(words[0], null, TerminalActor(services), cancellationToken);
            return result.Changed
                ? $"Updated {result.Plugin.Name} from {result.PreviousVersion} to {result.Plugin.Version}."
                : $"{result.Plugin.Name} is already at {result.Plugin.Version}, the newest version.";
        });
    }

    private sealed class DashboardRemoveProcessor(DashboardMarketplace marketplace, IServiceProvider services) : CliCommandProcessor
    {
        public override string Command { get; set; } = "remove";
        public override IEnumerable<string>? Aliases { get; set; } = ["uninstall"];
        public override string Description { get; set; } = "Removes an installed dashboard plugin: its page and its files";
        public override string? Module { get; set; } = ModuleName;
        public override bool? ValueRequired { get; set; } = true;
        public override IEnumerable<string>? RequiredPermissions { get; set; } = [CliRoutePolicy.DashboardInstall];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) => MarketplaceCall(async () =>
        {
            var words = CliValueHelpers.GetPositionals(command);
            if (words.Count != 1) return "Error: name one package: admin dashboard remove <name>";
            var plugin = await marketplace.RemovePluginAsync(words[0], TerminalActor(services));
            return $"Removed {plugin.Name}@{plugin.Version} (the page {plugin.Id}).";
        });
    }
}
