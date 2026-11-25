using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Helpers;
using Qodalis.Cli.Abstractions.Logs;
using Qodalis.Cli.Models;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Admin.Commands;

/// <summary>
/// <c>admin</c>: <c>status [--history]</c>, <c>plugins [list|enable|disable &lt;id&gt;]</c>, <c>clients</c>,
/// <c>logs [...]</c>. Needs <c>admin:access</c> on a protected server; <c>logs</c> also <c>logs:read</c>. Server
/// settings are the core <c>config</c> command. Reads the services the admin routes use.
/// </summary>
public sealed class CliAdminCommandProcessor : CliCommandProcessor
{
    /// <summary>The module of the <c>admin</c> command (<c>admin plugins disable</c> refuses it: that would disable this command).</summary>
    public const string ModuleName = "admin";

    private readonly StatusProcessor _status;

    public CliAdminCommandProcessor(IServiceProvider services)
    {
        _status = new StatusProcessor(services);
        Processors =
        [
            _status,
            new PluginsProcessor(services),
            new ClientsProcessor(services),
            new LogsProcessor(services),
        ];
    }

    public override string Command { get; set; } = "admin";
    public override string Description { get; set; } = "Server administration: status, command modules, WebSocket clients, logs";
    public override string? Module { get; set; } = ModuleName;
    public override string? Icon { get; set; } = "🛠️";
    public override IEnumerable<string>? RequiredPermissions { get; set; } = [CliRoutePolicy.AdminAccess];

    /// <summary><c>admin</c> alone: the status.</summary>
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
        _status.HandleAsync(command, cancellationToken);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly CliCommandParameterDescriptor JsonParam =
        new() { Name = "json", Description = "Print the result as JSON", Type = CommandParameterType.Boolean };

    private static string ToJson(object? value) => JsonSerializer.Serialize(value, Json);

    private static bool Has(CliProcessCommand command, string name) => command.Args.TryGetValue(name, out var value) && value != null;

    private static bool Flag(CliProcessCommand command, string name) =>
        command.Args.TryGetValue(name, out var value) && value?.ToString() is "True" or "true" or "";

