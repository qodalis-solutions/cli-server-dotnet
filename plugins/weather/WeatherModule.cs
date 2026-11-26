using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Plugin.Weather;

/// <summary>
/// CLI module that registers weather-related command processors.
/// </summary>
public class WeatherModule : Cli.CliModule
{
    /// <inheritdoc />
    public override string Name => "weather";
    /// <inheritdoc />
    public override string Version => "1.0.0";
    /// <inheritdoc />
    public override string Description => "Provides weather information commands";

    /// <inheritdoc />
    public override string? Icon => "🌤️";

    /// <inheritdoc />
    public override IEnumerable<ICliCommandProcessor> Processors { get; }

    /// <param name="defaultLocation">The place when a command names none, read on every command (default London).</param>
    public WeatherModule(Func<string?>? defaultLocation = null)
    {
        Processors = [new CliWeatherCommandProcessor(defaultLocation)];
    }
}
