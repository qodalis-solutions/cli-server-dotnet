using System.Net.Http.Json;
using System.Text.Json;
using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Plugin.Weather;

/// <summary>
/// CLI command processor that provides weather information for a given location.
/// Supports sub-commands for current conditions and multi-day forecasts.
/// </summary>
public class CliWeatherCommandProcessor : Cli.CliCommandProcessor
{
    private static readonly HttpClient _httpClient = new();

    /// <inheritdoc />
    public override string Command { get; set; } = "weather";
    /// <inheritdoc />
    public override string Description { get; set; } = "Shows weather information for a location";

    /// <inheritdoc />
    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; } =
    [
        new WeatherCurrentProcessor(),
        new WeatherForecastProcessor(),
    ];

    /// <inheritdoc />
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "location",
            Aliases = ["-l"],
            Description = "Location to get weather for (city name)",
            Type = CommandParameterType.String,
            DefaultValue = "London",
        },
    ];

    /// <inheritdoc />
    public override async Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var location = GetLocation(command);
        return await FetchWeather(location, cancellationToken);
    }

    /// <summary>
    /// Extracts the location from the command arguments, falling back to "London".
    /// </summary>
    internal static string GetLocation(CliProcessCommand command)
    {
        if (command.Args.TryGetValue("location", out var loc))
            return loc.ToString()!;

        if (!string.IsNullOrWhiteSpace(command.Value))
            return command.Value;

        return "London";
    }

    /// <summary>
    /// Fetches current weather conditions from wttr.in for the specified location.
    /// </summary>
    internal static async Task<string> FetchWeather(string location, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://wttr.in/{Uri.EscapeDataString(location)}?format=j1";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "qodalis-cli/1.0");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);

            var current = json.GetProperty("current_condition")[0];
            var temp = current.GetProperty("temp_C").GetString();
            var feelsLike = current.GetProperty("FeelsLikeC").GetString();
            var humidity = current.GetProperty("humidity").GetString();
            var windSpeed = current.GetProperty("windspeedKmph").GetString();
            var windDir = current.GetProperty("winddir16Point").GetString();
            var desc = current.GetProperty("weatherDesc")[0].GetProperty("value").GetString();
            var visibility = current.GetProperty("visibility").GetString();
            var pressure = current.GetProperty("pressure").GetString();

            var area = json.GetProperty("nearest_area")[0];
            var city = area.GetProperty("areaName")[0].GetProperty("value").GetString();
            var country = area.GetProperty("country")[0].GetProperty("value").GetString();

            return $"Weather for {city}, {country}\n" +
                   $"  Condition:   {desc}\n" +
                   $"  Temperature: {temp}°C (feels like {feelsLike}°C)\n" +
                   $"  Humidity:    {humidity}%\n" +
                   $"  Wind:        {windSpeed} km/h {windDir}\n" +
                   $"  Visibility:  {visibility} km\n" +
                   $"  Pressure:    {pressure} hPa";
        }
        catch (HttpRequestException ex)
        {
            return $"Failed to fetch weather data: {ex.Message}";
        }
    }

    /// <summary>
    /// Fetches a 3-day weather forecast from wttr.in for the specified location.
    /// </summary>
    internal static async Task<string> FetchForecast(string location, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://wttr.in/{Uri.EscapeDataString(location)}?format=j1";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "qodalis-cli/1.0");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);

            var area = json.GetProperty("nearest_area")[0];
            var city = area.GetProperty("areaName")[0].GetProperty("value").GetString();
            var country = area.GetProperty("country")[0].GetProperty("value").GetString();

            var lines = new List<string> { $"3-day forecast for {city}, {country}\n" };

            foreach (var day in json.GetProperty("weather").EnumerateArray())
            {
                var date = day.GetProperty("date").GetString();
                var maxTemp = day.GetProperty("maxtempC").GetString();
                var minTemp = day.GetProperty("mintempC").GetString();
                var desc = day.GetProperty("hourly")[4].GetProperty("weatherDesc")[0].GetProperty("value").GetString();
                var chanceOfRain = day.GetProperty("hourly")[4].GetProperty("chanceofrain").GetString();

                lines.Add($"  {date}: {desc}, {minTemp}°C - {maxTemp}°C, rain {chanceOfRain}%");
            }

            return string.Join("\n", lines);
        }
        catch (HttpRequestException ex)
        {
            return $"Failed to fetch forecast data: {ex.Message}";
        }
    }
}

/// <summary>
/// Sub-command processor that displays current weather conditions for a location.
/// </summary>
internal class WeatherCurrentProcessor : Cli.CliCommandProcessor
{
    public override string Command { get; set; } = "current";
    public override string Description { get; set; } = "Shows current weather conditions";

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "location",
            Aliases = ["-l"],
            Description = "Location to get weather for (city name)",
            Type = CommandParameterType.String,
            DefaultValue = "London",
        },
    ];

    public override async Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var location = CliWeatherCommandProcessor.GetLocation(command);
        return await CliWeatherCommandProcessor.FetchWeather(location, cancellationToken);
    }
}

/// <summary>
/// Sub-command processor that displays a 3-day weather forecast for a location.
/// </summary>
internal class WeatherForecastProcessor : Cli.CliCommandProcessor
{
    public override string Command { get; set; } = "forecast";
    public override string Description { get; set; } = "Shows a 3-day weather forecast";

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "location",
            Aliases = ["-l"],
            Description = "Location to get weather for (city name)",
            Type = CommandParameterType.String,
            DefaultValue = "London",
        },
    ];

    public override async Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var location = CliWeatherCommandProcessor.GetLocation(command);
        return await CliWeatherCommandProcessor.FetchForecast(location, cancellationToken);
    }
}
