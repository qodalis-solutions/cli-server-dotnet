using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Demo.Processors;

public class CliHttpCommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "http";
    public override string Description { get; set; } = "Makes HTTP requests from the server";
    public override bool? AllowUnlistedCommands { get; set; } = false;

    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; } = new ICliCommandProcessor[]
    {
        new CliHttpGetProcessor(),
        new CliHttpPostProcessor(),
    };

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        return Task.FromResult("Usage: http get|post <url> [--body <json>] [--headers]");
    }
}

public class CliHttpGetProcessor : CliCommandProcessor, ICliStreamCommandProcessor
{
    public override string Command { get; set; } = "get";
    public override string Description { get; set; } = "Performs an HTTP GET request";

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "headers",
            Description = "Show response headers",
            Type = CommandParameterType.Boolean,
        },
    ];

    public override async Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var url = command.Value;
        if (string.IsNullOrWhiteSpace(url))
            return "Usage: http get <url>";

        return await DemoHttpRequestHelper.SendAsync(url, HttpMethod.Get, null, command.Args.ContainsKey("headers"), cancellationToken);
    }

    public async Task<int> HandleStreamAsync(CliProcessCommand command, Func<object, Task> emit)
    {
        var url = command.Value;
        if (string.IsNullOrEmpty(url))
        {
            await emit(new { type = "text", value = "Usage: http get <url>" });
            return 1;
        }

        return await DemoHttpStreamHelper.DoStreamRequestAsync(url, "GET", null, command.Args?.ContainsKey("headers") == true, emit);
    }
}

public class CliHttpPostProcessor : CliCommandProcessor, ICliStreamCommandProcessor
{
    public override string Command { get; set; } = "post";
    public override string Description { get; set; } = "Performs an HTTP POST request";

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "body",
            Aliases = ["-b"],
            Description = "Request body (JSON string)",
            Type = CommandParameterType.String,
        },
        new CliCommandParameterDescriptor
        {
            Name = "headers",
            Description = "Show response headers",
            Type = CommandParameterType.Boolean,
        },
    ];

    public override async Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var url = command.Value;
        if (string.IsNullOrWhiteSpace(url))
            return "Usage: http post <url> --body '{\"key\":\"value\"}'";

        var body = command.Args.TryGetValue("body", out var b) ? b?.ToString() : null;
        return await DemoHttpRequestHelper.SendAsync(url, HttpMethod.Post, body, command.Args.ContainsKey("headers"), cancellationToken);
    }

    public async Task<int> HandleStreamAsync(CliProcessCommand command, Func<object, Task> emit)
    {
        var url = command.Value;
        if (string.IsNullOrEmpty(url))
        {
            await emit(new { type = "text", value = "Usage: http post <url> --body '{\"key\":\"value\"}'" });
            return 1;
        }

        var body = command.Args?.TryGetValue("body", out var b) == true ? b?.ToString() : null;
        return await DemoHttpStreamHelper.DoStreamRequestAsync(url, "POST", body, command.Args?.ContainsKey("headers") == true, emit);
    }
}

internal static class DemoHttpRequestHelper
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<string> SendAsync(string url, HttpMethod method, string? body, bool showHeaders, CancellationToken cancellationToken)
    {
        try
        {
            var request = new HttpRequestMessage(method, url);
            if (body != null)
            {
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            }

            var response = await Client.SendAsync(request, cancellationToken);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "unknown";
            var respBody = await response.Content.ReadAsStringAsync(cancellationToken);

            var lines = new List<string>
            {
                $"Status: {(int)response.StatusCode}",
                $"Content-Type: {contentType}",
            };

            if (showHeaders)
            {
                lines.Add("Headers:");
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                {
                    lines.Add($"  {header.Key}: {string.Join(", ", header.Value)}");
                }
            }

            lines.Add("");

            if (contentType.Contains("json"))
            {
                try
                {
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(respBody);
                    respBody = System.Text.Json.JsonSerializer.Serialize(parsed, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                }
                catch { /* keep raw */ }
            }

            lines.Add(respBody.Length > 5000 ? respBody[..5000] + "\n... (truncated)" : respBody);
            return string.Join("\n", lines);
        }
        catch (HttpRequestException ex)
        {
            return $"HTTP Error: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            return "Error: Request timed out";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}

internal static class DemoHttpStreamHelper
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<int> DoStreamRequestAsync(
        string url, string method, string? body, bool showHeaders, Func<object, Task> emit)
    {
        try
        {
            await emit(new { type = "text", value = $"Fetching {method} {url}...", style = "info" });

            var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (method == "POST" && body != null)
            {
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            }

            var resp = await Client.SendAsync(request);

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "unknown";
            var respBody = await resp.Content.ReadAsStringAsync();

            await emit(new { type = "text", value = $"Status: {(int)resp.StatusCode}" });
            await emit(new { type = "text", value = $"Content-Type: {contentType}" });

            if (showHeaders)
            {
                await emit(new { type = "text", value = "Headers:" });
                foreach (var header in resp.Headers.Concat(resp.Content.Headers))
                {
                    await emit(new { type = "text", value = $"  {header.Key}: {string.Join(", ", header.Value)}" });
                }
            }

            if (contentType.Contains("json"))
            {
                try
                {
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<object>(respBody);
                    respBody = System.Text.Json.JsonSerializer.Serialize(parsed, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                }
                catch { /* keep raw */ }
            }

            await emit(new { type = "text", value = respBody.Length > 5000 ? respBody[..5000] : respBody });
            return 0;
        }
        catch (Exception ex)
        {
            await emit(new { type = "text", value = $"Error: {ex.Message}", style = "error" });
            return 1;
        }
    }
}
