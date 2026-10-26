using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Helpers;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Demo.Processors;

public class CliHttpCommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "http";
    public override string Description { get; set; } = "Makes HTTP requests from the server";
    public override bool? AllowUnlistedCommands { get; set; } = false;

    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; }

    // Private/local destinations are refused unless CliServerOptions.AllowPrivateNetworks is set.
    public CliHttpCommandProcessor(CliServerOptions? options = null)
    {
        var allowPrivateNetworks = options?.AllowPrivateNetworks ?? false;
        Processors = new ICliCommandProcessor[]
        {
            new CliHttpGetProcessor(allowPrivateNetworks),
            new CliHttpPostProcessor(allowPrivateNetworks),
        };
    }

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        return Task.FromResult("Usage: http get|post <url> [--body <json>] [--headers]");
    }
}

public class CliHttpGetProcessor : CliCommandProcessor, ICliStreamCommandProcessor
{
    private readonly bool _allowPrivateNetworks;

    public CliHttpGetProcessor(bool allowPrivateNetworks = false)
    {
        _allowPrivateNetworks = allowPrivateNetworks;
    }

    public override string Command { get; set; } = "get";
    public override bool? AcceptsRawInput { get; set; } = true;
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
        var url = CliValueHelpers.GetPositionals(command).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(url))
            return "Usage: http get <url>";

        return await DemoHttpRequestHelper.SendAsync(url, HttpMethod.Get, null, command.Args.ContainsKey("headers"), _allowPrivateNetworks, cancellationToken);
    }

    public async Task<int> HandleStreamAsync(CliProcessCommand command, Func<object, Task> emit, CancellationToken cancellationToken = default)
    {
        var url = CliValueHelpers.GetPositionals(command).FirstOrDefault();
        if (string.IsNullOrEmpty(url))
        {
            await emit(new { type = "text", value = "Usage: http get <url>" });
            return 1;
        }

        return await DemoHttpStreamHelper.DoStreamRequestAsync(url, "GET", null, command.Args?.ContainsKey("headers") == true, _allowPrivateNetworks, emit, cancellationToken);
    }
}

public class CliHttpPostProcessor : CliCommandProcessor, ICliStreamCommandProcessor
{
    private readonly bool _allowPrivateNetworks;

    public CliHttpPostProcessor(bool allowPrivateNetworks = false)
    {
        _allowPrivateNetworks = allowPrivateNetworks;
    }

    public override string Command { get; set; } = "post";
    public override bool? AcceptsRawInput { get; set; } = true;
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
        var url = CliValueHelpers.GetPositionals(command).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(url))
            return "Usage: http post <url> --body '{\"key\":\"value\"}'";

        var body = command.Args.TryGetValue("body", out var b) ? b?.ToString() : null;
        return await DemoHttpRequestHelper.SendAsync(url, HttpMethod.Post, body, command.Args.ContainsKey("headers"), _allowPrivateNetworks, cancellationToken);
    }

    public async Task<int> HandleStreamAsync(CliProcessCommand command, Func<object, Task> emit, CancellationToken cancellationToken = default)
    {
        var url = CliValueHelpers.GetPositionals(command).FirstOrDefault();
        if (string.IsNullOrEmpty(url))
        {
            await emit(new { type = "text", value = "Usage: http post <url> --body '{\"key\":\"value\"}'" });
            return 1;
        }

        var body = command.Args?.TryGetValue("body", out var b) == true ? b?.ToString() : null;
        return await DemoHttpStreamHelper.DoStreamRequestAsync(url, "POST", body, command.Args?.ContainsKey("headers") == true, _allowPrivateNetworks, emit, cancellationToken);
    }
}

internal static class DemoHttpClients
{
    // Guarded client: every connection (redirect hops included) is vetted by CliOutboundRequestGuard.
    private static readonly HttpClient Guarded =
        new(CliOutboundRequestGuard.CreateHandler(allowPrivateNetworks: false)) { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly HttpClient Unrestricted =
        new(CliOutboundRequestGuard.CreateHandler(allowPrivateNetworks: true)) { Timeout = TimeSpan.FromSeconds(30) };

    public static HttpClient For(bool allowPrivateNetworks) => allowPrivateNetworks ? Unrestricted : Guarded;

    public static bool TryParse(string url, out Uri uri)
        => Uri.TryCreate(url, UriKind.Absolute, out uri!) && CliOutboundRequestGuard.IsAllowedScheme(uri);

    public static CliPrivateNetworkBlockedException? FindBlocked(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException)
        {
            if (ex is CliPrivateNetworkBlockedException blocked) return blocked;
        }
        return null;
    }
}

internal static class DemoHttpRequestHelper
{
    public static async Task<string> SendAsync(string url, HttpMethod method, string? body, bool showHeaders, bool allowPrivateNetworks, CancellationToken cancellationToken)
    {
        if (!DemoHttpClients.TryParse(url, out var uri))
            return "Error: only http:// and https:// URLs are supported";

        try
        {
            var request = new HttpRequestMessage(method, uri);
            if (body != null)
            {
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            }

            var response = await DemoHttpClients.For(allowPrivateNetworks).SendAsync(request, cancellationToken);
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
        catch (HttpRequestException ex) when (DemoHttpClients.FindBlocked(ex) is { } blocked)
        {
            return blocked.Message;
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
    public static async Task<int> DoStreamRequestAsync(
        string url, string method, string? body, bool showHeaders, bool allowPrivateNetworks, Func<object, Task> emit, CancellationToken cancellationToken = default)
    {
        if (!DemoHttpClients.TryParse(url, out var uri))
        {
            await emit(new { type = "text", value = "Error: only http:// and https:// URLs are supported", style = "error" });
            return 1;
        }

        try
        {
            await emit(new { type = "text", value = $"Fetching {method} {url}...", style = "info" });

            var request = new HttpRequestMessage(new HttpMethod(method), uri);
            if (method == "POST" && body != null)
            {
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            }

            var resp = await DemoHttpClients.For(allowPrivateNetworks).SendAsync(request, cancellationToken);

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "unknown";
            var respBody = await resp.Content.ReadAsStringAsync(cancellationToken);

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
            var message = DemoHttpClients.FindBlocked(ex)?.Message ?? $"Error: {ex.Message}";
            await emit(new { type = "text", value = message, style = "error" });
            return 1;
        }
    }
}
