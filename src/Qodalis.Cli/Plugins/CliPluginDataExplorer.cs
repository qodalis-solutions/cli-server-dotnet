using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugins;

/// <summary>What a data explorer source plugin is: its defaults and how it builds its provider.</summary>
public sealed class CliDataExplorerSourceSpec
{
    /// <summary>The default source name (<c>postgres</c>).</summary>
    public required string Name { get; init; }

    /// <summary>The default description (<c>PostgreSQL database</c>).</summary>
    public required string Description { get; init; }

    public DataExplorerLanguage Language { get; init; } = DataExplorerLanguage.Sql;
    public DataExplorerOutputFormat OutputFormat { get; init; } = DataExplorerOutputFormat.Table;

    /// <summary>The settings besides name, description, readOnly, timeoutMs and maxRows (the connection, ...).</summary>
    public required IReadOnlyList<CliConfigSettingDefinition> Connection { get; init; }

    /// <summary>The provider from the settings (called once they are all set).</summary>
    public required Func<CliStoredConfigProvider, IDataExplorerProvider> Create { get; init; }
}

public static class CliPluginDataExplorerExtensions
{
    /// <summary>The settings every data explorer source has around its connection settings.</summary>
    public static List<CliConfigSettingDefinition> SourceSettings(CliDataExplorerSourceSpec spec) =>
    [
        new()
        {
            Key = "name", Label = "Source name", Description = "Shown in the data explorer", Type = CliConfigEntryTypes.String, Default = spec.Name,
            RequiresRestart = true, Validation = new CliConfigValidation { Required = true, Pattern = "^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$" },
        },
        new() { Key = "description", Label = "Description", Type = CliConfigEntryTypes.String, Default = spec.Description, RequiresRestart = true },
        .. spec.Connection,
        new() { Key = "readOnly", Label = "Read-only", Description = "Queries may only read", Type = CliConfigEntryTypes.Boolean, Default = true, RequiresRestart = true },
        new()
        {
            Key = "timeoutMs", Label = "Timeout (ms)", Type = CliConfigEntryTypes.Number, Default = 30000, RequiresRestart = true,
            Validation = new CliConfigValidation { Min = 1000, Max = 600000 },
        },
        new()
        {
            Key = "maxRows", Label = "Rows at most", Type = CliConfigEntryTypes.Number, Default = 1000, RequiresRestart = true,
            Validation = new CliConfigValidation { Min = 1, Max = 100000 },
        },
    ];

    /// <summary>
    /// Adds one data explorer source from its settings: built when the data explorer is first used, left out while a
    /// required setting is missing (the plugin is then <c>needs-configuration</c>); a change applies after a restart.
    /// </summary>
    public static CliStoredConfigProvider AddDataExplorerSource(this CliPluginContext context, string title, string description, CliDataExplorerSourceSpec spec)
    {
        var settings = context.Settings(new CliPluginSettingsOptions { Title = title, Description = description, Settings = SourceSettings(spec) });
        context.Builder.AddDataExplorerProvider(() =>
        {
            if (settings.Missing().Count > 0) return null;
            var options = new DataExplorerProviderOptions
            {
                Name = settings.Text("name") is { Length: > 0 } name ? name : spec.Name,
                Description = settings.Text("description") is { Length: > 0 } text ? text : spec.Description,
                Language = spec.Language,
                DefaultOutputFormat = spec.OutputFormat,
                ReadOnly = settings.Flag("readOnly", true),
                Timeout = (int)settings.Number("timeoutMs", 30000),
                MaxRows = (int)settings.Number("maxRows", 1000),
            };
            return (spec.Create(settings), options);
        });
        return settings;
    }
}

/// <summary>
/// The data explorer's entry (<c>cli.AddPlugin&lt;DataExplorerPluginEntry&gt;(new { forceReadOnly = true })</c>). The data
/// explorer itself is part of the server; this entry sets the rules every source added after it follows (it loads at
/// <see cref="CliPluginOrder.Core"/>, before the sources at <see cref="CliPluginOrder.Source"/>): read-only for all, a row
/// cap, a rate limit for sources without their own. Read when the data explorer is first used (a restart applies a change).
/// </summary>
[CliPlugin(typeof(DataExplorerPluginEntry), Id = "data-explorer", Order = CliPluginOrder.Core)]
public sealed class DataExplorerPluginEntry : ICliPluginEntry
{
    /// <summary>Its settings (code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings =>
    [
        new() { Key = "forceReadOnly", Label = "Read-only for all", Description = "Every source may only read, whatever its own setting", Type = CliConfigEntryTypes.Boolean, Default = false, RequiresRestart = true },
        new()
        {
            Key = "maxRows", Label = "Rows at most", Description = "The most rows any source returns; 0: each source its own", Type = CliConfigEntryTypes.Number,
            Default = 0, RequiresRestart = true, Validation = new CliConfigValidation { Min = 0, Max = 1_000_000 },
        },
        new()
        {
            Key = "rateLimitQueries", Label = "Queries per window", Description = "Per caller and source, for sources without their own limit; 0: none",
            Type = CliConfigEntryTypes.Number, Default = 0, RequiresRestart = true, Validation = new CliConfigValidation { Min = 0 },
        },
        new()
        {
            Key = "rateLimitWindowSeconds", Label = "Window (seconds)", Type = CliConfigEntryTypes.Number, Default = 60, RequiresRestart = true,
            Validation = new CliConfigValidation { Min = 1, Max = 86400 },
        },
    ];

    public void Register(CliPluginContext context)
    {
        var settings = context.Settings(new CliPluginSettingsOptions { Title = "Data explorer", Description = "Rules every data explorer source follows", Settings = [.. Settings] });
        context.Services.AddSingleton(new DataExplorerSourcePolicy
        {
            After = context.Builder.DataExplorerSourceCount,
            ForceReadOnly = () => settings.Flag("forceReadOnly", false),
            MaxRows = () => (int)settings.Number("maxRows", 0),
            RateLimit = () => (int)settings.Number("rateLimitQueries", 0) is var queries and > 0
                ? new DataExplorerRateLimit { MaxQueries = queries, WindowSeconds = Math.Max(1, (int)settings.Number("rateLimitWindowSeconds", 60)) }
                : null,
        });
    }
}