    private static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A whole number flag from 1 to <paramref name="max"/>: <paramref name="fallback"/> when absent, an error text when invalid.</summary>
    private static (int Value, string? Error) IntArg(CliProcessCommand command, string[] names, int fallback, int max, string label)
    {
        foreach (var name in names)
        {
            if (!command.Args.TryGetValue(name, out var raw) || raw == null) continue;
            return int.TryParse(raw.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= max
                ? (n, null)
                : (0, $"Error: {label} must be a whole number from 1 to {max}");
        }
        return (fallback, null);
    }

    private sealed class StatusProcessor(IServiceProvider services) : CliCommandProcessor
    {
        public override string Command { get; set; } = "status";
        public override string Description { get; set; } =
            "Shows the server status: version, uptime, platform, memory, CPU, connections; --history summarizes the recent samples";
        public override string? Module { get; set; } = ModuleName;

        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
        [
            new CliCommandParameterDescriptor
            {
                Name = "history",
                Description = "Summarize the samples of the last minutes instead (count, min/avg/max memory and CPU)",
                Type = CommandParameterType.Boolean,
            },
            new CliCommandParameterDescriptor
            {
                Name = "minutes",
                Description = $"With --history: how many minutes back (1-{StatusHistory.MaxHistoryMinutes}, default {StatusHistory.DefaultHistoryMinutes})",
                Type = CommandParameterType.Number,
            },
            JsonParam,
        ];

        public override IEnumerable<CliCommandExample>? Examples { get; set; } =
        [
            new("admin status", "The status now"),
            new("admin status --history --minutes 15", "Memory and CPU over the last 15 minutes"),
        ];

        public override async Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        {
            if (Flag(command, "history")) return History(command);
            if (Has(command, "minutes")) return "Error: --minutes goes with --history";
            var status = await services.GetRequiredService<ServerStatus>().SnapshotAsync(cancellationToken);
            var plugins = services.GetService<CliInstalledPlugins>()?.All ?? [];
            var server = plugins.FirstOrDefault(p => p.Id == "Qodalis.Cli") ?? plugins.FirstOrDefault();
            if (Flag(command, "json"))
            {
                var body = new JsonObject
                {
                    ["server"] = server == null ? null : new JsonObject { ["id"] = server.Id, ["version"] = server.Version },
                };
                foreach (var (key, value) in JsonSerializer.SerializeToNode(status, Json)!.AsObject().ToList())
                    body[key] = value?.DeepClone();
                foreach (var key in new[] { "cpuUsagePercent", "systemMemoryTotalMb", "systemMemoryUsedMb", "memoryLimited" })
                {
                    if (!body.ContainsKey(key)) body[key] = null;
                }
                return body.ToJsonString(Json);
            }
            var rows = new (string Key, string Value)[]
            {
                ("Server", server == null ? "unknown" : $"{server.Id}{(server.Version != null ? $" {server.Version}" : "")}"),
                ("Platform", $"{status.Platform} {status.PlatformVersion} ({status.Os})"),
                ("Uptime", $"{AdminFormat.Duration(status.UptimeSeconds)} (since {AdminFormat.ShortTime(status.StartedAt)})"),
                ("Memory", AdminFormat.MemoryLine(status)),
                ("CPU", status.CpuUsagePercent is { } cpu ? $"{Number(cpu)}%" : "n/a"),
                ("Connections", $"{status.ActiveWsConnections} WebSocket, {status.ActiveShellSessions} shell"),
                ("Commands", status.RegisteredCommands.ToString(CultureInfo.InvariantCulture)),
                ("Jobs", status.RegisteredJobs.ToString(CultureInfo.InvariantCulture)),
                ("Features", status.EnabledFeatures.Count > 0 ? string.Join(", ", status.EnabledFeatures) : "none"),
            };
            return string.Join('\n', rows.Select(r => $"{r.Key,-12}{r.Value}"));
        }

        private string History(CliProcessCommand command)
        {
            var (minutes, error) = IntArg(command, ["minutes"], StatusHistory.DefaultHistoryMinutes, StatusHistory.MaxHistoryMinutes, "--minutes");
            if (error != null) return error;
            var samples = services.GetService<StatusHistory>()?.GetSamples(minutes) ?? [];
            var summary = StatusHistorySummary.Of(samples);
            var interval = (int)StatusHistory.Interval.TotalSeconds;
            if (Flag(command, "json"))
            {
                var body = new JsonObject { ["minutes"] = minutes, ["intervalSeconds"] = interval };
                foreach (var (key, value) in JsonSerializer.SerializeToNode(summary, Json)!.AsObject().ToList())
                    body[key] = value?.DeepClone();
                return body.ToJsonString(Json);
            }
            if (summary.Count == 0) return $"No status samples in the last {minutes} min.";
            static string Range(StatusRange r, string unit) =>
                $"min {Number(r.Min)}{unit}  avg {Number(r.Avg)}{unit}  max {Number(r.Max)}{unit}";
            return string.Join('\n',
                $"Last {minutes} min: {summary.Count} sample(s) every {interval} s, {AdminFormat.ShortTime(summary.From!)} to {AdminFormat.ShortTime(summary.To!)}",
                $"{"Memory",-12}{Range(summary.MemoryMb!, " MB")}",
                $"{"CPU",-12}{(summary.CpuPercent is { } cpu ? Range(cpu, "%") : "n/a")}",
                $"{"Peak",-12}{summary.MaxWsConnections} WebSocket, {summary.MaxShellSessions} shell");
        }
    }

    private sealed class PluginsProcessor : CliCommandProcessor
    {
        private readonly PluginsListProcessor _list;

        public PluginsProcessor(IServiceProvider services)
        {
            _list = new PluginsListProcessor(services);
            Processors = [_list, new PluginsSwitchProcessor(services, true), new PluginsSwitchProcessor(services, false)];
        }

        public override string Command { get; set; } = "plugins";
        public override IEnumerable<string>? Aliases { get; set; } = ["modules"];
        public override string Description { get; set; } = "Command modules: list them, enable or disable one";
        public override string? Module { get; set; } = ModuleName;
        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } = [JsonParam];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
            _list.HandleAsync(command, cancellationToken);
    }

