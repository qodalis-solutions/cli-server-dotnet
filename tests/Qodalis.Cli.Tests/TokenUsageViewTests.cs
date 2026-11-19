using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>Usage provider that answers 30 days with <c>requests</c> 0..29 for every token it is asked about.</summary>
internal sealed class FakeTokenUsageProvider(TimeProvider time) : IApiTokenUsageProvider
{
    public ConcurrentQueue<IReadOnlyCollection<string>> Calls { get; } = new();
    public bool Throw { get; set; }

    public Task<IReadOnlyDictionary<string, ApiTokenUsage>> GetUsageAsync(IReadOnlyCollection<string> tokenIds,
        CancellationToken ct = default)
    {
        Calls.Enqueue(tokenIds.ToList());
        if (Throw) throw new InvalidOperationException("usage store down");
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var days = Enumerable.Range(0, 30)
            .Select(i => new ApiTokenUsageDay(today.AddDays(i - 29).ToString("yyyy-MM-dd"), i))
            .ToList();
        IReadOnlyDictionary<string, ApiTokenUsage> result = tokenIds.ToDictionary(id => id,
            _ => new ApiTokenUsage { Today = 29, Last30Days = days.Sum(d => d.Requests), Days = days });
        return Task.FromResult(result);
    }
}

/// <summary>C4: token views carry <c>usage</c> only when a usage provider is registered.</summary>
public class TokenUsageViewTests
{
    private static async Task<(UsersTestHost H, string Root)> StartAsync(FakeTokenUsageProvider? provider = null,
        CapturingLoggerProvider? logs = null)
    {
        var h = await UsersTestHost.StartAsync(configureServices: s =>
        {
            if (provider != null) s.AddSingleton<IApiTokenUsageProvider>(provider);
            if (logs != null) s.AddSingleton<ILoggerProvider>(logs);
        });
        return (h, await h.RootTokenAsync());
    }

    private static async Task<string> CreateTokenAsync(UsersTestHost h, string session, string name)
    {
        var res = await h.PostAsync("tokens", session, new { name });
        Assert.Equal(201, (int)res.StatusCode);
        return (await UsersTestHost.ReadJsonAsync(res)).GetProperty("apiToken").GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task NoProvider_NoUsageField()
    {
        var (h, root) = await StartAsync();
        await using var host = h;
        await CreateTokenAsync(h, root, "t1");

        var view = Assert.Single((await UsersTestHost.ReadJsonAsync(await h.GetAsync("tokens", root))).EnumerateArray());

        Assert.Equal(["id", "name", "userId", "userName", "createdAt", "expiresAt", "lastUsedAt", "audience"],
            view.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Provider_AddsUsageLast()
    {
        var provider = new FakeTokenUsageProvider(new ManualTimeProvider());
        var (h, root) = await StartAsync(provider);
        await using var host = h;
        await CreateTokenAsync(h, root, "t1");

        var view = Assert.Single((await UsersTestHost.ReadJsonAsync(await h.GetAsync("tokens", root))).EnumerateArray());

        Assert.Equal("usage", view.EnumerateObject().Last().Name);
        var usage = view.GetProperty("usage");
        Assert.Equal(["today", "last30Days", "days"], usage.EnumerateObject().Select(p => p.Name));
        Assert.Equal(29, usage.GetProperty("today").GetInt64());
        Assert.Equal(435, usage.GetProperty("last30Days").GetInt64());
        var days = usage.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(30, days.Count);
        Assert.Equal(["date", "requests"], days[0].EnumerateObject().Select(p => p.Name));
        var today = DateOnly.FromDateTime(h.Time.GetUtcNow().UtcDateTime);
        Assert.Equal(today.AddDays(-29).ToString("yyyy-MM-dd"), days[0].GetProperty("date").GetString());
        Assert.Equal(today.ToString("yyyy-MM-dd"), days[29].GetProperty("date").GetString());
    }

    [Fact]
    public async Task Provider_AllTrue_CoversEveryUser()
    {
        var provider = new FakeTokenUsageProvider(new ManualTimeProvider());
        var (h, root) = await StartAsync(provider);
        await using var host = h;
        var rootToken = await CreateTokenAsync(h, root, "root-token");
        await h.CreateUserAsync(root, "alice");
        var alice = (await h.LoginAsync("alice", "pass1234")).Token;
        var aliceToken = await CreateTokenAsync(h, alice, "alice-token");
        provider.Calls.Clear();

        var views = (await UsersTestHost.ReadJsonAsync(await h.GetAsync("tokens?all=true", root))).EnumerateArray().ToList();

        Assert.Equal(2, views.Count);
        Assert.All(views, v => Assert.True(v.TryGetProperty("usage", out _)));
        var call = Assert.Single(provider.Calls);
        Assert.Equal(new[] { rootToken, aliceToken }.Order(), call.Order());
    }

    [Fact]
    public async Task ProviderThrows_ListStillReturned()
    {
        var logs = new CapturingLoggerProvider();
        var provider = new FakeTokenUsageProvider(new ManualTimeProvider()) { Throw = true };
        var (h, root) = await StartAsync(provider, logs);
        await using var host = h;
        await CreateTokenAsync(h, root, "t1");

        var res = await h.GetAsync("tokens", root);

        Assert.Equal(200, (int)res.StatusCode);
        var view = Assert.Single((await UsersTestHost.ReadJsonAsync(res)).EnumerateArray());
        Assert.False(view.TryGetProperty("usage", out _));
        Assert.Single(logs.Messages, m => m.Contains("token usage"));

        var listed = Assert.Single(await h.Service<IApiTokenManager>().ListAsync(
            (await UsersTestHost.ReadJsonAsync(await h.GetAsync("sessions/current", root))).GetProperty("user").GetProperty("id").GetString()!));
        Assert.Null(listed.Usage);
    }

    [Fact]
    public async Task Manager_List_HasUsage()
    {
        var provider = new FakeTokenUsageProvider(new ManualTimeProvider());
        var (h, root) = await StartAsync(provider);
        await using var host = h;
        await CreateTokenAsync(h, root, "t1");
        var rootId = (await UsersTestHost.ReadJsonAsync(await h.GetAsync("sessions/current", root)))
            .GetProperty("user").GetProperty("id").GetString()!;

        var view = Assert.Single(await h.Service<IApiTokenManager>().ListAsync(rootId));

        Assert.NotNull(view.Usage);
        Assert.Equal(29, view.Usage!.Today);
        Assert.Equal(30, view.Usage.Days.Count);
    }

    [Fact]
    public async Task Create_HasNoUsage()
    {
        var provider = new FakeTokenUsageProvider(new ManualTimeProvider());
        var (h, root) = await StartAsync(provider);
        await using var host = h;

        var res = await h.PostAsync("tokens", root, new { name = "t1" });

        var apiToken = (await UsersTestHost.ReadJsonAsync(res)).GetProperty("apiToken");
        Assert.False(apiToken.TryGetProperty("usage", out _));
        var rootId = (await UsersTestHost.ReadJsonAsync(await h.GetAsync("sessions/current", root)))
            .GetProperty("user").GetProperty("id").GetString()!;
        var created = await h.Service<IApiTokenManager>().CreateAsync(rootId, new ApiTokenCreateOptions { Name = "t2" });
        Assert.Null(created.ApiToken.Usage);
        Assert.DoesNotContain("usage", JsonSerializer.Serialize(created));
    }
}
