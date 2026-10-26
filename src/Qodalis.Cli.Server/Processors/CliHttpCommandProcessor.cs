using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Helpers;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Server.Processors;

/// <summary>
/// Command processor for making HTTP requests from the server, with "get" and "post" sub-commands.
/// </summary>
public class CliHttpCommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "http";
    public override string Description { get; set; } = "Makes HTTP requests from the server";
    public override bool? AllowUnlistedCommands { get; set; } = false;

    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; }

    /// <summary>
    /// Creates the command. Requests to loopback, private, link-local and CGNAT addresses are refused
    /// unless <see cref="CliServerOptions.AllowPrivateNetworks"/> is set.
    /// </summary>
    /// <param name="options">Server options (resolved from DI); <c>null</c> uses the defaults.</param>
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

/// <summary>
/// Sub-command processor that performs HTTP GET requests.
/// </summary>
public class CliHttpGetProcessor : CliCommandProcessor
{
    private readonly bool _allowPrivateNetworks;

    /// <summary>Creates the sub-command; <paramref name="allowPrivateNetworks"/> disables the private-network guard.</summary>
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

        return await HttpRequestHelper.SendAsync(url, HttpMethod.Get, null, command.Args.ContainsKey("headers"), _allowPrivateNetworks, cancellationToken);
    }
}

/// <summary>
/// Sub-command processor that performs HTTP POST requests.
/// </summary>
public class CliHttpPostProcessor : CliCommandProcessor
{
    private readonly bool _allowPrivateNetworks;

    /// <summary>Creates the sub-command; <paramref name="allowPrivateNetworks"/> disables the private-network guard.</summary>
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
        return await HttpRequestHelper.SendAsync(url, HttpMethod.Post, body, command.Args.ContainsKey("headers"), _allowPrivateNetworks, cancellationToken);
    }
}

/// <summary>
/// Helper for sending HTTP requests and formatting responses.
/// </summary>
internal static class HttpRequestHelper
{
    // Guarded client: every connection (redirect hops included) is vetted by CliOutboundRequestGuard.
    private static readonly HttpClient GuardedClient =
        new(CliOutboundRequestGuard.CreateHandler(allowPrivateNetworks: false)) { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly HttpClient UnrestrictedClient =
        new(CliOutboundRequestGuard.CreateHandler(allowPrivateNetworks: true)) { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<string> SendAsync(string url, HttpMethod method, string? body, bool showHeaders, bool allowPrivateNetworks, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !CliOutboundRequestGuard.IsAllowedScheme(uri))
            return "Error: only http:// and https:// URLs are supported";

        try
        {
            var request = new HttpRequestMessage(method, uri);
            if (body != null)
            {
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            }

            var client = allowPrivateNetworks ? UnrestrictedClient : GuardedClient;
            var response = await client.SendAsync(request, cancellationToken);
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
        catch (HttpRequestException ex) when (FindBlocked(ex) is { } blocked)
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

    private static CliPrivateNetworkBlockedException? FindBlocked(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException)
        {
            if (ex is CliPrivateNetworkBlockedException blocked) return blocked;
        }
        return null;
    }
}