    private sealed class PluginsListProcessor(IServiceProvider services) : CliCommandProcessor
    {
        public override string Command { get; set; } = "list";
        public override IEnumerable<string>? Aliases { get; set; } = ["ls"];
        public override string Description { get; set; } = "Lists the command modules: id, name, version, enabled or disabled, commands";
        public override string? Module { get; set; } = ModuleName;
        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } = [JsonParam];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        {
            var modules = services.GetRequiredService<ModuleRegistry>().List();
            if (Flag(command, "json")) return Task.FromResult(ToJson(modules));
            if (modules.Count == 0) return Task.FromResult("No command modules registered.");
            return Task.FromResult(AdminFormat.Table(["ID", "Name", "Version", "Status", "Commands"], modules.Select(m => new[]
            {
                m.Id, m.Name, string.IsNullOrEmpty(m.Version) ? "-" : m.Version, m.Enabled ? "enabled" : "disabled",
                m.ProcessorCount.ToString(CultureInfo.InvariantCulture),
            }).ToList()));
        }
    }

    private sealed class PluginsSwitchProcessor : CliCommandProcessor
    {
        private readonly IServiceProvider _services;
        private readonly bool _enable;

        public PluginsSwitchProcessor(IServiceProvider services, bool enable)
        {
            _services = services;
            _enable = enable;
            Command = enable ? "enable" : "disable";
            Description = enable
                ? "Enables a command module (its commands run again)"
                : "Disables a command module: its commands are refused until it is enabled";
            Examples = [new($"admin plugins {Command} weather", $"{(enable ? "Enable" : "Disable")} the weather module")];
        }

        public override string Command { get; set; }
        public override string Description { get; set; }
        public override bool? ValueRequired { get; set; } = true;
        public override string? Module { get; set; } = ModuleName;

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        {
            var words = CliValueHelpers.GetPositionals(command);
            if (words.Count != 1)
                return Task.FromResult($"Error: name one module: admin plugins {Command} <id> (admin plugins lists them)");
            var registry = _services.GetRequiredService<ModuleRegistry>();
            var id = words[0];
            if (registry.GetById(id) is not { } target) return Task.FromResult($"Error: unknown module: {id} (admin plugins lists them)");
            if (!_enable && string.Equals(target.Id, ModuleName, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult("Error: the admin module cannot be disabled from the terminal: it would disable this command");
            var (module, changed) = registry.SetEnabled(id, _enable)!.Value;
            if (!changed) return Task.FromResult($"{module.Id} is already {(_enable ? "enabled" : "disabled")}.");
            var n = module.ProcessorCount;
            return Task.FromResult($"{(_enable ? "Enabled" : "Disabled")} {module.Id} ({n} command{(n == 1 ? "" : "s")}).");
        }
    }

    private sealed class ClientsProcessor(IServiceProvider services) : CliCommandProcessor
    {
        public override string Command { get; set; } = "clients";
        public override string Description { get; set; } = "Lists the connected WebSocket clients: id, type, connected since, address";
        public override string? Module { get; set; } = ModuleName;
        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } = [JsonParam];

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        {
            var clients = services.GetRequiredService<ICliEventSocketManager>().GetClients();
            if (Flag(command, "json")) return Task.FromResult(ToJson(clients));
            if (clients.Count == 0) return Task.FromResult("No WebSocket clients connected.");
            return Task.FromResult($"{clients.Count} client(s)\n" + AdminFormat.Table(["ID", "Type", "Connected", "Address"],
                clients.Select(c => new[] { c.Id, c.Type, AdminFormat.ShortTime(c.ConnectedAt), string.IsNullOrEmpty(c.RemoteAddress) ? "-" : c.RemoteAddress })
                    .ToList()));
        }
    }

    private sealed class LogsProcessor(IServiceProvider services) : CliCommandProcessor
    {
        private const int MaxLines = 1000;
        private static readonly HashSet<string> LevelNames = ["DEBUG", "INFO", "WARN", "ERROR"];

        public override string Command { get; set; } = "logs";
        public override string Description { get; set; } =
            "Shows the latest server log entries (oldest first), filtered by level, text, service and time";
        public override string? Module { get; set; } = ModuleName;
        public override IEnumerable<string>? RequiredPermissions { get; set; } = [CliRoutePolicy.LogsRead];

        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
        [
            new CliCommandParameterDescriptor { Name = "level", Description = "Only these levels, comma-separated: debug, info, warn, error", Type = CommandParameterType.String },
            new CliCommandParameterDescriptor { Name = "search", Description = "Only entries with this text in the message or the source (case-insensitive)", Type = CommandParameterType.String },
            new CliCommandParameterDescriptor { Name = "service", Description = "Only entries of this service (a persistent log store such as Seq)", Type = CommandParameterType.String },
            new CliCommandParameterDescriptor { Name = "since", Description = "Only entries since a duration ago (30s, 5m, 1h, 7d) or an ISO 8601 date", Type = CommandParameterType.String },
            new CliCommandParameterDescriptor
            {
                Name = "limit",
                Aliases = ["n"],
                Description = $"How many entries (1-{MaxLines}, default 50)",
                Type = CommandParameterType.Number,
                DefaultValue = 50,
            },
            JsonParam,
        ];

        public override IEnumerable<CliCommandExample>? Examples { get; set; } =
        [
            new("admin logs", "The last 50 entries"),
            new("admin logs --level warn,error --since 1h", "Warnings and errors of the last hour"),
            new("admin logs --search timeout -n 200", "The last 200 entries mentioning timeout"),
        ];

        public override async Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        {
            var (limit, limitError) = IntArg(command, ["limit", "n"], 50, MaxLines, "--limit");
            if (limitError != null) return limitError;
            var texts = new Dictionary<string, string?>();
            foreach (var name in new[] { "level", "search", "service", "since" })
            {
                if (!command.Args.TryGetValue(name, out var raw) || raw == null) continue;
                var text = raw.ToString();
                if (text is null or "True" or "true" || text.Trim().Length == 0) return $"Error: --{name} needs a value";
                texts[name] = text.Trim();
            }
            var levels = new List<string>();
            if (texts.GetValueOrDefault("level") is { } level)
            {
                var names = level.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.ToUpperInvariant() == "WARNING" ? "WARN" : l.ToUpperInvariant()).ToList();
                var bad = names.Where(l => !LevelNames.Contains(l)).ToList();
                if (bad.Count > 0) return $"Error: unknown level {string.Join(", ", bad.Select(b => b.ToLowerInvariant()))} (debug, info, warn, error)";
                levels = names.Distinct().ToList();
            }
            DateTimeOffset? since = null;
            if (texts.GetValueOrDefault("since") is { } sinceText)
            {
                try
                {
                    since = AdminQueryTime.ParseSince(sinceText, "--since", (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow());
                }
                catch (AdminQueryException ex)
                {
                    return $"Error: {ex.Message}";
                }
            }

            var store = services.GetRequiredService<ILogStore>();
            // The newest `limit` entries (the store answers newest first, at most MaxLimit a page), printed oldest first like tail
            var entries = new List<LogRecord>();
            int? total = null;
            try
            {
                while (entries.Count < limit)
                {
                    var page = await store.QueryAsync(new LogQueryOptions
                    {
                        Levels = levels,
                        Search = texts.GetValueOrDefault("search"),
                        Service = texts.GetValueOrDefault("service"),
                        From = since,
                        Limit = Math.Min(LogQueryOptions.MaxLimit, limit - entries.Count),
                        Offset = entries.Count,
                    }, cancellationToken);
                    total ??= page.Total;
                    entries.AddRange(page.Entries);
                    if (page.Entries.Count == 0 || !page.HasMore) break;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException
                || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                return $"Error: the log store ({store.Name}) is unavailable: {ex.Message}";
            }
            entries.Reverse();
            var count = total ?? entries.Count;
            if (Flag(command, "json")) return ToJson(new { entries = entries.Select(AdminLogView.Of), total = count });
            if (entries.Count == 0) return "No log entries match.";
            var lines = new List<string>();
            if (count > entries.Count) lines.Add($"Last {entries.Count} of {count} entries");
            lines.AddRange(entries.Select(AdminFormat.LogLine));
            return string.Join('\n', lines);
        }
    }
}

/// <summary>A log entry as <c>GET /api/v1/qcli/logs</c> and <c>admin logs --json</c> write it.</summary>
public sealed record AdminLogView(string Timestamp, string Level, string Message, string Source, string? Service, string? Exception)
{
    public static AdminLogView Of(LogRecord e) =>
        new(e.Timestamp.UtcDateTime.ToString("o"), e.Level, e.Message, e.Source, e.Service, e.Exception);
}

/// <summary>Text helpers of the <c>admin</c> command.</summary>
public static partial class AdminFormat
{
    [GeneratedRegex(@"(\.\d+)?Z$")]
    private static partial Regex FractionRegex();

    /// <summary><c>2026-11-25T10:04:05.123Z</c> -> <c>2026-11-25 10:04:05</c> (any ISO 8601 time, shown in UTC).</summary>
    public static string ShortTime(string iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? ShortTime(time)
            : FractionRegex().Replace(iso.Replace('T', ' '), string.Empty);

    /// <inheritdoc cref="ShortTime(string)"/>
    public static string ShortTime(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>7384 -> <c>2 h 3 min</c>; under a minute <c>42 s</c>; from a day <c>3 d 4 h</c>.</summary>
    public static string Duration(long seconds)
    {
        var s = Math.Max(0, seconds);
        if (s < 60) return $"{s} s";
        var d = s / 86_400;
        var h = s % 86_400 / 3600;
        var m = s % 3600 / 60;
        if (d > 0) return $"{d} d {h} h";
        return h > 0 ? $"{h} h {m} min" : $"{m} min";
    }

    /// <summary>Aligned columns: every column but the last padded to its widest cell.</summary>
    public static string Table(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        var widths = headers.Select((h, i) => rows.Select(r => i < r.Length ? r[i].Length : 0).Append(h.Length).Max()).ToArray();
        string Line(IReadOnlyList<string> cells) =>
            string.Join("  ", cells.Select((c, i) => i == cells.Count - 1 ? c : c.PadRight(widths[i]))).TrimEnd();
        return string.Join('\n', new[] { Line(headers) }.Concat(rows.Select(r => Line(r))));
    }

    /// <summary>512 -> <c>512 MB</c>; from 1024 MB in GB with one decimal: 16384 -> <c>16 GB</c>, 10035 -> <c>9.8 GB</c>.</summary>
    public static string MemorySize(double megabytes) =>
        megabytes < 1024
            ? $"{Math.Round(megabytes, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)} MB"
            : $"{(Math.Round(megabytes / 1024 * 10, MidpointRounding.AwayFromZero) / 10).ToString(CultureInfo.InvariantCulture)} GB";

    /// <summary>
    /// The Memory line of <c>admin status</c>: the process, then the machine's (or the container's) use of the RAM the
    /// server can use: <c>146 MB (process) · machine 9.8 GB of 16 GB used (61%)</c>,
    /// <c>... · container 300 MB of 512 MB used (59%)</c>. Only the process memory when the total is unknown; the total
    /// alone when the use is.
    /// </summary>
    public static string MemoryLine(StatusSnapshot status)
    {
        var process = $"{status.MemoryUsageMb.ToString(CultureInfo.InvariantCulture)} MB";
        if (status.SystemMemoryTotalMb is not { } total || total <= 0) return process;
        var limited = status.MemoryLimited == true;
        var scope = limited ? "container" : "machine";
        var system = status.SystemMemoryUsedMb is { } used
            ? $"{scope} {MemorySize(used)} of {MemorySize(total)} used ({Math.Round(used * 100.0 / total, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}%)"
            : $"{scope}{(limited ? " limit" : "")} {MemorySize(total)}";
        return $"{process} (process) · {system}";
    }

    /// <summary>One entry as a line of <c>admin logs</c>: time, level, [source], message.</summary>
    public static string LogLine(LogRecord entry) =>
        $"{ShortTime(entry.Timestamp)} {entry.Level,-5} {(string.IsNullOrEmpty(entry.Source) ? "" : $"[{entry.Source}] ")}{entry.Message}";
}
