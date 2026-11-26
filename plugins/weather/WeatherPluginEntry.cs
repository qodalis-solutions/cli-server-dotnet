using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.Weather.WeatherPluginEntry), Id = "weather")]

namespace Qodalis.Cli.Plugin.Weather;

/// <summary>
/// The weather plugin installed from the dashboard (or <c>cli.AddPlugin&lt;WeatherPluginEntry&gt;(new { defaultLocation = "Paris" })</c>):
/// the <c>weather</c> commands, with the default place as a setting (<c>config set weather.settings.defaultLocation Paris</c>),
/// read on every command.
/// </summary>
public sealed class WeatherPluginEntry : ICliPluginEntry
{
    /// <summary>Its settings (the marketplace's <c>config</c>; code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings =>
    [
        new()
        {
            Key = "defaultLocation", Label = "Default location", Description = "The place when a command names none",
            Type = CliConfigEntryTypes.String, Default = "London", Validation = new CliConfigValidation { Required = true, Max = 100 },
        },
    ];

    public void Register(CliPluginContext context)
    {
        var settings = context.Settings(new CliPluginSettingsOptions
        {
            Title = "Weather", Description = "The weather commands (wttr.in)", Settings = [.. Settings],
        });
        context.Builder.AddModule(new WeatherModule(() => settings.GetValue("settings", "defaultLocation")?.ToString()));
    }
}
