using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Plugin.PublicApi;
using Qodalis.Cli.Plugin.Users.Authorization;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>A scoped service, to prove the quota resolver is resolved per request.</summary>
public sealed class ScopedQuotaSource
{
    public long PerDay => 5;
}

/// <summary>A resolver that depends on a scoped service.</summary>
public sealed class ScopedResolver(ScopedQuotaSource source) : IPublicApiQuotaResolver
{
    public ValueTask<PublicApiQuota> GetQuotaAsync(QcliCaller caller, CancellationToken ct) =>
        ValueTask.FromResult(new PublicApiQuota(source.PerDay, null));
}

/// <summary>C5: the public-api guard (host tokens, permission, per-account quotas, headers) and the usage it records.</summary>
public class PublicApiQuotaTests
{
    private static string Day => PublicApiTestHost.LateEvening.ToString("yyyy-MM-dd");

    private static Action<PublicApiOptions> Quota(long? perDay, long? perMinute) =>
        o => o.UseQuotaResolver((_, _, _) => ValueTask.FromResult(new PublicApiQuota(perDay, perMinute)));

    private static string? Header(HttpResponseMessage res, string name) =>
        res.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private static async Task AssertBodyAsync(HttpResponseMessage res, int status, string error)
    {
        Assert.Equal(status, (int)res.StatusCode);
        Assert.Equal(JsonSerializer.Serialize(new { error }), await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unlimited_NoHeaders()
    {
        await using var h = await PublicApiTestHost.StartAsync();
        var (_, token) = await h.HostTokenAsync("alice");

        var res = await h.GetAsync("/v1/rates", token);

        Assert.Equal(200, (int)res.StatusCode);
        Assert.DoesNotContain(res.Headers, x => x.Key.StartsWith("X-RateLimit", StringComparison.OrdinalIgnoreCase));
        Assert.Null(Header(res, "Retry-After"));
    }

    [Fact]
    public async Task Unlimited_StillCounted()
    {
        await using var h = await PublicApiTestHost.StartAsync();
        var (aliceId, token) = await h.HostTokenAsync("alice");

        await h.GetAsync("/v1/rates", token);
        await h.GetAsync("/v1/open", token);

        Assert.Equal(2, await h.Service<IPublicApiUsageStore>().GetAccountCountAsync(aliceId, Day, CancellationToken.None));
    }

    [Fact]
    public async Task Refusals()
    {
        await using var h = await PublicApiTestHost.StartAsync(Quota(3, null));
        var (aliceId, host) = await h.HostTokenAsync("alice", "rates:read", "banks:read");
        var session = (await h.LoginAsync("alice", "pass1234")).Token;
        var cli = (await h.Service<IApiTokenManager>().CreateAsync(aliceId, new ApiTokenCreateOptions { Name = "cli" })).Token;
        var banksOnly = await h.NewHostTokenAsync(aliceId, "banks", "banks:read");

        await AssertBodyAsync(await h.GetAsync("/v1/rates"), 401, "unauthorized");
        await AssertBodyAsync(await h.GetAsync("/v1/rates", "qcli_pat_unknown"), 401, "invalid or expired token");
        await AssertBodyAsync(await h.GetAsync("/v1/rates", session), 403, "forbidden: a host token is required");
        await AssertBodyAsync(await h.GetAsync("/v1/rates", cli), 403, "forbidden: a host token is required");
        var missing = await h.GetAsync("/v1/rates", banksOnly);
        await AssertBodyAsync(missing, 403, "forbidden: missing permission rates:read");
        Assert.Null(Header(missing, "X-RateLimit-Limit"));

        Assert.Equal(0, await h.Service<IPublicApiUsageStore>().GetAccountCountAsync(aliceId, Day, CancellationToken.None));
        Assert.Equal(200, (int)(await h.GetAsync("/v1/open", banksOnly)).StatusCode);
        Assert.Equal(200, (int)(await h.GetAsync("/v1/rates", host)).StatusCode);
    }

    [Fact]
    public async Task Daily_HeadersAndLimit()
    {
        await using var h = await PublicApiTestHost.StartAsync(Quota(3, null));
        var (_, token) = await h.HostTokenAsync("alice");

        foreach (var remaining in new[] { "2", "1", "0" })
        {
            var ok = await h.GetAsync("/v1/rates", token);
            Assert.Equal(200, (int)ok.StatusCode);
            Assert.Equal("3", Header(ok, "X-RateLimit-Limit"));
            Assert.Equal(remaining, Header(ok, "X-RateLimit-Remaining"));
            Assert.Equal("30", Header(ok, "X-RateLimit-Reset"));
            Assert.Null(Header(ok, "Retry-After"));
        }

        var refused = await h.GetAsync("/v1/rates", token);
        await AssertBodyAsync(refused, 429, "quota exceeded: 3 requests per day");
        Assert.Equal("30", Header(refused, "Retry-After"));
        Assert.Equal("0", Header(refused, "X-RateLimit-Remaining"));
        Assert.Equal("3", Header(refused, "X-RateLimit-Limit"));
    }

    [Fact]
    public async Task Minute_Limit()
    {
        await using var h = await PublicApiTestHost.StartAsync(Quota(null, 2));
        var (_, token) = await h.HostTokenAsync("alice");

        Assert.Equal("1", Header(await h.GetAsync("/v1/rates", token), "X-RateLimit-Remaining"));
        Assert.Equal("0", Header(await h.GetAsync("/v1/rates", token), "X-RateLimit-Remaining"));
        var refused = await h.GetAsync("/v1/rates", token);

        await AssertBodyAsync(refused, 429, "too many requests: 2 per minute");
        Assert.Equal("30", Header(refused, "Retry-After"));
        Assert.Equal("2", Header(refused, "X-RateLimit-Limit"));
        Assert.Equal("0", Header(refused, "X-RateLimit-Remaining"));
        Assert.Equal("30", Header(refused, "X-RateLimit-Reset"));
    }

    [Fact]
    public async Task Minute_Limit_NextMinuteAdmits()
    {
        await using var h = await PublicApiTestHost.StartAsync(Quota(null, 1));
        var (_, token) = await h.HostTokenAsync("alice");
        h.Time.SetUtcNow(new DateTimeOffset(2026, 11, 19, 12, 0, 59, 500, TimeSpan.Zero));

        Assert.Equal(200, (int)(await h.GetAsync("/v1/rates", token)).StatusCode);
        var refused = await h.GetAsync("/v1/rates", token);
        Assert.Equal(429, (int)refused.StatusCode);
        Assert.Equal("1", Header(refused, "Retry-After"));
        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(200, (int)(await h.GetAsync("/v1/rates", token)).StatusCode);
    }

    [Fact]
    public async Task BothLimits_HeadersDescribeTheDay()
    {
        await using var h = await PublicApiTestHost.StartAsync(Quota(100, 2));
        var (_, token) = await h.HostTokenAsync("alice");

        var first = await h.GetAsync("/v1/rates", token);
        Assert.Equal("100", Header(first, "X-RateLimit-Limit"));
        Assert.Equal("99", Header(first, "X-RateLimit-Remaining"));
        await h.GetAsync("/v1/rates", token);
        var refused = await h.GetAsync("/v1/rates", token);

        await AssertBodyAsync(refused, 429, "too many requests: 2 per minute");
        Assert.Equal("100", Header(refused, "X-RateLimit-Limit"));
        Assert.Equal("0", Header(refused, "X-RateLimit-Remaining"));
        Assert.Equal("30", Header(refused, "Retry-After"));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    public async Task ZeroLimit_Returns403(long limit)
    {
        await using var h = await PublicApiTestHost.StartAsync(Quota(limit, null));
        var (aliceId, token) = await h.HostTokenAsync("alice");

        var res = await h.GetAsync("/v1/rates", token);

        await AssertBodyAsync(res, 403, "forbidden: no API access");
        Assert.Null(Header(res, "X-RateLimit-Limit"));
        Assert.Equal(0, await h.Service<IPublicApiUsageStore>().GetAccountCountAsync(aliceId, Day, CancellationToken.None));
    }

    [Fact]
    public async Task ZeroMinuteLimit_Returns403()
    {
        await using var h = await PublicApiTestHost.StartAsync(Quota(null, 0));
        var (_, token) = await h.HostTokenAsync("alice");

        await AssertBodyAsync(await h.GetAsync("/v1/rates", token), 403, "forbidden: no API access");
    }

    [Fact]
    public async Task Quota_IsPerAccount()
    {
        await using var h = await PublicApiTestHost.StartAsync(Quota(3, null));
        var (aliceId, first) = await h.HostTokenAsync("alice");
        var second = await h.NewHostTokenAsync(aliceId, "second");
        var (_, bob) = await h.HostTokenAsync("bob");

        Assert.Equal(200, (int)(await h.GetAsync("/v1/rates", first)).StatusCode);
        Assert.Equal(200, (int)(await h.GetAsync("/v1/rates", first)).StatusCode);
        var third = await h.GetAsync("/v1/rates", second);
        Assert.Equal(200, (int)third.StatusCode);
        Assert.Equal("0", Header(third, "X-RateLimit-Remaining"));

        Assert.Equal(429, (int)(await h.GetAsync("/v1/rates", first)).StatusCode);
        Assert.Equal(429, (int)(await h.GetAsync("/v1/rates", second)).StatusCode);
        Assert.Equal(200, (int)(await h.GetAsync("/v1/rates", bob)).StatusCode);
    }

    [Fact]
    public async Task Quota_RevokedTokensStillCount()
    {
        await using var h = await PublicApiTestHost.StartAsync(Quota(2, null));
        var (aliceId, a) = await h.HostTokenAsync("alice");
        Assert.Equal(200, (int)(await h.GetAsync("/v1/rates", a)).StatusCode);
        Assert.Equal(200, (int)(await h.GetAsync("/v1/rates", a)).StatusCode);

        var tokens = h.Service<IApiTokenManager>();
        var aId = (await tokens.ListAsync(aliceId)).Single().Id;
        Assert.True(await tokens.RevokeAsync(aliceId, aId));
        var b = await h.NewHostTokenAsync(aliceId, "fresh");

        await AssertBodyAsync(await h.GetAsync("/v1/rates", b), 429, "quota exceeded: 2 requests per day");
    }

    [Fact]
    public async Task Quota_DayRollsOverAtUtcMidnight()
    {
        await using var h = await PublicApiTestHost.StartAsync(Quota(1, null));
        var (_, token) = await h.HostTokenAsync("alice");

        Assert.Equal(200, (int)(await h.GetAsync("/v1/rates", token)).StatusCode);
        var refused = await h.GetAsync("/v1/rates", token);
        Assert.Equal(429, (int)refused.StatusCode);
        Assert.Equal("30", Header(refused, "Retry-After"));

        h.Time.Advance(TimeSpan.FromSeconds(31)); // 2026-11-20T00:00:01Z
        var next = await h.GetAsync("/v1/rates", token);

        Assert.Equal(200, (int)next.StatusCode);
        Assert.Equal("0", Header(next, "X-RateLimit-Remaining"));
        Assert.Equal("86399", Header(next, "X-RateLimit-Reset"));
    }

    [Fact]
    public async Task Quota_ParallelRequests_AdmitExactlyTheLimit()
    {
        await using var h = await PublicApiTestHost.StartAsync(Quota(10, null));
        var (aliceId, token) = await h.HostTokenAsync("alice");

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => h.GetAsync("/v1/rates", token)));

        Assert.Equal(10, results.Count(r => (int)r.StatusCode == 200));
        Assert.Equal(40, results.Count(r => (int)r.StatusCode == 429));
        Assert.Equal(10, await h.Service<IPublicApiUsageStore>().GetAccountCountAsync(aliceId, Day, CancellationToken.None));
    }

    [Fact]
    public async Task QuotaResolverThrows_Returns500_NotCounted()
    {
        var broken = true;
        var logs = new CapturingLoggerProvider();
        await using var h = await PublicApiTestHost.StartAsync(
            o => o.UseQuotaResolver((_, _, _) => broken
                ? throw new InvalidOperationException("db down")
                : ValueTask.FromResult(new PublicApiQuota(3, null))),
            configureServices: s => s.AddSingleton<ILoggerProvider>(logs));
        var (_, token) = await h.HostTokenAsync("alice");

        var res = await h.GetAsync("/v1/rates", token);

        Assert.Equal(500, (int)res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal("""{"error":"internal server error"}""", body);
        Assert.DoesNotContain("db down", body);
        Assert.Null(Header(res, "X-RateLimit-Limit"));
        Assert.Contains(logs.Messages, m => m.Contains("quota", StringComparison.OrdinalIgnoreCase));

        broken = false;
        var next = await h.GetAsync("/v1/rates", token);
        Assert.Equal(200, (int)next.StatusCode);
        Assert.Equal("2", Header(next, "X-RateLimit-Remaining"));
    }

    [Fact]
    public async Task ResolverGetsTheCaller()
    {
        QcliCaller? seen = null;
        await using var h = await PublicApiTestHost.StartAsync(o => o.UseQuotaResolver((caller, _, _) =>
        {
            seen = caller;
            return ValueTask.FromResult(PublicApiQuota.Unlimited);
        }));
        var (aliceId, token) = await h.HostTokenAsync("alice");

        var res = await h.GetAsync("/v1/rates", token);

        Assert.NotNull(seen);
        Assert.Equal(aliceId, seen!.UserId);
        Assert.Equal("alice", seen.UserName);
        Assert.Equal(await res.Content.ReadAsStringAsync(), seen.TokenId);
        Assert.Equal("host", seen.Audience);
        Assert.Equal(["rates:read"], seen.Permissions);
    }

    [Fact]
    public async Task ScopedResolver_ResolvedPerRequest()
    {
        await using var h = await PublicApiTestHost.StartAsync(o => o.UseQuotaResolver<ScopedResolver>(),
            configureServices: s => s.AddScoped<ScopedQuotaSource>(), validateScopes: true);
        var (_, token) = await h.HostTokenAsync("alice");

        var res = await h.GetAsync("/v1/rates", token);

        Assert.Equal(200, (int)res.StatusCode);
        Assert.Equal("5", Header(res, "X-RateLimit-Limit"));
    }

    [Fact]
    public async Task Usage_OnTokenViews()
    {
        await using var h = await PublicApiTestHost.StartAsync(Quota(3, null));
        var (aliceId, a) = await h.HostTokenAsync("alice");
        await h.NewHostTokenAsync(aliceId, "second");
        for (var i = 0; i < 3; i++) Assert.Equal(200, (int)(await h.GetAsync("/v1/rates", a)).StatusCode);
        var session = (await h.LoginAsync("alice", "pass1234")).Token;

        var views = (await UsersTestHost.ReadJsonAsync(await h.GetAsync("tokens", session))).EnumerateArray()
            .ToDictionary(v => v.GetProperty("name").GetString()!, v => v.GetProperty("usage"));

        Assert.Equal(3, views["site"].GetProperty("today").GetInt64());
        Assert.Equal(3, views["site"].GetProperty("last30Days").GetInt64());
        var days = views["site"].GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(30, days.Count);
        Assert.Equal(Day, days[29].GetProperty("date").GetString());
        Assert.Equal("2026-10-21", days[0].GetProperty("date").GetString());
        Assert.Equal(0, views["second"].GetProperty("today").GetInt64());

        var account = await h.Service<IPublicApiUsage>().GetAccountUsageAsync(aliceId);
        Assert.Equal(3, account.Today);
        Assert.Equal(new PublicApiQuota(3, null), account.Quota);
        Assert.Equal(2, account.Tokens.Count);
        Assert.Equal(3, account.Tokens.Single(t => t.Name == "site").Usage.Today);
    }

    [Fact]
    public async Task Usage_UnknownUser_Throws404()
    {
        await using var h = await PublicApiTestHost.StartAsync();

        var ex = await Assert.ThrowsAsync<UsersApiException>(() =>
            h.Service<IPublicApiUsage>().GetAccountUsageAsync("nobody"));

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("user not found", ex.Message);
    }

    [Fact]
    public async Task Headers_OnHandlerErrors()
    {
        await using var h = await PublicApiTestHost.StartAsync(Quota(3, null));
        var (_, token) = await h.HostTokenAsync("alice");

        var res = await h.GetAsync("/v1/missing", token);

        Assert.Equal(404, (int)res.StatusCode);
        Assert.Equal("3", Header(res, "X-RateLimit-Limit"));
        Assert.Equal("2", Header(res, "X-RateLimit-Remaining"));
    }

    [Fact]
    public async Task MvcAttribute_GuardsLikeTheFilter()
    {
        await using var h = await PublicApiTestHost.StartAsync(Quota(1, null),
            configureMvc: mvc => mvc.AddApplicationPart(typeof(PublicRatesController).Assembly));
        var (_, token) = await h.HostTokenAsync("alice");
        var session = (await h.LoginAsync("alice", "pass1234")).Token;

        await AssertBodyAsync(await h.GetAsync("/v1/mvc-rates", session), 403, "forbidden: a host token is required");
        var ok = await h.GetAsync("/v1/mvc-rates", token);
        Assert.Equal(200, (int)ok.StatusCode);
        Assert.Equal("0", Header(ok, "X-RateLimit-Remaining"));
        await AssertBodyAsync(await h.GetAsync("/v1/mvc-rates", token), 429, "quota exceeded: 1 requests per day");
    }

    [Fact]
    public async Task OldUsage_IsPruned()
    {
        await using var h = await PublicApiTestHost.StartAsync(o => o.UsageRetentionDays = 31);
        var (aliceId, token) = await h.HostTokenAsync("alice");
        var store = h.Service<IPublicApiUsageStore>();
        await store.IncrementAsync("old-token", aliceId, "2026-09-01", CancellationToken.None);

        await h.GetAsync("/v1/rates", token);

        Assert.Equal(0, await store.GetAccountCountAsync(aliceId, "2026-09-01", CancellationToken.None));
        Assert.Equal(1, await store.GetAccountCountAsync(aliceId, Day, CancellationToken.None));
    }
}

/// <summary>MVC host route guarded with <see cref="RequirePublicApiAttribute"/>.</summary>
[Microsoft.AspNetCore.Mvc.ApiController]
[Microsoft.AspNetCore.Mvc.Route("v1/mvc-rates")]
public class PublicRatesController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [Microsoft.AspNetCore.Mvc.HttpGet]
    [RequirePublicApi("rates:read")]
    public Microsoft.AspNetCore.Mvc.IActionResult Get() => Ok(new { ok = true });
}
