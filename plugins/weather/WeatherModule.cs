using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Plugins.Weather;

public class WeatherModule : Cli.CliModule
{
    public override string Name => "weather";
    public override string Version => "1.0.0";
    public override string Description => "Provides weather information commands";

    public override IEnumerable<ICliCommandProcessor> Processors { get; } =
    [
        new CliWeatherCommandProcessor(),
    ];
}
