using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Plugin.Audit.Extensions;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.Audit.AuditPluginEntry), Id = "audit")]

namespace Qodalis.Cli.Plugin.Audit;

/// <summary>
/// The audit plugin installed from the dashboard (or <c>cli.AddPlugin&lt;AuditPluginEntry&gt;(new { maxEntries = 5000 })</c>):
/// the audit log (<c>/api/v1/qcli/audit</c>) and the dashboard's Audit page, kept in memory with its size a setting
/// (read when the log is first used: a restart applies a change). A store the host set is kept.
/// </summary>
public sealed class AuditPluginEntry : ICliPluginEntry
{
    /// <summary>Its settings (the marketplace's <c>config</c>; code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings =>
    [
        new()
        {
            Key = "maxEntries", Label = "Entries kept", Description = "The oldest are dropped beyond it", Type = CliConfigEntryTypes.Number,
            Default = InMemoryAuditStore.DefaultMaxEntries, RequiresRestart = true, Validation = new CliConfigValidation { Min = 100, Max = 1_000_000 },
        },
    ];

    public void Register(CliPluginContext context)
    {
        var settings = context.Settings(new CliPluginSettingsOptions
        {
            Title = "Audit", Description = "The audit log, kept in memory", Settings = [.. Settings],
        });
        context.Services.TryAddSingleton<IAuditStore>(_ =>
            new InMemoryAuditStore(double.TryParse(settings.GetValue("settings", "maxEntries")?.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var max) ? (int)max : InMemoryAuditStore.DefaultMaxEntries));
        context.Builder.AddAudit();
    }
}
