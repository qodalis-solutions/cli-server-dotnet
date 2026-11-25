using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Plugin.Users;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests.Config;

internal static class ConfigApi
{
    public const string V1 = "/api/v1/qcli";

    public static CliStoredConfigProvider Billing(string? requiredPermission = null) => new(new CliStoredConfigProviderOptions
    {
        Id = requiredPermission != null ? "vault" : "billing",
        Title = requiredPermission != null ? "Vault" : "Billing",
        RequiredPermission = requiredPermission,
        Sections =
        [
            new()
            {
                Id = "invoices",
                Settings =
                [
                    new() { Key = "dueDays", Type = "number", Default = 30, Validation = new() { Min = 1, Max = 90 } },
                    new() { Key = "apiKey", Type = "secret" },
                    new() { Key = "currency", Type = "enum", Default = "EUR", Validation = new() { Options = ["EUR", "USD"] } },
                    new() { Key = "webhook", Type = "url", Default = "https://hooks.example/a" },
                    new() { Key = "region", Type = "string", Mutable = false, Default = "eu" },
                ],
            },
        ],
    });

    public static HttpRequestMessage Patch(string path, string json) =>
        new(HttpMethod.Patch, $"{V1}/config/{path}") { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static async Task<JsonElement> JsonOf(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    public static JsonElement Find(JsonElement array, string property, string value) =>
        array.EnumerateArray().Single(e => e.GetProperty(property).GetString() == value);
}

/// <summary>The config API (admin plugin) on an open server: the static admin of password mode may do everything.</summary>
public sealed class ConfigApiOpenServerTests : IAsyncLifetime
{
    private ConfigTestHost _host = null!;
    private readonly List<CliEvent> _events = [];

    public async Task InitializeAsync()
    {
        _host = await ConfigTestHost.StartAsync(cli =>
        {
            cli.SetAllowedOrigins("https://app.example");
            cli.AddMcp();
            cli.AddConfigProvider(ConfigApi.Billing());
            Qodalis.Cli.Plugin.Jobs.CliBuilderJobExtensions.ConfigureJobs(cli, _ => { });
        }, admin: true);
        _host.Service<ICliEventBus>().Subscribe("config:*", (e, _) => { _events.Add(e); return Task.CompletedTask; });
        await _host.SignInAdminAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private HttpClient Client => _host.Client;

    private Task<HttpResponseMessage> PatchAsync(string path, string json) => Client.SendAsync(ConfigApi.Patch(path, json));

    [Fact]
    public async Task Status401_WithoutTheAdminSession()
    {
        Client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(401, (int)(await Client.GetAsync($"{ConfigApi.V1}/config")).StatusCode);
        Assert.Equal(401, (int)(await PatchAsync("custom/custom", "{\"changes\":{\"a\":1}}")).StatusCode);
    }

    [Fact]
    public async Task GetConfig_EveryProviderInOrder_WithTheJsonShape()
    {
        var res = await Client.GetAsync($"{ConfigApi.V1}/config");
        Assert.Equal(200, (int)res.StatusCode);
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());
        var body = await ConfigApi.JsonOf(res);
        Assert.Equal(["defaultStore", "providers"], body.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("{\"kind\":\"memory\",\"title\":\"In memory\",\"detail\":\"\",\"persistent\":false}", body.GetProperty("defaultStore").GetRawText());
        var providers = body.GetProperty("providers");
        Assert.Equal(["server", "auth", "host", "cors", "mcp", "jobs", "logs", "billing", "custom"],
            providers.EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        var billing = ConfigApi.Find(providers, "id", "billing");
        Assert.Equal(["id", "title", "description", "order", "writable", "storage", "store", "sections"], billing.EnumerateObject().Select(p => p.Name));
        Assert.Equal("{\"kind\":\"store\",\"description\":\"Changes are saved to its config store\",\"sources\":[]}",
            billing.GetProperty("storage").GetRawText());
        Assert.Equal("{\"kind\":\"memory\",\"title\":\"In memory\",\"detail\":\"\",\"persistent\":false}", billing.GetProperty("store").GetRawText());
        var storage = providers.EnumerateArray().ToDictionary(p => p.GetProperty("id").GetString()!, p => p.GetProperty("storage"));
        Assert.Equal("{\"kind\":\"runtime\",\"description\":\"Read from the running server process\",\"sources\":[]}", storage["server"].GetRawText());
        Assert.Equal("configuration", storage["host"].GetProperty("kind").GetString());
        Assert.Equal("Server options", storage["host"].GetProperty("sources")[0].GetString());
        Assert.Equal(("options", 0), (storage["auth"].GetProperty("kind").GetString(), storage["auth"].GetProperty("sources").GetArrayLength()));
        foreach (var id in new[] { "cors", "mcp", "jobs", "logs", "custom" }) Assert.Equal("store", storage[id].GetProperty("kind").GetString());
        Assert.Equal(("Billing", "", 100, true),
            (billing.GetProperty("title").GetString(), billing.GetProperty("description").GetString(), billing.GetProperty("order").GetInt32(), billing.GetProperty("writable").GetBoolean()));
        var section = billing.GetProperty("sections")[0];
        Assert.Equal(["allowCustomKeys", "description", "entries", "id", "mutable", "title", "version"], section.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(("invoices", "invoices", true, "0", false),
            (section.GetProperty("id").GetString(), section.GetProperty("title").GetString(), section.GetProperty("mutable").GetBoolean(),
             section.GetProperty("version").GetString(), section.GetProperty("allowCustomKeys").GetBoolean()));
        Assert.Equal(
            "{\"key\":\"dueDays\",\"label\":\"dueDays\",\"description\":\"\",\"type\":\"number\",\"value\":30,\"default\":30,\"source\":\"default\",\"mutable\":true,\"requiresRestart\":false,\"sensitive\":false,\"validation\":{\"min\":1,\"max\":90}}",
            section.GetProperty("entries")[0].GetRawText());
        Assert.Equal(
            "{\"key\":\"apiKey\",\"label\":\"apiKey\",\"description\":\"\",\"type\":\"secret\",\"value\":null,\"default\":null,\"source\":\"default\",\"mutable\":true,\"requiresRestart\":false,\"sensitive\":true,\"isSet\":false}",
            section.GetProperty("entries")[1].GetRawText());
    }

    [Fact]
    public async Task GetProvider_OneProvider_404ForAnUnknownOne()
    {
        var server = await ConfigApi.JsonOf(await Client.GetAsync($"{ConfigApi.V1}/config/server"));
        Assert.Equal(("server", false), (server.GetProperty("id").GetString(), server.GetProperty("writable").GetBoolean()));
        Assert.Equal("runtime", server.GetProperty("storage").GetProperty("kind").GetString());
        Assert.False(server.TryGetProperty("store", out _));
        Assert.Equal("dotnet", ConfigApi.Find(server.GetProperty("sections")[0].GetProperty("entries"), "key", "platform").GetProperty("value").GetString());
        var missing = await Client.GetAsync($"{ConfigApi.V1}/config/nope");
        Assert.Equal(404, (int)missing.StatusCode);
        Assert.Equal("{\"error\":\"unknown config provider: nope\"}", await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_200WithTheSection_AppliedKeysAndVersion_409ForAStaleVersion()
    {
        var res = await PatchAsync("billing/invoices", "{\"changes\":{\"dueDays\":45,\"currency\":\"USD\"},\"version\":\"0\"}");
        Assert.Equal(200, (int)res.StatusCode);
        var body = await ConfigApi.JsonOf(res);
        Assert.Equal(("billing", "[\"dueDays\",\"currency\"]", false, "1", "1"),
            (body.GetProperty("provider").GetString(), body.GetProperty("applied").GetRawText(), body.GetProperty("requiresRestart").GetBoolean(),
             body.GetProperty("version").GetString(), body.GetProperty("section").GetProperty("version").GetString()));
        var due = ConfigApi.Find(body.GetProperty("section").GetProperty("entries"), "key", "dueDays");
        Assert.Equal((45, "runtime"), (due.GetProperty("value").GetInt32(), due.GetProperty("source").GetString()));
        var again = await ConfigApi.JsonOf(await Client.GetAsync($"{ConfigApi.V1}/config/billing"));
        Assert.Equal("USD", ConfigApi.Find(again.GetProperty("sections")[0].GetProperty("entries"), "key", "currency").GetProperty("value").GetString());

        var stale = await PatchAsync("billing/invoices", "{\"changes\":{\"dueDays\":50},\"version\":\"0\"}");
        Assert.Equal(409, (int)stale.StatusCode);
        Assert.Equal("{\"error\":\"version conflict\",\"version\":\"1\"}", await stale.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_400WithPerKeyErrors()
    {
        var res = await PatchAsync("billing/invoices", "{\"changes\":{\"dueDays\":0,\"currency\":\"GBP\",\"webhook\":\"not-a-url\",\"region\":\"us\",\"unknown\":1}}");
        Assert.Equal(400, (int)res.StatusCode);
        Assert.Equal(
            "{\"error\":\"validation failed\",\"errors\":{\"dueDays\":\"must be at least 1\",\"currency\":\"must be one of: EUR, USD\",\"webhook\":\"must be an absolute URL\",\"region\":\"read-only\",\"unknown\":\"unknown setting\"}}",
            await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_400ForBadBodiesAndReadOnlyProviders_404ForUnknownSections()
    {
        async Task Expect(string path, string json, int status, string? body)
        {
            var res = await PatchAsync(path, json);
            Assert.Equal(status, (int)res.StatusCode);
            if (body != null) Assert.Equal(body, await res.Content.ReadAsStringAsync());
        }
        await Expect("billing/invoices", "{\"dueDays\":3}", 400, "{\"error\":\"changes must be a non-empty object\"}");
        await Expect("billing/invoices", "[1]", 400, "{\"error\":\"changes must be a non-empty object\"}");
        await Expect("billing/invoices", "{\"changes\":{\"dueDays\":3},\"version\":3}", 400, "{\"error\":\"version must be a string\"}");
        await Expect("server/runtime", "{\"changes\":{\"pid\":1}}", 400, "{\"error\":\"config provider server is read-only\"}");
        await Expect("billing/nope", "{\"changes\":{\"a\":1}}", 404, "{\"error\":\"unknown config section: billing.nope\"}");
        await Expect("nope/x", "{\"changes\":{\"a\":1}}", 404, null);
    }

    [Fact]
    public async Task Secrets_SetAndReplaced_NeverReturned_ClearedWithNull()
    {
        Assert.Equal(200, (int)(await PatchAsync("billing/invoices", "{\"changes\":{\"apiKey\":\"sk_live_top_secret\"}}")).StatusCode);
        var text = await Client.GetStringAsync($"{ConfigApi.V1}/config");
        Assert.DoesNotContain("sk_live_top_secret", text);
        var billing = ConfigApi.Find(JsonDocument.Parse(text).RootElement.GetProperty("providers"), "id", "billing");
        var entry = ConfigApi.Find(billing.GetProperty("sections")[0].GetProperty("entries"), "key", "apiKey");
        Assert.Equal((JsonValueKind.Null, true), (entry.GetProperty("value").ValueKind, entry.GetProperty("isSet").GetBoolean()));
        var replaced = await PatchAsync("billing/invoices", "{\"changes\":{\"apiKey\":\"sk_live_other\"}}");
        Assert.DoesNotContain("sk_live_other", await replaced.Content.ReadAsStringAsync());
        var cleared = await ConfigApi.JsonOf(await PatchAsync("billing/invoices", "{\"changes\":{\"apiKey\":null}}"));
        Assert.False(ConfigApi.Find(cleared.GetProperty("section").GetProperty("entries"), "key", "apiKey").GetProperty("isSet").GetBoolean());
    }

    [Fact]
    public async Task Custom_AddChangeRemoveKeys()
    {
        var before = await ConfigApi.JsonOf(await Client.GetAsync($"{ConfigApi.V1}/config/custom"));
        var section = before.GetProperty("sections")[0];
        Assert.Equal(("custom", true, "0", 0),
            (section.GetProperty("id").GetString(), section.GetProperty("allowCustomKeys").GetBoolean(), section.GetProperty("version").GetString(), section.GetProperty("entries").GetArrayLength()));
        Assert.Equal(200, (int)(await PatchAsync("custom/custom", "{\"changes\":{\"theme\":\"dark\",\"maxLogSize\":500},\"version\":\"0\"}")).StatusCode);
        var res = await ConfigApi.JsonOf(await PatchAsync("custom/custom", "{\"changes\":{\"theme\":null},\"version\":\"1\"}"));
        var entry = Assert.Single(res.GetProperty("section").GetProperty("entries").EnumerateArray());
        Assert.Equal(("maxLogSize", 500), (entry.GetProperty("key").GetString(), entry.GetProperty("value").GetInt32()));
    }

    [Fact]
    public async Task PublishesConfigChanged_OnEveryChange()
    {
        Assert.Equal(200, (int)(await PatchAsync("billing/invoices", "{\"changes\":{\"dueDays\":20}}")).StatusCode);
        var e = Assert.Single(_events);
        Assert.Equal(("config:changed", "config:read"), (e.Type, e.Audience.Permission));
        var data = JsonSerializer.Serialize(e.Data);
        Assert.Contains("\"provider\":\"billing\"", data);
        Assert.Contains("\"keys\":[\"dueDays\"]", data);
        Assert.Contains("\"user\":\"admin\"", data);
    }

    [Fact]
    public async Task TheOldPut_IsGone()
    {
        var res = await Client.PutAsJsonAsync($"{ConfigApi.V1}/config", new { auth = new { username = "x" } });
        Assert.True((int)res.StatusCode is 404 or 405, $"status {(int)res.StatusCode}");
    }
}

/// <summary>The config API on a server protected by the users plugin: config:read / config:write, a provider's requiredPermission, audit.</summary>
public sealed class ConfigApiProtectedServerTests : IAsyncLifetime
{
    private UsersTestHost _h = null!;
    private string _root = null!, _nora = null!, _rita = null!, _will = null!, _vera = null!;

    public async Task InitializeAsync()
    {
        _h = await UsersTestHost.StartAsync(
            o => o.MinPasswordLength = 4,
            configureCli: cli =>
            {
                cli.AddConfigProvider(ConfigApi.Billing());
                cli.AddConfigProvider(ConfigApi.Billing("vault:admin"));
                cli.AddAdmin();
            },
            configureApp: app => app.UseQodalisAdmin());
        _root = await _h.RootTokenAsync();
        _nora = await _h.UserWithPermissionsAsync(_root, "nora", "admin:access");
        _rita = await _h.UserWithPermissionsAsync(_root, "rita", "admin:access", "config:read");
        _will = await _h.UserWithPermissionsAsync(_root, "will", "admin:access", "config:read", "config:write");
        _vera = await _h.UserWithPermissionsAsync(_root, "vera", "admin:access", "config:read", "config:write", "vault:admin");
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<HttpResponseMessage> PatchAsync(string path, string token, object body) =>
        _h.SendAsync(HttpMethod.Patch, $"config/{path}", token, body);

    [Fact]
    public async Task Status401WithoutASession_403WithoutConfigRead()
    {
        Assert.Equal(401, (int)(await _h.GetAsync("config")).StatusCode);
        Assert.Contains("config:read", await UsersTestHost.AssertErrorAsync(await _h.GetAsync("config", _nora), 403));
    }

    [Fact]
    public async Task ConfigRead_SeesTheProviders_NotThoseNeedingAnotherPermission_WritableIsFalse()
    {
        var providers = (await UsersTestHost.ReadJsonAsync(await _h.GetAsync("config", _rita))).GetProperty("providers");
        var ids = providers.EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
        Assert.Contains("billing", ids);
        Assert.Contains("users", ids);
        Assert.DoesNotContain("vault", ids);
        Assert.All(providers.EnumerateArray(), p => Assert.False(p.GetProperty("writable").GetBoolean()));
        Assert.Equal(403, (int)(await _h.GetAsync("config/vault", _rita)).StatusCode);
    }

    [Fact]
    public async Task Patch_NeedsConfigWrite_ARequiredPermissionGuardsTheProviderToo()
    {
        Assert.Contains("config:write", await UsersTestHost.AssertErrorAsync(
            await PatchAsync("billing/invoices", _rita, new { changes = new { dueDays = 5 } }), 403));
        var res = await PatchAsync("billing/invoices", _will, new { changes = new { dueDays = 5 }, version = "0" });
        Assert.Equal(200, (int)res.StatusCode);
        Assert.Equal("1", (await UsersTestHost.ReadJsonAsync(res)).GetProperty("version").GetString());
        Assert.Equal(403, (int)(await PatchAsync("vault/invoices", _will, new { changes = new { dueDays = 5 } })).StatusCode);
        Assert.Equal(200, (int)(await PatchAsync("vault/invoices", _vera, new { changes = new { dueDays = 6 } })).StatusCode);
        Assert.True((await UsersTestHost.ReadJsonAsync(await _h.GetAsync("config/billing", _will))).GetProperty("writable").GetBoolean());
    }

    [Fact]
    public async Task AuditsConfigUpdate_InTheAuditLog_SecretsMasked()
    {
        Assert.Equal(200, (int)(await PatchAsync("billing/invoices", _will, new { changes = new { apiKey = "never-log-me", dueDays = 9 } })).StatusCode);
        var entries = await _h.Service<IAuditStore>().GetEntriesAsync(new AuditQueryOptions { Event = "config.update" });
        var entry = Assert.Single(entries);
        Assert.Equal(("config.update", "will", "will", "config:billing.invoices"), (entry.Event, entry.Username, entry.Actor, entry.Target));
        var properties = JsonSerializer.Serialize(entry.Properties);
        Assert.Contains("\"changes\":[{\"key\":\"apiKey\",\"old\":null,\"new\":\"***\"},{\"key\":\"dueDays\",\"old\":30,\"new\":9}]", properties);
        Assert.Contains("\"via\":\"api\"", properties);
        Assert.DoesNotContain("never-log-me", JsonSerializer.Serialize(entries));
    }

    [Fact]
    public async Task TheUsersProvider_ChangesTheSessionTimeoutOfTheRunningPlugin()
    {
        var view = await UsersTestHost.ReadJsonAsync(await _h.GetAsync("config/users", _root));
        var sessions = ConfigApi.Find(view.GetProperty("sections"), "id", "sessions");
        Assert.Equal("30m", ConfigApi.Find(sessions.GetProperty("entries"), "key", "sessionTimeout").GetProperty("value").GetString());
        Assert.Equal(200, (int)(await PatchAsync("users/sessions", _root, new { changes = new { sessionTimeout = "2h" }, version = sessions.GetProperty("version").GetString() })).StatusCode);
        Assert.Equal(7_200_000, _h.Service<UsersModuleOptions>().EffectiveSessionTimeoutMs);
        var bad = await PatchAsync("users/sessions", _root, new { changes = new { sessionTimeout = "10s" } });
        Assert.Equal(400, (int)bad.StatusCode);
        Assert.Equal("must be at least 1m", (await UsersTestHost.ReadJsonAsync(bad)).GetProperty("errors").GetProperty("sessionTimeout").GetString());
        Assert.Equal("config section users.access is read-only",
            await UsersTestHost.AssertErrorAsync(await PatchAsync("users/access", _root, new { changes = new { protectServer = false } }), 400));
        Assert.Equal(200, (int)(await PatchAsync("users/sessions", _root, new { changes = new Dictionary<string, object?> { ["sessionTimeout"] = null } })).StatusCode);
        Assert.Equal(30 * 60_000, _h.Service<UsersModuleOptions>().EffectiveSessionTimeoutMs);
    }

    [Fact]
    public async Task TheAuthProvider_ReportsTheUsersSignInMode()
    {
        var view = await UsersTestHost.ReadJsonAsync(await _h.GetAsync("config/auth", _rita));
        Assert.Equal("users", ConfigApi.Find(view.GetProperty("sections")[0].GetProperty("entries"), "key", "mode").GetProperty("value").GetString());
    }
}
