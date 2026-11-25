using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Helpers;

namespace Qodalis.Cli.Processors;

/// <summary>
/// The <c>http</c> command: HTTP requests sent from the server (<c>http get|post &lt;url&gt;</c>). Hosts opt in with
/// <c>cli.AddProcessor&lt;CliHttpCommandProcessor&gt;()</c>; running it needs <see cref="Permission"/>
/// (<c>http:request</c>) on a protected server.
/// </summary>
public class CliHttpCommandProcessor : CliCommandProcessor
{
    /// <summary>The permission the command needs: <c>http:request</c> (core catalog, group Commands).</summary>
    public const string Permission = "http:request";

    /// <inheritdoc />
    public override string Command { get; set; } = "http";
    /// <inheritdoc />
    public override string Description { get; set; } = "Makes HTTP requests from the server";
    /// <summary>Listed under the server's built-in commands.</summary>
    public override string? Module { get; set; } = "core";
    /// <summary><c>http:request</c>, for every sub-command.</summary>
    public override IEnumerable<string>? RequiredPermissions { get; set; } = [Permission];

    /// <inheritdoc />
    public override IEnumerable<CliCommandExample>? Examples { get; set; } =
    [
        new("http get https://example.com", "Fetches a page from the server"),
        new("http post https://httpbin.org/post --body '{\"a\":1}'", "Posts JSON from the server"),
    ];

    public override string? Documentation { get; set; } =
        "Sends the request from the server, not from the browser, so it reaches hosts on the server's network and is not limited by the browser's CORS rules. Prints the status and the body; --headers adds the response headers.";
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

/// <summary>The <c>http get &lt;url&gt;</c> sub-command.</summary>
public class CliHttpGetProcessor : CliCommandProcessor, ICliStreamCommandProcessor
{
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

        return await HttpRequestHelper.SendAsync(url, HttpMethod.Get, null, command.Args.ContainsKey("headers"), cancellationToken);
    }

    public async Task<int> HandleStreamAsync(CliProcessCommand command, Func<object, Task> emit, CancellationToken cancellationToken = default)
    {
        var url = CliValueHelpers.GetPositionals(command).FirstOrDefault();
        if (string.IsNullOrEmpty(url))
        {
            await emit(new { type = "text", value = "Usage: http get <url>" });
            return 1;
        }

        return await HttpStreamHelper.DoStreamRequestAsync(url, "GET", null, command.Args?.ContainsKey("headers") == true, emit, cancellationToken);
    }
}

/// <summary>The <c>http post &lt;url&gt; [--body &lt;json&gt;]</c> sub-command.</summary>
public class CliHttpPostProcessor : CliCommandProcessor, ICliStreamCommandProcessor
{
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
            Examples = ["{\"name\":\"qodalis\"}"],
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
        return await HttpRequestHelper.SendAsync(url, HttpMethod.Post, body, command.Args.ContainsKey("headers"), cancellationToken);
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
        return await HttpStreamHelper.DoStreamRequestAsync(url, "POST", body, command.Args?.ContainsKey("headers") == true, emit, cancellationToken);
    }
}

internal static class HttpRequestHelper
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

internal static class HttpStreamHelper
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<int> DoStreamRequestAsync(
        string url, string method, string? body, bool showHeaders, Func<object, Task> emit, CancellationToken cancellationToken = default)
    {
        try
        {
            await emit(new { type = "text", value = $"Fetching {method} {url}...", style = "info" });

            var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (method == "POST" && body != null)
            {
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            }

            var resp = await Client.SendAsync(request, cancellationToken);

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
            await emit(new { type = "text", value = $"Error: {ex.Message}", style = "error" });
            return 1;
        }
    }
}
