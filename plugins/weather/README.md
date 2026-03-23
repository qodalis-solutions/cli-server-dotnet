# Qodalis.Cli.Plugin.Weather

Weather command plugin for [Qodalis CLI Server (.NET)](https://github.com/qodalis-solutions/cli-server-dotnet). Provides CLI commands for fetching current weather conditions and multi-day forecasts using the [wttr.in](https://wttr.in) API.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.Weather
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.Weather;

builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddModule(new WeatherModule());
});
```

## Commands

| Command | Description |
|---|---|
| `weather <location>` | Show current weather for a location (default: London) |
| `weather current <location>` | Show current weather conditions |
| `weather forecast <location>` | Show a 3-day weather forecast |

## Parameters

| Parameter | Alias | Description | Default |
|---|---|---|---|
| `--location` | `-l` | City name to get weather for | `London` |

## Example

```
> weather Paris
Weather for Paris, France
  Condition:   Partly cloudy
  Temperature: 18°C (feels like 17°C)
  Humidity:    65%
  Wind:        12 km/h SW
  Visibility:  10 km
  Pressure:    1015 hPa
```

## License

MIT
