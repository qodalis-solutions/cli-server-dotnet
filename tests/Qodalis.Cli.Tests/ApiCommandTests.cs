using System.Text.Json;
using Qodalis.Cli.Plugin.PublicApi;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Config;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>
/// The public-api plugin's <c>api usage</c> terminal command through <c>POST /execute</c>: your own usage per token
/// and day with the quota, --days, --json, another account by name or id (tokens:read), no tokens, unknown users and
/// bad arguments. Today is 2026-11-19 (<see cref="PublicApiTestHost.LateEvening"/>).
/// </summary>
public sealed class ApiCommandTests : IAsyncLifetime
{
    private UsersTestHost? _h;
    private string _alice = null!, _auditor = null!, _root = null!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_h != null) await _h.DisposeAsync();
    }

    private async Task<UsersTestHost> StartAsync(Func<string, PublicApiQuota>? quota = null)
    {
        _h = await PublicApiTestHost.StartAsync(o =>
        {
            if (quota != null) o.UseQuotaResolver((caller, _, _) => ValueTask.FromResult(quota(caller.UserName)));
        });
        _root = await _h.RootTokenAsync();
        _alice = await _h.UserWithPermissionsAsync(_root, "alice", "commands:execute", "rates:read");
        _auditor = await _h.UserWithPermissionsAsync(_root, "auditor", "commands:execute", "tokens:read");
        return _h;
    }

    private async Task<(int Status, string Text, JsonElement Body)> Run(string line, string? token) =>
        await ConfigCommands.RunAsync(_h!.Client, _h.Service<ICliCommandRegistry>(), line, token);

    /// <summary>A host token of the user and <paramref name="requests"/> counted on each of the days.</summary>
    private async Task<(string UserId, string TokenId)> TokenWithUsageAsync(string userName, string name, Dictionary<string, int> requests)
    {
        var user = (await _h!.Service<IUserStore>().GetUserAsync(userName))!;
        var created = await _h.Service<IApiTokenManager>().CreateAsync(user.Id, new ApiTokenCreateOptions
        {
            Name = name, Audience = ApiTokenAudience.Host, Permissions = ["rates:read"],
        });
        var store = _h.Service<IPublicApiUsageStore>();
        foreach (var (day, n) in requests)
        {
            for (var i = 0; i < n; i++) await store.IncrementAsync(created.ApiToken.Id, user.Id, day, CancellationToken.None);
        }
        return (user.Id, created.ApiToken.Id);
    }

    [Fact]
    public async Task IsRegisteredAsApiWithUsage_NeedingNoPermissionBeyondCommandsExecute()
    {
        var h = await StartAsync();
        var root = h.Service<ICliCommandRegistry>().FindProcessor("api")!;
        Assert.Null(root.RequiredPermissions);
        var usage = Assert.Single(root.Processors!);
        Assert.Equal("usage", usage.Command);
        Assert.Null(usage.RequiredPermissions);
    }

    [Fact]
    public async Task Usage_YourRequestsPerDayAndToken_TotalsTodayAndTheQuota()
    {
        await StartAsync(_ => new PublicApiQuota(1000, 60));
        await TokenWithUsageAsync("alice", "website", new() { ["2026-11-19"] = 3, ["2026-11-18"] = 2, ["2026-11-04"] = 9 });
        await TokenWithUsageAsync("alice", "ci-bot", new() { ["2026-11-19"] = 10 });
        Assert.Equal(string.Join('\n',
            "alice: 13 request(s) today (every token, revoked ones too)",
            "Quota: 1000 per day, 60 per minute",
            "Day         website  ci-bot  Total",
            "2026-11-17        0       0      0",
            "2026-11-18        2       0      2",
            "2026-11-19        3      10     13",
            "Total             5      10     15"), (await Run("api usage --days 3", _alice)).Text);
        // api alone is api usage, 7 days by default
        Assert.Equal(2 + 1 + 7 + 1, (await Run("api", _alice)).Text.Split('\n').Length);
        Assert.Contains("Total            14      10     24", (await Run("api usage --days 30", _alice)).Text);
    }

    [Fact]
    public async Task Json_TheAccountUsage_WithTheDaysAskedFor()
    {
        await StartAsync();
        var (userId, tokenId) = await TokenWithUsageAsync("alice", "website", new() { ["2026-11-19"] = 4 });
        var body = JsonDocument.Parse((await Run("api usage --days 2 --json", _alice)).Text).RootElement;
        Assert.Equal(
            $$$"""{"user":{"id":"{{{userId}}}","name":"alice"},"days":2,"today":4,"quota":{"requestsPerDay":null,"requestsPerMinute":null},"tokens":[{"tokenId":"{{{tokenId}}}","name":"website","usage":{"today":4,"last30Days":4,"days":[{"date":"2026-11-18","requests":0},{"date":"2026-11-19","requests":4}]}}]}""",
            JsonSerializer.Serialize(body));
    }

    [Fact]
    public async Task AnAccountWithoutTokens_Unlimited()
    {
        await StartAsync();
        Assert.Equal(string.Join('\n', "alice: 0 request(s) today (every token, revoked ones too)", "Quota: unlimited", "alice has no API tokens."),
            (await Run("api usage", _alice)).Text);
    }

    [Fact]
    public async Task AnotherAccountsUsage_NeedsTokensRead_ByNameOrId()
    {
        await StartAsync();
        var (userId, _) = await TokenWithUsageAsync("alice", "website", new() { ["2026-11-19"] = 1 });
        Assert.Equal("Error: another account's usage needs tokens:read", (await Run("api usage --user auditor", _alice)).Text);
        Assert.Contains("alice: 1 request(s) today", (await Run("api usage --user alice", _auditor)).Text);
        Assert.Contains("alice: 1 request(s) today", (await Run($"api usage --user {userId}", _auditor)).Text);
        Assert.Contains("alice: 1 request(s) today", (await Run("api usage --user ALICE", _root)).Text);
        // naming yourself needs nothing more
        Assert.Contains("alice: 1 request(s) today", (await Run("api usage --user alice", _alice)).Text);
    }

    [Fact]
    public async Task RefusesUnknownUsersAndBadArguments()
    {
        await StartAsync();
        Assert.Equal("Error: user not found: nobody", (await Run("api usage --user nobody", _root)).Text);
        Assert.Equal("Error: --user needs a name or id", (await Run("api usage --user", _root)).Text);
        foreach (var bad in new[] { "0", "31", "x", "2.5" })
            Assert.Equal("Error: --days must be a whole number from 1 to 30", (await Run($"api usage --days {bad}", _root)).Text);
    }

    [Fact]
    public async Task TheQuota_AsTheResolverGivesItForTheAccount()
    {
        await StartAsync(name => name == "alice" ? new PublicApiQuota(0, null) : new PublicApiQuota(null, 5));
        Assert.Contains("Quota: no API access", (await Run("api usage", _alice)).Text);
        Assert.Contains("Quota: 5 per minute", (await Run("api usage", _auditor)).Text);
    }

    [Fact]
    public async Task AnonymousCallersAreRefused_TheUsersPluginProtectsTheServer()
    {
        await StartAsync();
        Assert.Equal(401, (await Run("api usage", null)).Status);
    }

    [Fact]
    public async Task OnAnOpenServer_NeedsTheAccount()
    {
        _h = await PublicApiTestHost.StartAsync(configureUsers: o => o.ProtectServer = false);
        Assert.Equal("Error: no signed-in user: name the account with --user <name|id>", (await Run("api usage", null)).Text);
        Assert.Equal("root: 0 request(s) today (every token, revoked ones too)\nQuota: unlimited\nroot has no API tokens.",
            (await Run("api usage --user root", null)).Text);
    }

    [Fact]
    public void FormatQuota()
    {
        Assert.Equal("unlimited", CliApiCommandProcessor.FormatQuota(PublicApiQuota.Unlimited));
        Assert.Equal("10 per day", CliApiCommandProcessor.FormatQuota(new PublicApiQuota(10, null)));
        Assert.Equal("no API access", CliApiCommandProcessor.FormatQuota(new PublicApiQuota(10, -1)));
    }
}
