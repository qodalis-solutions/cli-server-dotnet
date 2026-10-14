using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Users;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Tests.Helpers;

/// <summary>A <see cref="TimeProvider"/> whose clock only moves when told to.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>Captures log messages (to read the generated root password in tests).</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

    public void Dispose() { }

    private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
    }
}

/// <summary>Records broadcast and user-scoped messages instead of sending them to WebSocket clients.</summary>
public sealed class FakeEventSocketManager : ICliEventSocketManager
{
    public ConcurrentQueue<string> Messages { get; } = new();

    /// <summary>User-scoped messages as (userId, message).</summary>
    public ConcurrentQueue<(string UserId, string Message)> UserMessages { get; } = new();

    public Task HandleConnectionAsync(WebSocket socket, CancellationToken cancellationToken, string? remoteAddress = null,
        CliPrincipal? principal = null) => Task.CompletedTask;

    public Task BroadcastMessageAsync(string message)
    {
        Messages.Enqueue(message);
        return Task.CompletedTask;
    }

    public Task SendToUserAsync(string userId, string message)
    {
        UserMessages.Enqueue((userId, message));
        return Task.CompletedTask;
    }

    public Task BroadcastDisconnectAsync() => Task.CompletedTask;

    public IReadOnlyList<CliWebSocketClientInfo> GetClients() => [];

    /// <summary>Parsed <c>session-kicked</c> payloads (<c>data</c> objects), from any channel.</summary>
    public List<JsonElement> SessionKicked() => Messages.Concat(UserMessages.Select(m => m.Message))
        .Select(m => JsonSerializer.Deserialize<JsonElement>(m))
        .Where(m => m.GetProperty("type").GetString() == "session-kicked")
        .Select(m => m.GetProperty("data"))
        .ToList();

    public void Dispose() { }
}

/// <summary>
/// Minimal ASP.NET Core test host with the users plugin. The host uses default MVC JSON
/// options (no WhenWritingNull) so the plugin's own response shaping is exercised.
/// </summary>
public sealed class UsersTestHost : IAsyncDisposable
{
    public const string Base = "/api/v1/qcli";

    private UsersTestHost(IHost host, FakeEventSocketManager events, ManualTimeProvider time)
    {
        Host = host;
        Client = host.GetTestClient();
        Events = events;
        Time = time;
    }

    public IHost Host { get; }
    public HttpClient Client { get; }
    public FakeEventSocketManager Events { get; }
    public ManualTimeProvider Time { get; }

