using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Plugin.Logs.Seq.Extensions;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.Logs.Seq.SeqLogsPluginEntry), Id = "logs-seq")]

namespace Qodalis.Cli.Plugin.Logs.Seq;

/// <summary>
/// The server's logs shipped to Seq from the dashboard (or <c>cli.AddPlugin&lt;SeqLogsPluginEntry&gt;(new { serverUrl = "http://seq:5341" })</c>),
/// and read back by the dashboard's Logs page. The server URL, API key (a secret), service name and minimum level are
/// settings kept in <c>plugin-settings.json</c> and read while the server starts; without a URL it is <c>needs-configuration</c>.
/// </summary>
public sealed class SeqLogsPluginEntry : ICliPluginEntry
{
    /// <summary>Its settings (code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings =>
    [
        new() { Key = "serverUrl", Label = "Seq URL", Description = "e.g. http://seq:5341", Type = CliConfigEntryTypes.String, Required = true, Default = "", RequiresRestart = true, Validation = new CliConfigValidation { Pattern = "^(|https?://\\S+)$" } },
        new() { Key = "apiKey", Label = "API key", Description = "Kept as a secret, never returned", Type = CliConfigEntryTypes.Secret, RequiresRestart = true },
        new() { Key = "serviceName", Label = "Service name", Description = "Empty: the application's name", Type = CliConfigEntryTypes.String, Default = "", RequiresRestart = true },
        new()
        {
            Key = "minimumLevel", Label = "Minimum level", Type = CliConfigEntryTypes.Enum, Default = "Information", RequiresRestart = true,
            Validation = new CliConfigValidation { Options = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"] },
        },
        new() { Key = "readHistory", Label = "Read history", Description = "The dashboard's Logs page reads Seq", Type = CliConfigEntryTypes.Boolean, Default = true, RequiresRestart = true },
    ];

    public void Register(CliPluginContext context)
    {
        var settings = context.BootSettings(new CliPluginSettingsOptions { Title = "Logs on Seq", Description = "The server's logs shipped to Seq", Settings = [.. Settings] });
        var url = settings.Text("serverUrl");
        if (url.Length == 0) return; // needs-configuration
        context.Builder.AddSeqLogs(o =>
        {
            o.ServerUrl = url;
            if (settings.Text("apiKey") is { Length: > 0 } key) o.ApiKey = key;
            if (settings.Text("serviceName") is { Length: > 0 } service) o.ServiceName = service;
            if (Enum.TryParse<LogLevel>(settings.Text("minimumLevel"), out var level)) o.MinimumLevel = level;
            o.ReadHistory = settings.Flag("readHistory", true);
        });
    }
}
