using System.Globalization;
using System.Text.Json;
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

    /// <summary>Runs a marketplace call, turning its refusals into <c>Error: &lt;reason&gt;</c> (and a line per setting refused).</summary>
    private static async Task<string> MarketplaceCall(Func<Task<string>> call)
    {
        try
        {
            return await call();
        }
        catch (DashboardMarketplaceException ex)
        {
            return string.Join('\n', new[] { $"Error: {ex.Message}" }.Concat((ex.Errors ?? new Dictionary<string, string>()).Select(e => $"  {e.Key}: {e.Value}")));
        }
    }

    private static readonly CliCommandParameterDescriptor ConfigParam = new()
    {
        Name = "config",
        Description = "The values of the plugin's settings, a JSON object: --config '{\"refreshSeconds\":60}'",
        Type = CommandParameterType.String,
    };

    /// <summary>
    /// <c>--config</c>: a JSON object (text, or already parsed by the client); null when absent. Throws 400 when it is
    /// not one.
    /// </summary>
    private static JsonObject? ConfigArg(CliProcessCommand command)
    {
        if (!command.Args.TryGetValue("config", out var raw) || raw == null) return null;
        JsonNode? value = null;
        try
        {
            value = raw switch
            {
                JsonElement { ValueKind: JsonValueKind.String } e => JsonNode.Parse(e.GetString()!),
                JsonElement e => JsonNode.Parse(e.GetRawText()),
                JsonNode node => node.DeepClone(),
                string text => JsonNode.Parse(text),
                _ => null,
            };
        }
        catch (JsonException)
        {
        }
        return value as JsonObject
            ?? throw new DashboardMarketplaceException(400, "invalid --config: use a JSON object, e.g. --config '{\"key\":\"value\"}'");
    }

    /// <summary>A setting's value as printed: strings as they are, anything else as JSON.</summary>
    private static string SettingText(JsonNode? value) => value switch
    {
        null => "(none)",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => value.ToJsonString(Compact),
    };

    private static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The warnings and <c>Its settings: admin dashboard info &lt;name&gt;</c>, after an install or update.</summary>
    private static IEnumerable<string> AfterChange(InstalledDashboardPlugin plugin) =>
        (plugin.Warnings ?? []).Select(w => $"Warning: {w}")
            .Concat(plugin.HasConfig ? [$"Its settings: admin dashboard info {plugin.Name}"] : []);

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
                new DashboardInfoProcessor(marketplace),
                new DashboardInstallProcessor(marketplace, services),
                new DashboardUpdateProcessor(marketplace, services),
                new DashboardRemoveProcessor(marketplace, services),
            ];
        }

        public override string Command { get; set; } = "dashboard";
        public override string Description { get; set; } = "Dashboard plugins from the marketplace: list the installed ones, search, info, install, update, remove";
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
                if (p["installed"] is JsonObject code && Text(code["source"]) == "code")
                {
                    var version = Text(code["version"]);
                    status = $"added in code{(version != null ? $" {version}" : "")}{(p["updateAvailable"]?.GetValue<bool>() == true ? ", newer version in the marketplace" : "")}";
                }
                else if (p["installed"] is JsonObject installed)
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
            new("admin dashboard install @acme/cli-dashboard-tickets --config '{\"refreshSeconds\":60}'", "With values for its settings"),
        ];

        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } = [ConfigParam];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) => MarketplaceCall(async () =>
        {
            var words = CliValueHelpers.GetPositionals(command);
            if (words.Count != 1) return "Error: name one package: admin dashboard install <name>[@version]";
            var spec = words[0];
            var at = spec.LastIndexOf('@');
            var (name, version) = at > 0 ? (spec[..at], spec[(at + 1)..]) : (spec, null);
            var plugin = await marketplace.InstallPluginAsync(name, version, TerminalActor(services), ConfigArg(command), cancellationToken);
            return string.Join('\n', new[] { $"Installed {plugin.Name}@{plugin.Version}: the dashboard page \"{plugin.Title}\" (/x/{plugin.Id})." }
                .Concat(AfterChange(plugin)));
        });
    }

    private sealed class DashboardUpdateProcessor(DashboardMarketplace marketplace, IServiceProvider services) : CliCommandProcessor
    {
        public override string Command { get; set; } = "update";
        public override string Description { get; set; } =
            "Updates an installed dashboard plugin to the newest version this dashboard runs (settings the new version still accepts are kept)";
        public override string? Module { get; set; } = ModuleName;
        public override bool? ValueRequired { get; set; } = true;
        public override IEnumerable<string>? RequiredPermissions { get; set; } = [CliRoutePolicy.DashboardInstall];
        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } = [ConfigParam];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) => MarketplaceCall(async () =>
        {
            var words = CliValueHelpers.GetPositionals(command);
            if (words.Count != 1) return "Error: name one package: admin dashboard update <name>";
            var config = ConfigArg(command);
            var result = await marketplace.UpdatePluginAsync(words[0], null, TerminalActor(services), config, cancellationToken);
            if (!result.Changed)
            {
                var applied = config is { Count: > 0 } ? " Its settings were changed." : "";
                return $"{result.Plugin.Name} is already at {result.Plugin.Version}, the newest version.{applied}";
            }
            var lines = new List<string> { $"Updated {result.Plugin.Name} from {result.PreviousVersion} to {result.Plugin.Version}." };
            if (result.ConfigDropped.Count > 0)
                lines.Add($"Settings dropped (the new version no longer accepts them): {string.Join(", ", result.ConfigDropped)}");
            lines.AddRange(AfterChange(result.Plugin));
            return string.Join('\n', lines);
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

    private sealed class DashboardInfoProcessor(DashboardMarketplace marketplace) : CliCommandProcessor
    {
        public override string Command { get; set; } = "info";
        public override string Description { get; set; } = "Shows an installed dashboard plugin: version, page, files, and its settings with their values";
        public override string? Module { get; set; } = ModuleName;
        public override bool? ValueRequired { get; set; } = true;
        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } = [JsonParam];
        public override IEnumerable<CliCommandExample>? Examples { get; set; } =
            [new("admin dashboard info @acme/cli-dashboard-tickets", "The plugin and its settings")];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) => MarketplaceCall(async () =>
        {
            var words = CliValueHelpers.GetPositionals(command);
            if (words.Count != 1) return "Error: name one package: admin dashboard info <name>";
            var info = await marketplace.PluginInfoAsync(words[0], cancellationToken);
            if (Flag(command, "json")) return JsonSerializer.Serialize(info, Compact);
            var p = info.Plugin;
            var lines = new List<string>
            {
                $"{p.Name}@{p.Version}",
                $"  Page        {p.Title} (/x/{p.Id})",
                $"  Installed   {AdminFormat.ShortTime(p.InstalledAt)}{(string.IsNullOrEmpty(p.InstalledBy) ? "" : $" by {p.InstalledBy}")}",
                $"  Files       {string.Join(", ", info.Files.Select(f => $"{f.File} ({f.Size} bytes)"))}",
            };
            if (info.Settings.Count == 0)
            {
                lines.Add("  Settings    none");
                return string.Join('\n', lines);
            }
            lines.Add($"Settings (config set dashboard-plugins.{p.Id}.<key> <value>; version {info.ConfigVersion}):");
            static string Rule(DashboardPluginSetting s)
            {
                var v = s.Validation;
                if (v == null) return "";
                return string.Join("; ", new[]
                {
                    v.Required == true ? "required" : "",
                    v.Min is { } min ? $"min {min.ToString(CultureInfo.InvariantCulture)}" : "",
                    v.Max is { } max ? $"max {max.ToString(CultureInfo.InvariantCulture)}" : "",
                    v.Pattern != null ? $"pattern {v.Pattern}" : "",
                    v.Options is { Count: > 0 } options ? $"one of {string.Join(", ", options)}" : "",
                }.Where(r => r.Length > 0));
            }
            lines.Add(AdminFormat.Table(["Key", "Value", "Type", "Default", "Rule"],
                info.Settings.Select(s => new[]
                {
                    s.Key, $"{SettingText(s.Value)}{(s.IsDefault ? "" : " *")}", s.Type, SettingText(s.Default), Rule(s) is { Length: > 0 } r ? r : "-",
                }).ToList()));
            if (info.Settings.Any(s => !s.IsDefault)) lines.Add("* changed from the default");
            return string.Join('\n', lines);
        });
    }
}