    public static async Task<UsersTestHost> StartAsync(
        Action<UsersModuleOptions>? configure = null,
        bool useMiddleware = true,
        Action<IServiceCollection>? configureServices = null,
        Action<Microsoft.AspNetCore.Mvc.JsonOptions>? jsonOptions = null,
        Action<CliBuilder>? configureCli = null,
        bool realEventSockets = false,
        Action<IApplicationBuilder>? configureApp = null,
        Action<IMvcBuilder>? configureMvc = null)
    {
        var events = new FakeEventSocketManager();
        var time = new ManualTimeProvider();

        var builder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddSingleton<TimeProvider>(time);
                    configureServices?.Invoke(services);
                    services.AddCors();
                    var mvc = services
                        .AddControllers()
                        .AddCli(cli =>
                        {
                            configureCli?.Invoke(cli);
                            cli.AddUsers(o =>
                            {
                                o.DefaultAdminPassword = "root";
                                configure?.Invoke(o);
                            });
                        });
                    if (jsonOptions != null) mvc.AddJsonOptions(jsonOptions);
                    configureMvc?.Invoke(mvc);
                    if (!realEventSockets)
                    {
                        services.RemoveAll<ICliEventSocketManager>();
                        services.AddSingleton<ICliEventSocketManager>(events);
                    }
                });
                webHost.Configure(app =>
                {
                    app.UseWebSockets();
                    app.UseRouting();
                    app.UseCors();
                    if (useMiddleware) app.UseQodalisUsers();
                    app.UseAuthorization();
                    configureApp?.Invoke(app);
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                    app.UseCli();
                });
            });

        var host = await builder.StartAsync();
        return new UsersTestHost(host, events, time);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Host.StopAsync();
        Host.Dispose();
    }

    public T Service<T>() where T : notnull => Host.Services.GetRequiredService<T>();

    /// <summary>Requests a single-use WebSocket ticket for <paramref name="token"/>.</summary>
    public async Task<string> TicketAsync(string token)
    {
        var res = await PostAsync("sessions/ws-ticket", token);
        Assert.Equal(201, (int)res.StatusCode);
        return (await ReadJsonAsync(res)).GetProperty("ticket").GetString()!;
    }

    /// <summary>Opens a WebSocket on the test server; throws when the upgrade is rejected.</summary>
    public async Task<WebSocket> ConnectWebSocketAsync(string pathAndQuery, string? origin = null)
    {
        var client = Host.GetTestServer().CreateWebSocketClient();
        if (origin != null)
            client.ConfigureRequest = r => r.Headers["Origin"] = origin;
        return await client.ConnectAsync(new Uri("ws://localhost" + pathAndQuery), CancellationToken.None);
    }

    /// <summary>Status code of a rejected WebSocket upgrade (0 when it was accepted).</summary>
    public async Task<int> RejectedUpgradeStatusAsync(string pathAndQuery, string? origin = null)
    {
        try
        {
            var socket = await ConnectWebSocketAsync(pathAndQuery, origin);
            await CloseAsync(socket);
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            var match = System.Text.RegularExpressions.Regex.Match(ex.Message, @"status code: (\d+)");
            return match.Success ? int.Parse(match.Groups[1].Value) : -1;
        }
    }

    public static async Task<string> ReceiveTextAsync(WebSocket socket, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[8192];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    public static async Task CloseAsync(WebSocket socket)
    {
        try
        {
            using var cts = new CancellationTokenSource(2000);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
        }
        catch
        {
            // best effort
        }
        socket.Dispose();
    }

    // ---------------------------------------------------------------------
    // HTTP helpers
    // ---------------------------------------------------------------------

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token = null, object? body = null)
    {
        var req = new HttpRequestMessage(method, path.StartsWith('/') ? path : $"{Base}/{path}");
        if (token != null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
            req.Content = new StringContent(
                body as string ?? JsonSerializer.Serialize(body, JsonSerializerOptions.Web),
                Encoding.UTF8, "application/json");
        return Client.SendAsync(req);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? token = null) => SendAsync(HttpMethod.Get, path, token);
    public Task<HttpResponseMessage> PostAsync(string path, string? token, object? body = null) => SendAsync(HttpMethod.Post, path, token, body ?? new { });
    public Task<HttpResponseMessage> PutAsync(string path, string? token, object body) => SendAsync(HttpMethod.Put, path, token, body);
    public Task<HttpResponseMessage> DeleteAsync(string path, string? token = null) => SendAsync(HttpMethod.Delete, path, token);

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    /// <summary>Asserts the status code and a <c>{ "error": string }</c> body; returns the message.</summary>
    public static async Task<string> AssertErrorAsync(HttpResponseMessage res, int status)
    {
        Assert.Equal(status, (int)res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        Assert.True(body.TryGetProperty("error", out var error), $"expected {{error}} body, got {body}");
        Assert.Equal(JsonValueKind.String, error.ValueKind);
        return error.GetString()!;
    }

    public async Task<(string Token, JsonElement Session)> LoginAsync(string username, string password)
    {
        var res = await PostAsync("sessions", null, new { username, password });
        Assert.Equal(201, (int)res.StatusCode);
        var body = await ReadJsonAsync(res);
        return (body.GetProperty("token").GetString()!, body.GetProperty("session"));
    }

    public async Task<string> RootTokenAsync() => (await LoginAsync("root", "root")).Token;

    public async Task<JsonElement> CreateUserAsync(string adminToken, string name, string? password = "pass1234",
        string[]? groups = null, string? email = null)
    {
        var payload = new Dictionary<string, object?> { ["name"] = name, ["email"] = email ?? $"{name}@test.com" };
        if (password != null) payload["password"] = password;
        if (groups != null) payload["groups"] = groups;
        var res = await PostAsync("users", adminToken, payload);
        Assert.Equal(201, (int)res.StatusCode);
        return await ReadJsonAsync(res);
    }

    public async Task CreateGroupAsync(string adminToken, string name, params string[] permissions)
    {
        var res = await PostAsync("groups", adminToken, new { name, permissions });
        Assert.Equal(201, (int)res.StatusCode);
    }

    /// <summary>Creates a group with the given permissions, a member user, and logs the user in.</summary>
    public async Task<string> UserWithPermissionsAsync(string adminToken, string name, params string[] permissions)
    {
        var group = $"{name}-group";
        await CreateGroupAsync(adminToken, group, permissions);
        await CreateUserAsync(adminToken, name, "pass1234", [group]);
        return (await LoginAsync(name, "pass1234")).Token;
    }
}
