using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Users;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Tests.Helpers;

/// <summary>A <see cref="TimeProvider"/> whose clock only moves when told to.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>Records broadcast messages instead of sending them to WebSocket clients.</summary>
public sealed class FakeEventSocketManager : ICliEventSocketManager
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public Task HandleConnectionAsync(WebSocket socket, CancellationToken cancellationToken, string? remoteAddress = null) =>
        Task.CompletedTask;

    public Task BroadcastMessageAsync(string message)
    {
        Messages.Enqueue(message);
        return Task.CompletedTask;
    }

    public Task BroadcastDisconnectAsync() => Task.CompletedTask;

    public IReadOnlyList<CliWebSocketClientInfo> GetClients() => [];

    /// <summary>Parsed <c>session-kicked</c> payloads (<c>data</c> objects).</summary>
    public List<JsonElement> SessionKicked() => Messages
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
        Action<Microsoft.AspNetCore.Mvc.JsonOptions>? jsonOptions = null)
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
                        .AddCli(cli => cli.AddUsers(configure));
                    if (jsonOptions != null) mvc.AddJsonOptions(jsonOptions);
                    services.RemoveAll<ICliEventSocketManager>();
                    services.AddSingleton<ICliEventSocketManager>(events);
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseCors();
                    if (useMiddleware) app.UseQodalisUsers();
                    app.UseAuthorization();
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
