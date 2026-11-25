using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Processors;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Config;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>The built-in http command needs <c>http:request</c> (core catalog, group Commands).</summary>
public class HttpCommandTests
{
    [Fact]
    public void Http_RequiresHttpRequest_InTheCoreCatalog()
    {
        Assert.Equal(["http:request"], new CliHttpCommandProcessor().RequiredPermissions!);

        var services = new ServiceCollection();
        _ = new CliBuilder(services);
        var entry = services.BuildServiceProvider().GetRequiredService<ICliPermissionCatalog>().Find("http:request")!;
        Assert.Equal("Commands", entry.Group);
        Assert.Equal("Make HTTP requests from the server", entry.Description);
        Assert.Equal("cli", entry.Surface);
    }

    [Fact]
    public async Task OnAProtectedServer_HttpIsRefusedWithoutHttpRequest()
    {
        await using var h = await UsersTestHost.StartAsync(o => o.MinPasswordLength = 4,
            configureCli: cli => cli.AddProcessor<CliHttpCommandProcessor>());
        var registry = h.Service<ICliCommandRegistry>();
        var root = await h.RootTokenAsync();
        var runner = await h.UserWithPermissionsAsync(root, "runner", "commands:execute");
        var requester = await h.UserWithPermissionsAsync(root, "requester", "commands:execute", "http:request");

        var refused = await ConfigCommands.RunAsync(h.Client, registry, "http get https://example.com", runner);
        Assert.Equal(403, refused.Status);
        Assert.Contains("http:request", refused.Body.GetProperty("error").GetString());
        Assert.Equal(403, (await ConfigCommands.RunAsync(h.Client, registry, "http", runner)).Status);

        var allowed = await ConfigCommands.RunAsync(h.Client, registry, "http", requester);
        Assert.Equal(200, allowed.Status);
        Assert.Contains("Usage: http get|post", allowed.Text);
    }
}
