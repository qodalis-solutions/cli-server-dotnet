using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>
/// API tokens next to an external auth adapter (<c>AllowApiTokens()</c>): the host signs users in, and the plugin's
/// tokens (created by those users or handed to a linked terminal) authenticate on their own.
/// </summary>
public class ExternalAuthApiTokensTests
{
    /// <summary>A host whose adapter signs in the store's user named in <c>X-User</c>.</summary>
    private static Task<UsersTestHost> StartAsync(bool allowApiTokens) =>
        UsersTestHost.StartAsync(o =>
        {
            o.UseExternalAuth(async (context, _) =>
                context.Request.Headers["X-User"].FirstOrDefault() is { Length: > 0 } name
                && await context.RequestServices.GetRequiredService<IUserStore>().GetUserAsync(name) is { } user
                    ? new ExternalAuthResult { User = user }
                    : null);
            if (allowApiTokens) o.AllowApiTokens();
        });

    private static async Task<string> UserAsync(UsersTestHost h, string name, bool disabled = false) =>
        (await h.Service<IUserStore>().CreateUserAsync(new CreateUserDto
        {
            Name = name, Email = $"{name}@external.com", Groups = ["users"], Disabled = disabled,
        })).Id;

    private static Task<HttpResponseMessage> AsUserAsync(UsersTestHost h, HttpMethod method, string path, string user, object? body = null)
    {
        var req = new HttpRequestMessage(method, $"{UsersTestHost.Base}/{path}");
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        req.Headers.Add("X-User", user);
        req.Headers.Add("Origin", "http://localhost"); // same origin: adapter auth needs it on writes
        return h.Client.SendAsync(req);
    }

    private static Task<HttpResponseMessage> WithTokenAsync(UsersTestHost h, string path, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{UsersTestHost.Base}/{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return h.Client.SendAsync(req);
    }

    [Fact]
    public async Task AUserSignedInByTheHostCreatesATokenThatAuthenticatesOnItsOwn()
    {
        await using var h = await StartAsync(allowApiTokens: true);
        var userId = await UserAsync(h, "ann");

        var created = await UsersTestHost.ReadJsonAsync(await AsUserAsync(h, HttpMethod.Post, "tokens", "ann", new { name = "ci" }));
        var token = created.GetProperty("token").GetString()!;
        var listed = await UsersTestHost.ReadJsonAsync(await WithTokenAsync(h, "tokens", token));

        Assert.StartsWith("qcli_pat_", token);
        Assert.Equal("ci", Assert.Single(listed.EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(userId, created.GetProperty("apiToken").GetProperty("userId").GetString());
        // A token never creates another one
        await UsersTestHost.AssertErrorAsync(await h.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{UsersTestHost.Base}/tokens")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent("""{"name":"again"}""", Encoding.UTF8, "application/json"),
        }), 403);
    }

    [Fact]
    public async Task ATokenStopsWorkingWhenTheStoreDisablesItsUser()
    {
        await using var h = await StartAsync(allowApiTokens: true);
        var userId = await UserAsync(h, "bob");
        var token = (await UsersTestHost.ReadJsonAsync(await AsUserAsync(h, HttpMethod.Post, "tokens", "bob", new { name = "ci" })))
            .GetProperty("token").GetString()!;

        await h.Service<IUserStore>().UpdateUserAsync(userId, new UpdateUserDto { Disabled = true });

        Assert.Equal(401, (int)(await WithTokenAsync(h, "tokens", token)).StatusCode);
    }

    [Fact]
    public async Task AUserSignedInByTheHostApprovesALinkAndTheTerminalUsesItsToken()
    {
        await using var h = await StartAsync(allowApiTokens: true);
        await UserAsync(h, "cara");
        var created = await UsersTestHost.ReadJsonAsync(await h.PostAsync("link", null, new { client = "pwa" }));
        var code = created.GetProperty("userCode").GetString();

        var approved = await AsUserAsync(h, HttpMethod.Post, $"link/code/{code}/approve", "cara", new { });
        var collected = await UsersTestHost.ReadJsonAsync(await h.GetAsync($"link/{created.GetProperty("linkId").GetString()}"));
        var token = collected.GetProperty("token").GetString()!;

        Assert.Equal(200, (int)approved.StatusCode);
        Assert.Equal(200, (int)(await WithTokenAsync(h, "tokens", token)).StatusCode);
    }

    [Fact]
    public async Task WithoutTheOptionTokensStayOffAndBearerTokensGoToTheAdapter()
    {
        await using var h = await StartAsync(allowApiTokens: false);
        await UserAsync(h, "dan");

        await UsersTestHost.AssertErrorAsync(await AsUserAsync(h, HttpMethod.Post, "tokens", "dan", new { name = "ci" }), 501);
        Assert.Equal(401, (int)(await WithTokenAsync(h, "tokens", "qcli_pat_not-a-real-token")).StatusCode);
    }
}
