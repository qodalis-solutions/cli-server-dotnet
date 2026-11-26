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

## Terminal commands

The commands are listed under [Commands](#commands); they need no permission beyond `commands:execute` on a protected
server.

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

## Install from the dashboard or in code

The package declares an entry point (`WeatherPluginEntry`, plugin id `weather`), so it can be installed from the dashboard's
marketplace (the admin plugin's server plugins, loaded at the next start) or added in code through the same entry:

```csharp
cli.AddPlugin<WeatherPluginEntry>(new { defaultLocation = "Paris" });
```

Its settings (config provider `weather` unless noted; code options are their defaults, saved settings override them):
`defaultLocation` (default London), read on every command. A required setting without a value lists the plugin as `needs-configuration` with `missing`; see
[Server plugins](../../README.md#server-plugins-from-code-or-the-dashboard).

## License

MIT
