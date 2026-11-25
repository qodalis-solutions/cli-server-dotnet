using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests.Config;

/// <summary>
/// <c>config:changed</c> end to end: a WebSocket client on <c>/ws/v1/qcli/events</c> receives it after
/// <c>PATCH /api/v1/qcli/config/{provider}/{section}</c> and after the <c>config set</c> command, on an open server and on
/// a protected one (a signed-in admin; a user without <c>config:read</c> gets nothing).
/// </summary>
public class ConfigEventsSocketTests
{
    /// <summary>The messages up to the marker broadcast after the change.</summary>
    private static async Task<List<JsonElement>> MessagesUntilMarkerAsync(WebSocket socket)
    {
        var messages = new List<JsonElement>();
        while (true)
        {
            var json = JsonSerializer.Deserialize<JsonElement>(await UsersTestHost.ReceiveTextAsync(socket));
            if (json.GetProperty("type").GetString() == "marker") return messages;
            messages.Add(json);
        }
    }

    private static JsonElement? ConfigChanged(List<JsonElement> messages) =>
        messages.Where(m => m.GetProperty("type").GetString() == "config:changed").Cast<JsonElement?>().FirstOrDefault();

    [Fact]
    public async Task OpenServer_PatchAndConfigSet_ReachTheEventsSocket()
    {
        await using var host = await ConfigTestHost.StartAsync(admin: true);
        await host.SignInAdminAsync();
        var socket = await host.Host.GetTestServer().CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws/v1/qcli/events"), CancellationToken.None);
        Assert.Contains("connected", await UsersTestHost.ReceiveTextAsync(socket));

        var patch = await host.Client.SendAsync(ConfigApi.Patch("cors/cors", "{\"changes\":{\"allowedOrigins\":[\"https://a.example\"]},\"version\":\"0\"}"));
        Assert.Equal(200, (int)patch.StatusCode);
        await host.Service<ICliEventSocketManager>().BroadcastMessageAsync("{\"type\":\"marker\"}");
        var fromApi = ConfigChanged(await MessagesUntilMarkerAsync(socket));
        Assert.NotNull(fromApi);
        Assert.Equal(("cors", "cors", "[\"allowedOrigins\"]", "1", "admin"),
            (fromApi!.Value.GetProperty("provider").GetString(), fromApi.Value.GetProperty("section").GetString(), fromApi.Value.GetProperty("keys").GetRawText(),
             fromApi.Value.GetProperty("version").GetString(), fromApi.Value.GetProperty("user").GetString()));

        Assert.StartsWith("Updated custom.custom.motd", (await ConfigCommands.RunAsync(host.Client, "config set custom.custom.motd hi")).Text);
        await host.Service<ICliEventSocketManager>().BroadcastMessageAsync("{\"type\":\"marker\"}");
        var fromCommand = ConfigChanged(await MessagesUntilMarkerAsync(socket));
        Assert.NotNull(fromCommand);
        Assert.Equal(("custom", "[\"motd\"]"), (fromCommand!.Value.GetProperty("provider").GetString(), fromCommand.Value.GetProperty("keys").GetRawText()));
        await UsersTestHost.CloseAsync(socket);
    }

    [Fact]
    public async Task ProtectedServer_TheAdminReceivesIt_AUserWithoutConfigReadDoesNot()
    {
        await using var h = await UsersTestHost.StartAsync(o => o.MinPasswordLength = 4, realEventSockets: true,
            configureCli: cli => cli.AddAdmin(), configureApp: app => app.UseQodalisAdmin());
        var root = await h.RootTokenAsync();
        var plain = await h.UserWithPermissionsAsync(root, "plain", "commands:execute");

        async Task<WebSocket> OpenAsync(string token)
        {
            var socket = await h.ConnectWebSocketAsync($"/ws/v1/qcli/events?ticket={await h.TicketAsync(token)}");
            Assert.Contains("connected", await UsersTestHost.ReceiveTextAsync(socket));
            return socket;
        }

        var rootSocket = await OpenAsync(root);
        var plainSocket = await OpenAsync(plain);

        var patch = await h.SendAsync(HttpMethod.Patch, "config/cors/cors", root, new { changes = new { allowedOrigins = new[] { "https://a.example" } } });
        Assert.Equal(200, (int)patch.StatusCode);
        Assert.StartsWith("Updated custom.custom.motd", (await ConfigCommands.RunAsync(h.Client, "config set custom.custom.motd hi", root)).Text);
        await h.Service<ICliEventSocketManager>().BroadcastMessageAsync("{\"type\":\"marker\"}");

        var rootChanges = (await MessagesUntilMarkerAsync(rootSocket)).Where(m => m.GetProperty("type").GetString() == "config:changed").ToList();
        Assert.Equal(["cors", "custom"], rootChanges.Select(m => m.GetProperty("provider").GetString()));
        Assert.All(rootChanges, m => Assert.Equal("root", m.GetProperty("user").GetString()));
        Assert.Null(ConfigChanged(await MessagesUntilMarkerAsync(plainSocket)));

        await UsersTestHost.CloseAsync(rootSocket);
        await UsersTestHost.CloseAsync(plainSocket);
    }
}
