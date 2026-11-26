using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Config;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Config;
using Host = Qodalis.Cli.Tests.MarketplaceTestHost;

namespace Qodalis.Cli.Tests;

/// <summary>
/// Settings of dashboard plugins from the marketplace (admin plugin): the <c>dashboard-plugins</c> config provider with a
/// section per installed plugin with settings (entries and defaults from the version's <c>config.entries</c>),
/// install-time values checked before anything is downloaded, values kept or dropped on update, deleted on remove,
/// restored after a restart from installed.json (offline), invalid or secret settings from the marketplace refused, GET
/// /installed/{name}/config, <c>config set</c> through the existing command, <c>config:changed</c>, the store
/// resolution, permissions, and <c>admin dashboard install|update --config</c> and <c>admin dashboard info</c>. No network.
/// </summary>
public sealed class DashboardPluginConfigTests : IAsyncLifetime
{
    private const string Market = FakeMarketplaceNet.Market;
    private const string V1 = "/api/v1/qcli";
    private const string Base = MarketplaceTestHost.Base;
    private const string ConfigApi = V1 + "/config";
    private const string Section = ConfigApi + "/dashboard-plugins/tickets";
    private const string Tickets = "@acme/cli-dashboard-tickets";

    /// <summary>The settings of the tickets page, version 1.</summary>
    private static JsonNode SettingsV1 => JsonNode.Parse("""
        {"entries":[
          {"key":"refreshSeconds","label":"Refresh every","description":"Seconds between two refreshes","type":"number","default":30,"validation":{"min":5,"max":3600}},
          {"key":"columns","type":"string[]","default":["name","status"]},
          {"key":"mode","type":"enum","default":"compact","validation":{"options":["compact","full"]}}
        ]}
        """)!;

    /// <summary>Version 2: refreshSeconds at most 30, mode gone, columns unchanged, theme new.</summary>
    private static JsonNode SettingsV2 => JsonNode.Parse("""
        {"entries":[
          {"key":"refreshSeconds","label":"Refresh every","type":"number","default":15,"validation":{"min":5,"max":30}},
          {"key":"columns","type":"string[]","default":["name"]},
          {"key":"theme","type":"string","default":"light","validation":{"options":["light","dark"]}}
        ]}
        """)!;

    private readonly MarketplaceTestServers _servers = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _servers.DisposeAsync();

    private Task<Host> StartAsync(FakeMarketplaceNet? net = null, string? dir = null, Action<Extensions.CliBuilder>? configure = null) =>
        _servers.StartAsync(net, dir, configure: configure);

    private static void PublishTickets(FakeMarketplaceNet net, params FakeVersion[] versions) =>
        net.Publish(Tickets, versions.Length > 0 ? versions : [new FakeVersion("1.0.0", Config: SettingsV1)], title: "Tickets", icon: "🎫");

    private static FakeVersion Version(string version, JsonNode? config = null) => new(version, Config: config);

    private static List<string> CdnCalls(FakeMarketplaceNet net) => net.CdnCalls();

    private static async Task<JsonNode> Json(HttpResponseMessage res) => JsonNode.Parse(await res.Content.ReadAsStringAsync())!;

    private static string J(string json) => JsonNode.Parse(json)!.ToJsonString();

    private static Task<HttpResponseMessage> Install(Host h, object body) => h.Post(body);

    private static async Task<string> Values(Host h, string name = Tickets) =>
        (await Json(await h.Get($"{Base}/installed/{name}/config"))).ToJsonString();

    private static async Task<JsonNode> ProviderOf(Host h) => await Json(await h.Get($"{ConfigApi}/dashboard-plugins"));

    private static async Task<JsonArray> Sections(Host h) => (await ProviderOf(h))["sections"]!.AsArray();

    private static JsonNode InstalledJson(Host h) => JsonNode.Parse(File.ReadAllText(Path.Combine(h.Dir, "installed.json")))!;

    private static List<string> ConfigEventData(Host h)
    {
        lock (h.ConfigEvents) return h.ConfigEvents.Select(e => System.Text.Json.JsonSerializer.Serialize(e.Data)).ToList();
    }

    private static string Data(string json) => J(json);

    // ---------------------------------------------------------------- the marketplace record

    [Fact]
    public void ParsesAConfig_KnownFieldsKept_DefaultsFilledWithNull_NoEntriesIsNoSettings()
    {
        var config = DashboardPluginConfigs.Parse(JsonNode.Parse("""
            {"entries":[
              {"key":"a","type":"string","extra":1},
              {"key":"b.c-d_e","label":"B","description":"The b","type":"duration","default":"30s","validation":{"min":1000,"unknown":true}},
              {"key":"u","type":"url","default":"https://x.io"},
              {"key":"j","type":"json","default":{"x":[1]}},
              {"key":"f","type":"boolean","default":false}
            ],"other":true}
            """));
        Assert.Equal(J("""
            {"entries":[
              {"key":"a","type":"string","default":null},
              {"key":"b.c-d_e","label":"B","description":"The b","type":"duration","default":"30s","validation":{"min":1000}},
              {"key":"u","type":"url","default":"https://x.io"},
              {"key":"j","type":"json","default":{"x":[1]}},
              {"key":"f","type":"boolean","default":false}
            ]}
            """), System.Text.Json.JsonSerializer.Serialize(config));
        Assert.Null(DashboardPluginConfigs.Parse(null));
        Assert.Null(DashboardPluginConfigs.Parse(JsonNode.Parse("""{"entries":[]}""")));
        Assert.Null(DashboardPluginConfigs.Parse(new JsonObject()));
    }

    [Fact]
    public void RefusesASecretEntryAndEveryMalformedConfig_WithTheReason()
    {
        static string Entry(string extra) => $$$"""{"entries":[{"key":"k","type":"string"{{{extra}}}}]}""";
        var many = new JsonObject
        {
            ["entries"] = new JsonArray(Enumerable.Range(0, DashboardPluginConfigs.MaxEntries + 1)
                .Select(i => (JsonNode?)new JsonObject { ["key"] = $"k{i}", ["type"] = "string" }).ToArray()),
        };
        var cases = new (JsonNode Config, string Reason)[]
        {
            (JsonNode.Parse("""{"entries":[{"key":"k","type":"secret"}]}""")!, "k: secret settings are not allowed (a dashboard plugin runs in the browser)"),
            (JsonValue.Create("text"), "config must be an object"),
            (new JsonArray(), "config must be an object"),
            (JsonNode.Parse("""{"entries":"x"}""")!, "config.entries must be a list"),
            (many, "config has more than 50 entries"),
            (JsonNode.Parse($$$"""{"entries":[{"key":"k","type":"string","default":"{{{new string('x', 33 * 1024)}}}"}]}""")!, "config is larger than 32 KB"),
            (JsonNode.Parse("""{"entries":["k"]}""")!, "every config entry must be an object"),
            (JsonNode.Parse("""{"entries":[{"key":"1abc","type":"string"}]}""")!, "invalid config key: 1abc"),
            (JsonNode.Parse("""{"entries":[{"key":"a b","type":"string"}]}""")!, "invalid config key: a b"),
            (JsonNode.Parse($$$"""{"entries":[{"key":"{{{new string('x', 65)}}}","type":"string"}]}""")!, $"invalid config key: {new string('x', 65)}"),
            (JsonNode.Parse("""{"entries":[{"key":"k","type":"string"},{"key":"k","type":"number"}]}""")!, "duplicate config key: k"),
            (JsonNode.Parse("""{"entries":[{"key":"k","type":"color"}]}""")!, "k: unknown type color"),
            (JsonNode.Parse(Entry(""","label":3"""))!, "k: label must be a string"),
            (JsonNode.Parse(Entry(""","validation":"x" """))!, "k: validation must be an object"),
            (JsonNode.Parse(Entry(""","validation":{"min":"1"}"""))!, "k: validation.min must be a number"),
            (JsonNode.Parse(Entry(""","validation":{"min":5,"max":1}"""))!, "k: validation.min is above validation.max"),
            (JsonNode.Parse(Entry(""","validation":{"pattern":"(["}"""))!, "k: validation.pattern must be a regular expression"),
            (JsonNode.Parse(Entry(""","validation":{"options":[1]}"""))!, "k: validation.options must be a list of strings"),
            (JsonNode.Parse(Entry(""","validation":{"required":"yes"}"""))!, "k: validation.required must be true or false"),
            (JsonNode.Parse("""{"entries":[{"key":"k","type":"enum","default":"a"}]}""")!, "k: an enum needs validation.options"),
            (JsonNode.Parse("""{"entries":[{"key":"k","type":"enum","default":"c","validation":{"options":["a","b"]}}]}""")!, "k: the default must be one of: a, b"),
            (JsonNode.Parse("""{"entries":[{"key":"k","type":"number","default":1,"validation":{"min":5}}]}""")!, "k: the default must be at least 5"),
            (JsonNode.Parse("""{"entries":[{"key":"k","type":"number","default":"5"}]}""")!, "k: the default must be a number"),
            (JsonNode.Parse(Entry(""","validation":{"required":true}"""))!, "k: the default is required"),
            (JsonNode.Parse("""{"entries":[{"key":"k","type":"url","default":"not a url"}]}""")!, "k: the default must be an absolute URL"),
        };
        // JSON the parser cannot read
        cases = [.. cases, (JsonNode.Parse("""{"entries":[{"key":"k","type":"string","type":"number"}]}""")!, "config must be JSON")];
        foreach (var (config, reason) in cases)
            Assert.Equal(reason, Assert.Throws<DashboardPluginConfigException>(() => DashboardPluginConfigs.Parse(config)).Message);
    }

    [Fact]
    public void ChecksValuesAgainstTheSettings_UnknownKeysTypesAndRules_NullKeepsTheDefault()
    {
        var config = DashboardPluginConfigs.Parse(SettingsV1);
        static Dictionary<string, JsonNode?> Of(string json) => JsonNode.Parse(json)!.AsObject().ToDictionary(p => p.Key, p => p.Value?.DeepClone());
        Assert.Empty(DashboardPluginConfigs.ValidateValues(config, Of("""{"refreshSeconds":60,"columns":["a"],"mode":"full"}""")));
        Assert.Empty(DashboardPluginConfigs.ValidateValues(config, Of("""{"refreshSeconds":null}""")));
        Assert.Equal(new Dictionary<string, string>
        {
            ["refreshSeconds"] = "must be at least 5",
            ["columns"] = "must be a list of strings",
            ["mode"] = "must be one of: compact, full",
            ["nope"] = "unknown setting",
        }, DashboardPluginConfigs.ValidateValues(config, Of("""{"refreshSeconds":1,"columns":"a","mode":"huge","nope":1}""")));
        Assert.Equal(new Dictionary<string, string> { ["a"] = "unknown setting" }, DashboardPluginConfigs.ValidateValues(null, Of("""{"a":1}""")));
    }

    // ---------------------------------------------------------------- install

    [Fact]
    public async Task Install_CreatesThePluginsSection_WithTheVersionsEntriesAndTheirDefaults()
    {
        var net = new FakeMarketplaceNet();
        PublishTickets(net);
        var h = await StartAsync(net);
        var res = await Install(h, new { name = Tickets });
        Assert.Equal(201, (int)res.StatusCode);
        var body = await Json(res);
        Assert.Equal((Tickets, "tickets", true), (body["name"]!.GetValue<string>(), body["id"]!.GetValue<string>(), body["hasConfig"]!.GetValue<bool>()));
        Assert.Null(body["warnings"]);

        var provider = await ProviderOf(h);
        Assert.Equal(("dashboard-plugins", "Dashboard plugins", true, "store", "memory"),
            (provider["id"]!.GetValue<string>(), provider["title"]!.GetValue<string>(), provider["writable"]!.GetValue<bool>(),
                provider["storage"]!["kind"]!.GetValue<string>(), provider["store"]!["kind"]!.GetValue<string>()));
        Assert.Equal(J($$$"""
            [{
              "id":"tickets","title":"Tickets","description":"{{{Tickets}}}@1.0.0","mutable":true,"version":"0","allowCustomKeys":false,
              "entries":[
                {"key":"refreshSeconds","label":"Refresh every","description":"Seconds between two refreshes","type":"number","value":30,"default":30,
                 "source":"default","origin":"default","mutable":true,"requiresRestart":false,"sensitive":false,"required":false,"validation":{"min":5,"max":3600}},
                {"key":"columns","label":"columns","description":"","type":"string[]","value":["name","status"],"default":["name","status"],
                 "source":"default","origin":"default","mutable":true,"requiresRestart":false,"sensitive":false,"required":false},
                {"key":"mode","label":"mode","description":"","type":"enum","value":"compact","default":"compact",
                 "source":"default","origin":"default","mutable":true,"requiresRestart":false,"sensitive":false,"required":false,"validation":{"options":["compact","full"]}}
              ]
            }]
            """), provider["sections"]!.ToJsonString());
        // the entries are kept with the plugin, for restarts
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(DashboardPluginConfigs.Parse(SettingsV1)),
            InstalledJson(h)["plugins"]![0]!["config"]!.ToJsonString());
        // the values the dashboard passes to the page
        var values = await h.Get($"{Base}/installed/{Tickets}/config");
        Assert.Equal(200, (int)values.StatusCode);
        Assert.Equal("no-store", values.Headers.CacheControl?.ToString());
        Assert.Equal(J("""{"values":{"refreshSeconds":30,"columns":["name","status"],"mode":"compact"},"version":"0"}"""),
            (await Json(values)).ToJsonString());
        // the name encoded works too
        Assert.Equal("0", (await Json(await h.Get($"{Base}/installed/{Uri.EscapeDataString(Tickets)}/config")))["version"]!.GetValue<string>());
    }

    [Fact]
    public async Task APluginWithoutSettings_GetsNoSection_ItsConfigIsEmptyValuesAndANullVersion()
    {
        var net = new FakeMarketplaceNet();
        PublishTickets(net, Version("1.0.0"));
        var h = await StartAsync(net);
        Assert.False((await Json(await Install(h, new { name = Tickets })))["hasConfig"]!.GetValue<bool>());
        Assert.Empty(await Sections(h));
        Assert.Equal(J("""{"values":{},"version":null}"""), await Values(h));
        Assert.Null(InstalledJson(h)["plugins"]![0]!["config"]);
        // the settings were looked up in the version record: the details had none
        Assert.Contains($"{Market}/api/v1/qcli/marketplace/plugins/{Uri.EscapeDataString(Tickets)}/versions/1.0.0", net.Calls);
    }

    [Fact]
    public async Task GetPluginConfig_404NotInstalled_400InvalidName_401WithoutSignIn()
    {
        var h = await StartAsync();
        Assert.Equal(404, (int)(await h.Get($"{Base}/installed/{Tickets}/config")).StatusCode);
        Assert.Equal(400, (int)(await h.Get($"{Base}/installed/..%2F..%2Fetc/config")).StatusCode);
        Assert.Equal(401, (int)(await h.Get($"{Base}/installed/{Tickets}/config", auth: false)).StatusCode);
        Assert.Equal(404, (int)(await h.Get($"{Base}/installed/{Tickets}/other")).StatusCode);
    }

    [Fact]
    public async Task ReadsTheSettingsFromTheVersionRecord_WhenTheDetailsLeaveThemOut()
    {
        var net = new FakeMarketplaceNet { ConfigOnlyInVersionRecord = true };
        PublishTickets(net);
        var h = await StartAsync(net);
        Assert.True((await Json(await Install(h, new { name = Tickets })))["hasConfig"]!.GetValue<bool>());
        Assert.Equal("compact", JsonNode.Parse(await Values(h))!["values"]!["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Install_AppliesValidValues_Stored_Audited_ConfigChangedPublished()
    {
        var net = new FakeMarketplaceNet();
        PublishTickets(net);
        var h = await StartAsync(net);
        var res = await Install(h, JsonNode.Parse($$$"""{"name":"{{{Tickets}}}","config":{"refreshSeconds":60,"mode":"full","columns":null}}""")!);
        Assert.Equal(201, (int)res.StatusCode);
        Assert.Equal(J("""{"values":{"refreshSeconds":60,"columns":["name","status"],"mode":"full"},"version":"1"}"""), await Values(h));
        var section = (await Sections(h))[0]!;
        Assert.Equal(["refreshSeconds 60 runtime", "columns [\"name\",\"status\"] default", "mode \"full\" runtime"],
            section["entries"]!.AsArray().Select(e => $"{e!["key"]} {e["value"]!.ToJsonString()} {e["source"]}"));
        var events = h.ConfigEvents.ToList();
        var e = Assert.Single(events);
        Assert.Equal("config:changed", e.Type);
        Assert.Equal("config:read", e.Audience?.Permission);
        Assert.Equal(Data("""{"provider":"dashboard-plugins","section":"tickets","keys":["refreshSeconds","mode"],"version":"1","requiresRestart":false,"store":"memory","user":"admin"}"""),
            ConfigEventData(h)[0]);
        var audit = h.Audit.Entries.Single(a => a.Event == "dashboard.plugin.install");
        Assert.Equal(J($$$"""{"name":"{{{Tickets}}}","version":"1.0.0","id":"tickets","config":["refreshSeconds","mode"]}"""),
            System.Text.Json.JsonSerializer.Serialize(audit.Properties));
    }

    [Fact]
    public async Task Install_RefusesInvalidValuesWith400PerKey_BeforeAnythingIsDownloadedOrStored()
    {
        var net = new FakeMarketplaceNet();
        PublishTickets(net);
        net.Publish("@acme/cli-dashboard-plain", [Version("1.0.0")]);
        var h = await StartAsync(net);
        var res = await Install(h, JsonNode.Parse($$$"""{"name":"{{{Tickets}}}","config":{"refreshSeconds":1,"columns":"name","mode":"huge","nope":true}}""")!);
        Assert.Equal(400, (int)res.StatusCode);
        Assert.Equal(J("""
            {"error":"validation failed","errors":{"refreshSeconds":"must be at least 5","columns":"must be a list of strings","mode":"must be one of: compact, full","nope":"unknown setting"}}
            """), (await Json(res)).ToJsonString());
        foreach (var config in new JsonNode[] { JsonValue.Create("{\"a\":1}"), new JsonArray(1), JsonValue.Create(3) })
        {
            var bad = await Install(h, new JsonObject { ["name"] = Tickets, ["config"] = config });
            Assert.Equal(400, (int)bad.StatusCode);
            Assert.Equal(J("""{"error":"config must be an object"}"""), (await Json(bad)).ToJsonString());
        }
        // values for a plugin without settings
        var plain = await Install(h, JsonNode.Parse("""{"name":"@acme/cli-dashboard-plain","config":{"a":1}}""")!);
        Assert.Equal(400, (int)plain.StatusCode);
        Assert.Equal(J("""{"a":"unknown setting"}"""), (await Json(plain))["errors"]!.ToJsonString());

        Assert.Empty(CdnCalls(net));
        Assert.Equal("[]", (await Json(await h.Get($"{Base}/installed"))).ToJsonString());
        Assert.Empty(await Sections(h));
        Assert.Empty(h.ConfigEvents);
        Assert.Empty(h.Audit.Entries);
    }

    [Fact]
    public async Task ASecretEntryOrAMalformedConfigFromTheMarketplace_InstallsWithoutSettings_WithAWarning()
    {
        var net = new FakeMarketplaceNet();
        net.Publish("@acme/cli-dashboard-secret", [Version("1.0.0",
            JsonNode.Parse("""{"entries":[{"key":"token","type":"secret"},{"key":"ok","type":"string","default":"x"}]}"""))]);
        net.Publish("@acme/cli-dashboard-broken", [Version("2.0.0",
            JsonNode.Parse("""{"entries":[{"key":"n","type":"number","default":1,"validation":{"min":5}}]}"""))]);
        net.Publish("@acme/cli-dashboard-junk", [Version("3.0.0", JsonValue.Create("junk"))]);
        var h = await StartAsync(net);

        var secret = await Install(h, new { name = "@acme/cli-dashboard-secret" });
        Assert.Equal(201, (int)secret.StatusCode);
        var body = await Json(secret);
        Assert.False(body["hasConfig"]!.GetValue<bool>());
        Assert.Equal(J("""["the settings of @acme/cli-dashboard-secret@1.0.0 are invalid and were ignored: token: secret settings are not allowed (a dashboard plugin runs in the browser)"]"""),
            body["warnings"]!.ToJsonString());
        Assert.Equal(J("""["the settings of @acme/cli-dashboard-broken@2.0.0 are invalid and were ignored: n: the default must be at least 5"]"""),
            (await Json(await Install(h, new { name = "@acme/cli-dashboard-broken" })))["warnings"]!.ToJsonString());
        Assert.Equal(J("""["the settings of @acme/cli-dashboard-junk@3.0.0 are invalid and were ignored: config must be an object"]"""),
            (await Json(await Install(h, new { name = "@acme/cli-dashboard-junk" })))["warnings"]!.ToJsonString());
        Assert.Empty(await Sections(h));
        Assert.All(InstalledJson(h)["plugins"]!.AsArray(), p => Assert.Null(p!["config"]));
        Assert.Equal(J("""{"values":{},"version":null}"""), await Values(h, "@acme/cli-dashboard-secret"));
    }

    [Fact]
    public async Task ValuesGivenForARefusedConfig_AreUnknownSettings_400NothingInstalled()
    {
        var net = new FakeMarketplaceNet();
        net.Publish("@acme/cli-dashboard-secret", [Version("1.0.0", JsonNode.Parse("""{"entries":[{"key":"token","type":"secret"}]}"""))]);
        var h = await StartAsync(net);
        var res = await Install(h, JsonNode.Parse("""{"name":"@acme/cli-dashboard-secret","config":{"token":"abc"}}""")!);
        Assert.Equal(400, (int)res.StatusCode);
        Assert.Equal(J("""{"error":"validation failed","errors":{"token":"unknown setting"}}"""), (await Json(res)).ToJsonString());
        Assert.Empty(CdnCalls(net));
    }

    // ---------------------------------------------------------------- update and remove

    [Fact]
    public async Task Update_KeepsTheStoredValuesTheNewVersionStillAccepts_DropsTheOthers()
    {
        var net = new FakeMarketplaceNet();
        PublishTickets(net, Version("1.0.0", SettingsV1));
        var h = await StartAsync(net);
        Assert.Equal(201, (int)(await Install(h, JsonNode.Parse($$$"""{"name":"{{{Tickets}}}","config":{"refreshSeconds":60,"columns":["id","title"],"mode":"full"}}""")!)).StatusCode);
        PublishTickets(net, Version("1.0.0", SettingsV1), Version("2.0.0", SettingsV2));
        lock (h.ConfigEvents) h.ConfigEvents.Clear();

        var res = await h.Put(Tickets);
        Assert.Equal(200, (int)res.StatusCode);
        var body = await Json(res);
        Assert.Equal(("2.0.0", true, """["refreshSeconds","mode"]"""),
            (body["version"]!.GetValue<string>(), body["hasConfig"]!.GetValue<bool>(), body["configDropped"]!.ToJsonString()));
        // refreshSeconds 60 is above the new max, mode is gone; columns kept; theme new at its default
        Assert.Equal(J("""{"values":{"refreshSeconds":15,"columns":["id","title"],"theme":"light"},"version":"2"}"""), await Values(h));
        var section = (await Sections(h))[0]!;
        Assert.Equal($"{Tickets}@2.0.0", section["description"]!.GetValue<string>());
        Assert.Equal(["refreshSeconds:default", "columns:runtime", "theme:default"],
            section["entries"]!.AsArray().Select(e => $"{e!["key"]}:{e["source"]}"));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(DashboardPluginConfigs.Parse(SettingsV2)),
            InstalledJson(h)["plugins"]![0]!["config"]!.ToJsonString());
        Assert.Equal([Data("""{"provider":"dashboard-plugins","section":"tickets","keys":["refreshSeconds","mode"],"version":"2","requiresRestart":false,"store":"memory","user":"admin"}""")],
            ConfigEventData(h));
        Assert.Equal(J($$$"""{"name":"{{{Tickets}}}","version":"2.0.0","id":"tickets","previousVersion":"1.0.0","configDropped":["refreshSeconds","mode"]}"""),
            System.Text.Json.JsonSerializer.Serialize(h.Audit.Entries.Single(a => a.Event == "dashboard.plugin.update").Properties));
        // the extensions event carries the plugin only
        var changed = h.Events.Last();
        Assert.Equal("update", changed.Data["action"]);
        Assert.False(changed.Data.ContainsKey("configDropped"));
    }

    [Fact]
    public async Task Update_NothingToDrop_ConfigDroppedIsEmpty_NothingWritten()
    {
        var net = new FakeMarketplaceNet();
        PublishTickets(net, Version("1.0.0", SettingsV1), Version("1.1.0", SettingsV1));
        var h = await StartAsync(net);
        Assert.Equal(201, (int)(await Install(h, JsonNode.Parse($$$"""{"name":"{{{Tickets}}}","version":"1.0.0","config":{"refreshSeconds":90}}""")!)).StatusCode);
        lock (h.ConfigEvents) h.ConfigEvents.Clear();
        var body = await Json(await h.Put(Tickets));
        Assert.Equal(("1.1.0", "[]"), (body["version"]!.GetValue<string>(), body["configDropped"]!.ToJsonString()));
        Assert.Equal(J("""{"values":{"refreshSeconds":90,"columns":["name","status"],"mode":"compact"},"version":"1"}"""), await Values(h));
        Assert.Empty(h.ConfigEvents);
    }

    [Fact]
    public async Task Update_ValuesAreCheckedAgainstTheNewVersion_BeforeAnythingIsDownloaded_ThenApplied()
    {
        var net = new FakeMarketplaceNet();
        PublishTickets(net, Version("1.0.0", SettingsV1), Version("2.0.0", SettingsV2));
        var h = await StartAsync(net);
        Assert.Equal(201, (int)(await Install(h, JsonNode.Parse($$$"""{"name":"{{{Tickets}}}","version":"1.0.0","config":{"mode":"full"}}""")!)).StatusCode);
        var downloads = CdnCalls(net).Count;

        // mode exists in 1.0.0 only, theme in 2.0.0 only
        var bad = await h.Put(Tickets, JsonNode.Parse("""{"config":{"mode":"full","theme":"blue"}}""")!);
        Assert.Equal(400, (int)bad.StatusCode);
        Assert.Equal(J("""{"error":"validation failed","errors":{"mode":"unknown setting","theme":"must be one of: light, dark"}}"""), (await Json(bad)).ToJsonString());
        Assert.Equal(downloads, CdnCalls(net).Count);
        Assert.Equal("1.0.0", (await Json(await h.Get($"{Base}/installed")))[0]!["version"]!.GetValue<string>());
        Assert.Equal("full", JsonNode.Parse(await Values(h))!["values"]!["mode"]!.GetValue<string>());
        Assert.Equal(J("""{"error":"config must be an object"}"""), (await Json(await h.Put(Tickets, JsonNode.Parse("""{"config":"x"}""")!))).ToJsonString());

        var ok = await h.Put(Tickets, JsonNode.Parse("""{"config":{"theme":"dark","refreshSeconds":20}}""")!);
        Assert.Equal(200, (int)ok.StatusCode);
        var body = await Json(ok);
        Assert.Equal(("2.0.0", """["mode"]"""), (body["version"]!.GetValue<string>(), body["configDropped"]!.ToJsonString()));
        Assert.Equal(J("""{"refreshSeconds":20,"columns":["name"],"theme":"dark"}"""), JsonNode.Parse(await Values(h))!["values"]!.ToJsonString());
        var audit = h.Audit.Entries.Single(a => a.Event == "dashboard.plugin.update").Properties!;
        Assert.Equal(("""["theme","refreshSeconds"]""", """["mode"]"""),
            (System.Text.Json.JsonSerializer.Serialize(audit["config"]), System.Text.Json.JsonSerializer.Serialize(audit["configDropped"])));
    }

    [Fact]
    public async Task Update_AValueGivenForADroppedKeyKeepsIt_AndNullResetsASetting()
    {
        var net = new FakeMarketplaceNet();
        PublishTickets(net, Version("1.0.0", SettingsV1), Version("2.0.0", SettingsV2));
        var h = await StartAsync(net);
        Assert.Equal(201, (int)(await Install(h, JsonNode.Parse($$$"""{"name":"{{{Tickets}}}","version":"1.0.0","config":{"refreshSeconds":600,"columns":["a"]}}""")!)).StatusCode);
        var res = await Json(await h.Put(Tickets, JsonNode.Parse("""{"config":{"refreshSeconds":25,"columns":null}}""")!));
        Assert.Equal("[]", res["configDropped"]!.ToJsonString());
        Assert.Equal(J("""{"refreshSeconds":25,"columns":["name"],"theme":"light"}"""), JsonNode.Parse(await Values(h))!["values"]!.ToJsonString());
    }

    [Fact]
    public async Task Update_TheSameVersionWithValues_Applied_AnUpdateToAVersionWithoutSettingsRemovesTheSection()
    {
        var net = new FakeMarketplaceNet();
        PublishTickets(net, Version("1.0.0", SettingsV1));
        var h = await StartAsync(net);
        Assert.Equal(201, (int)(await Install(h, JsonNode.Parse($$$"""{"name":"{{{Tickets}}}","config":{"mode":"full"}}""")!)).StatusCode);
        var same = await Json(await h.Put(Tickets, JsonNode.Parse("""{"config":{"refreshSeconds":45}}""")!));
        Assert.Equal(("1.0.0", "[]"), (same["version"]!.GetValue<string>(), same["configDropped"]!.ToJsonString()));
        var values = JsonNode.Parse(await Values(h))!["values"]!;
        Assert.Equal((45, "full"), (values["refreshSeconds"]!.GetValue<int>(), values["mode"]!.GetValue<string>()));
        Assert.Equal(J("""{"refreshSeconds":"must be at least 5"}"""),
            (await Json(await h.Put(Tickets, JsonNode.Parse("""{"config":{"refreshSeconds":1}}""")!)))["errors"]!.ToJsonString());

        PublishTickets(net, Version("1.0.0", SettingsV1), Version("3.0.0"));
        var res = await Json(await h.Put(Tickets));
        Assert.Equal(("3.0.0", false, """["mode","refreshSeconds"]"""),
            (res["version"]!.GetValue<string>(), res["hasConfig"]!.GetValue<bool>(), res["configDropped"]!.ToJsonString()));
        Assert.Empty(await Sections(h));
        Assert.Equal(J("""{"values":{},"version":null}"""), await Values(h));
    }

    [Fact]
    public async Task Remove_DeletesTheSectionAndItsStoredValues_InstalledAgainItStartsFromTheDefaults()
    {
        var net = new FakeMarketplaceNet();
        PublishTickets(net);
        var store = new InMemoryConfigStore();
        var h = await StartAsync(net, configure: cli => cli.SetConfigStore(store, "dashboard-plugins"));
        Assert.Equal(201, (int)(await Install(h, JsonNode.Parse($$$"""{"name":"{{{Tickets}}}","config":{"refreshSeconds":60,"mode":"full"}}""")!)).StatusCode);
        Assert.Equal(J("""{"refreshSeconds":60,"mode":"full"}"""), (await store.ReadAsync("dashboard-plugins", "tickets")).ValuesToJson().ToJsonString());
        lock (h.ConfigEvents) h.ConfigEvents.Clear();

        Assert.Equal(200, (int)(await h.Delete(Tickets)).StatusCode);
        var stored = await store.ReadAsync("dashboard-plugins", "tickets");
        Assert.Equal(("{}", 2L), (stored.ValuesToJson().ToJsonString(), stored.Version));
        Assert.Empty(await Sections(h));
        Assert.Equal(404, (int)(await h.Get($"{Base}/installed/{Tickets}/config")).StatusCode);
        Assert.Equal(404, (int)(await h.Send(HttpMethod.Patch, Section, new { changes = new { mode = "full" } })).StatusCode);
        Assert.Equal([Data("""{"provider":"dashboard-plugins","section":"tickets","keys":["refreshSeconds","mode"],"version":"2","requiresRestart":false,"store":"memory","user":"admin"}""")],
            ConfigEventData(h));

        Assert.Equal(201, (int)(await Install(h, new { name = Tickets })).StatusCode);
        Assert.Equal(J("""{"values":{"refreshSeconds":30,"columns":["name","status"],"mode":"compact"},"version":"2"}"""), await Values(h));
    }

    [Fact]
    public async Task Install_ClearsValuesLeftInTheStoreFromAnEarlierInstallOfThePage()
    {
        var net = new FakeMarketplaceNet();
        PublishTickets(net);
        var store = new InMemoryConfigStore();
        await store.WriteAsync("dashboard-plugins", "tickets", new Dictionary<string, JsonNode?> { ["mode"] = "full", ["stale"] = 1 });
        var h = await StartAsync(net, configure: cli => cli.SetConfigStore(store, "dashboard-plugins"));
        Assert.Equal(201, (int)(await Install(h, JsonNode.Parse($$$"""{"name":"{{{Tickets}}}","config":{"refreshSeconds":10}}""")!)).StatusCode);
        Assert.Equal(J("""{"refreshSeconds":10}"""), (await store.ReadAsync("dashboard-plugins", "tickets")).ValuesToJson().ToJsonString());
    }

    // ---------------------------------------------------------------- restart and stores

    [Fact]
    public async Task ARestart_ReCreatesTheSectionsFromInstalledJson_Offline_WithTheStoredValues()
    {
        var net = new FakeMarketplaceNet();
        PublishTickets(net);
        var dir = _servers.TempDir();
        var file = Path.Combine(dir, "qcli-config.json");
        var h1 = await StartAsync(net, dir, cli => cli.SetConfigStore(new JsonFileConfigStore(file), "dashboard-plugins"));
        Assert.Equal(201, (int)(await Install(h1, JsonNode.Parse($$$"""{"name":"{{{Tickets}}}","config":{"refreshSeconds":120}}""")!)).StatusCode);
        var saved = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal(("""{"refreshSeconds":120}""", 1), (saved["dashboard-plugins"]!["tickets"]!["values"]!.ToJsonString(),
            saved["dashboard-plugins"]!["tickets"]!["version"]!.GetValue<int>()));

        var offline = new FakeMarketplaceNet { MarketplaceDown = true };
        var h2 = await StartAsync(offline, dir, cli => cli.SetConfigStore(new JsonFileConfigStore(file), "dashboard-plugins"));
        var provider = await ProviderOf(h2);
        Assert.Equal(("file", true), (provider["store"]!["kind"]!.GetValue<string>(), provider["store"]!["persistent"]!.GetValue<bool>()));
        Assert.Equal(["tickets Tickets 1"],
            provider["sections"]!.AsArray().Select(s => $"{s!["id"]} {s["title"]} {s["version"]}"));
        Assert.Equal(J("""{"values":{"refreshSeconds":120,"columns":["name","status"],"mode":"compact"},"version":"1"}"""), await Values(h2));
        // changeable after the restart, without the marketplace
        Assert.Equal(200, (int)(await h2.Send(HttpMethod.Patch, Section, new { changes = new { mode = "full" }, version = "1" })).StatusCode);
        Assert.Empty(offline.Calls);
    }

    [Fact]
    public async Task AnInstalledJsonWhoseSettingsBecameInvalid_LoadsThePluginWithoutThem()
    {
        var net = new FakeMarketplaceNet();
        PublishTickets(net);
        var first = await StartAsync(net);
        Assert.Equal(201, (int)(await Install(first, new { name = Tickets })).StatusCode);
        var path = Path.Combine(first.Dir, "installed.json");
        var saved = JsonNode.Parse(File.ReadAllText(path))!;
        saved["plugins"]![0]!["config"]!["entries"]!.AsArray().Add(new JsonObject { ["key"] = "apiKey", ["type"] = "secret" });
        File.WriteAllText(path, saved.ToJsonString());
        var second = await StartAsync(net, first.Dir);
        var installed = Assert.Single((await Json(await second.Get($"{Base}/installed"))).AsArray())!;
        Assert.Equal((Tickets, false), (installed["name"]!.GetValue<string>(), installed["hasConfig"]!.GetValue<bool>()));
        Assert.Empty(await Sections(second));
    }

    [Fact]
    public async Task TheProvidersStore_TheHostsStoreForDashboardPlugins_ElseTheDefaultStore_ElseMemory()
    {
        var net = new FakeMarketplaceNet();
        PublishTickets(net);
        var own = new InMemoryConfigStore();
        var fallback = new InMemoryConfigStore();
        var withOwn = await StartAsync(net, configure: cli =>
        {
            cli.SetConfigStore(fallback);
            cli.SetConfigStore(own, "dashboard-plugins");
        });
        Assert.Same(own, withOwn.Service<CliConfigRegistry>().StoreOf("dashboard-plugins"));
        Assert.Equal(201, (int)(await Install(withOwn, JsonNode.Parse($$$"""{"name":"{{{Tickets}}}","config":{"mode":"full"}}""")!)).StatusCode);
        Assert.Equal(J("""{"mode":"full"}"""), (await own.ReadAsync("dashboard-plugins", "tickets")).ValuesToJson().ToJsonString());
        Assert.Equal(0, (await fallback.ReadAsync("dashboard-plugins", "tickets")).Version);

        var defaultStore = new InMemoryConfigStore();
        var withDefault = await StartAsync(net, configure: cli => cli.SetConfigStore(defaultStore));
        Assert.Same(defaultStore, withDefault.Service<CliConfigRegistry>().StoreOf("dashboard-plugins"));
        Assert.Equal(201, (int)(await Install(withDefault, JsonNode.Parse($$$"""{"name":"{{{Tickets}}}","config":{"mode":"full"}}""")!)).StatusCode);
        Assert.Equal(J("""{"mode":"full"}"""), (await defaultStore.ReadAsync("dashboard-plugins", "tickets")).ValuesToJson().ToJsonString());

        var plain = await StartAsync(net);
        Assert.Equal(J("""{"kind":"memory","title":"In memory","detail":"","persistent":false}"""), (await ProviderOf(plain))["store"]!.ToJsonString());
        Assert.Same(plain.Service<DashboardMarketplace>().Config, plain.Service<CliConfigRegistry>().Find("dashboard-plugins"));
    }

    [Fact]
    public async Task TheMarketplaceTurnedOff_RegistersNoProvider()
    {
        var h = await _servers.StartAsync(enabled: false);
        Assert.False(h.Service<CliConfigRegistry>().Has("dashboard-plugins"));
        Assert.Equal(404, (int)(await h.Get($"{ConfigApi}/dashboard-plugins")).StatusCode);
    }

    // ---------------------------------------------------------------- through the config providers

    [Fact]
    public async Task ConfigSet_ValidatedByTheExistingCommand_Versions_Audit_ConfigChanged()
    {
        var net = new FakeMarketplaceNet();
        PublishTickets(net);
        var h = await StartAsync(net);
        Assert.Equal(201, (int)(await Install(h, new { name = Tickets })).StatusCode);
        lock (h.ConfigEvents) h.ConfigEvents.Clear();

        Assert.Equal("Error: validation failed\n  refreshSeconds: must be at least 5", await h.Run("config set dashboard-plugins.tickets.refreshSeconds 2"));
        Assert.StartsWith("Error: ", await h.Run("config set dashboard-plugins.tickets.refreshSeconds soon"));
        Assert.Equal("Error: validation failed\n  mode: must be one of: compact, full", await h.Run("config set dashboard-plugins.tickets.mode huge"));
        Assert.Equal("Error: validation failed\n  nope: unknown setting", await h.Run("config set dashboard-plugins.tickets.nope 1"));
        Assert.StartsWith("Error: unknown config section: dashboard-plugins.other", await h.Run("config set dashboard-plugins.other.mode full"));
        Assert.Empty(h.ConfigEvents);

        Assert.Equal("Updated dashboard-plugins.tickets.refreshSeconds = 60 (version 1)", await h.Run("config set dashboard-plugins.tickets.refreshSeconds 60"));
        Assert.Equal("Updated dashboard-plugins.tickets.columns = [\"id\",\"title\"] (version 2)", await h.Run("config set dashboard-plugins.tickets.columns id,title"));
        Assert.Equal("Error: version conflict (current version 2)", await h.Run("config set dashboard-plugins.tickets.mode full --expected-version 1"));
        Assert.Equal(string.Join('\n',
            "dashboard-plugins.tickets  Tickets  (version 2)",
            "  refreshSeconds  60  [number, runtime]",
            "  columns         [\"id\",\"title\"]  [string[], runtime]",
            "  mode            compact  [enum, default]"), await h.Run("config get dashboard-plugins.tickets"));
        Assert.Equal(
        [
            Data("""{"provider":"dashboard-plugins","section":"tickets","keys":["refreshSeconds"],"version":"1","requiresRestart":false,"store":"memory"}"""),
            Data("""{"provider":"dashboard-plugins","section":"tickets","keys":["columns"],"version":"2","requiresRestart":false,"store":"memory"}"""),
        ], ConfigEventData(h));
        Assert.Equal(
        [
            """config:dashboard-plugins.tickets [{"key":"refreshSeconds","old":30,"new":60}]""",
            """config:dashboard-plugins.tickets [{"key":"columns","old":["name","status"],"new":["id","title"]}]""",
        ], h.Audit.Entries.Where(a => a.Event == "config.update")
            .Select(a => $"{a.Target} {System.Text.Json.JsonSerializer.Serialize(a.Properties!["changes"])}"));
        // the page's values follow
        Assert.Equal(J("""{"values":{"refreshSeconds":60,"columns":["id","title"],"mode":"compact"},"version":"2"}"""), await Values(h));
        // reset to the default
        Assert.Contains("dashboard-plugins.tickets.refreshSeconds", await h.Run("config reset dashboard-plugins.tickets.refreshSeconds"));
        Assert.Equal(30, JsonNode.Parse(await Values(h))!["values"]!["refreshSeconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task Patch_400PerKey_409OnAStaleVersion()
    {
        var net = new FakeMarketplaceNet();
        PublishTickets(net);
        var h = await StartAsync(net);
        Assert.Equal(201, (int)(await Install(h, new { name = Tickets })).StatusCode);
        var bad = await h.Send(HttpMethod.Patch, Section, JsonNode.Parse("""{"changes":{"refreshSeconds":9999,"columns":[1]}}""")!);
        Assert.Equal(400, (int)bad.StatusCode);
        Assert.Equal(J("""{"error":"validation failed","errors":{"refreshSeconds":"must be at most 3600","columns":"must be a list of strings"}}"""),
            (await Json(bad)).ToJsonString());
        var ok = await h.Send(HttpMethod.Patch, Section, new { changes = new { mode = "full" }, version = "0" });
        Assert.Equal(200, (int)ok.StatusCode);
        var body = await Json(ok);
        Assert.Equal(("dashboard-plugins", """["mode"]""", "1"),
            (body["provider"]!.GetValue<string>(), body["applied"]!.ToJsonString(), body["version"]!.GetValue<string>()));
        var stale = await h.Send(HttpMethod.Patch, Section, new { changes = new { mode = "compact" }, version = "0" });
        Assert.Equal(409, (int)stale.StatusCode);
        Assert.Equal(J("""{"error":"version conflict","version":"1"}"""), (await Json(stale)).ToJsonString());
    }

    // ---------------------------------------------------------------- permissions on a protected server

    [Fact]
    public async Task OnAProtectedServer_ValuesNeedAdminAccess_ChangesConfigWrite_InstallTimeValuesDashboardInstall()
    {
        var (h, net, _, tokens) = await _servers.StartProtectedAsync(new()
        {
            ["viewer"] = ["admin:access"],
            ["reader"] = ["admin:access", "config:read"],
            ["writer"] = ["admin:access", "config:read", "config:write"],
            ["installer"] = ["admin:access", "dashboard:install"],
            ["outsider"] = ["files:read"],
        });
        PublishTickets(net);
        Task<HttpResponseMessage> InstallAs(string token) =>
            h.PostAsync($"{Base}/installed", token, $$$"""{"name":"{{{Tickets}}}","config":{"refreshSeconds":60}}""");
        async Task<JsonNode> ValuesAs(string token) => (await Json(await h.GetAsync($"{Base}/installed/{Tickets}/config", token)))["values"]!;

        // install-time values: dashboard:install, not config:write
        Assert.Equal(403, (int)(await InstallAs(tokens["writer"])).StatusCode);
        var installed = await InstallAs(tokens["installer"]);
        Assert.Equal(201, (int)installed.StatusCode);
        Assert.Equal("installer", (await Json(installed))["installedBy"]!.GetValue<string>());

        // the values: admin:access
        Assert.Equal(60, (await ValuesAs(tokens["viewer"]))["refreshSeconds"]!.GetValue<int>());
        Assert.Equal(403, (int)(await h.GetAsync($"{Base}/installed/{Tickets}/config", tokens["outsider"])).StatusCode);
        Assert.Equal(401, (int)(await h.GetAsync($"{Base}/installed/{Tickets}/config")).StatusCode);

        // the config API: config:read to see, config:write to change
        Assert.Equal(403, (int)(await h.GetAsync($"{ConfigApi}/dashboard-plugins", tokens["viewer"])).StatusCode);
        Assert.False((await Json(await h.GetAsync($"{ConfigApi}/dashboard-plugins", tokens["reader"])))["writable"]!.GetValue<bool>());
        const string change = """{"changes":{"mode":"full"}}""";
        Assert.Equal(403, (int)(await h.SendAsync(HttpMethod.Patch, Section, tokens["reader"], change)).StatusCode);
        Assert.Equal(403, (int)(await h.SendAsync(HttpMethod.Patch, Section, tokens["installer"], change)).StatusCode);
        Assert.Equal(200, (int)(await h.SendAsync(HttpMethod.Patch, Section, tokens["writer"], change)).StatusCode);
        var values = await ValuesAs(tokens["viewer"]);
        Assert.Equal((60, "full"), (values["refreshSeconds"]!.GetValue<int>(), values["mode"]!.GetValue<string>()));

        // updating with values: dashboard:install
        const string update = """{"config":{"mode":"compact"}}""";
        Assert.Equal(403, (int)(await h.SendAsync(HttpMethod.Put, $"{Base}/installed/{Tickets}", tokens["writer"], update)).StatusCode);
        Assert.Equal(200, (int)(await h.SendAsync(HttpMethod.Put, $"{Base}/installed/{Tickets}", tokens["installer"], update)).StatusCode);
    }

    [Fact]
    public async Task OnAProtectedServer_TheCommand_InstallAndUpdateConfigNeedDashboardInstall_InfoAdminAccess()
    {
        var (h, net, _, tokens) = await _servers.StartProtectedAsync(new()
        {
            ["viewer"] = ["admin:access", "commands:execute"],
            ["installer"] = ["admin:access", "commands:execute", "dashboard:install"],
        });
        PublishTickets(net);
        var registry = h.Service<ICliCommandRegistry>();
        var dashboard = registry.FindProcessor("admin")!.Processors!.Single(p => p.Command == "dashboard");
        Assert.Null(dashboard.Processors!.Single(p => p.Command == "info").RequiredPermissions);
        Task<(int Status, string Text, System.Text.Json.JsonElement Body)> Run(string line, string token) =>
            ConfigCommands.RunAsync(h.Client, registry, line, token);
        Assert.Equal(403, (await Run($"admin dashboard install {Tickets} --config '{{\"mode\":\"full\"}}'", tokens["viewer"])).Status);
        Assert.Contains($"Installed {Tickets}@1.0.0", (await Run($"admin dashboard install {Tickets} --config '{{\"mode\":\"full\"}}'", tokens["installer"])).Text);
        Assert.Contains("mode            full *", (await Run($"admin dashboard info {Tickets}", tokens["viewer"])).Text);
        Assert.Equal(403, (await Run($"admin dashboard update {Tickets} --config '{{\"mode\":\"compact\"}}'", tokens["viewer"])).Status);
    }

    // ---------------------------------------------------------------- the admin dashboard command

    [Fact]
    public async Task TheCommand_InstallWithConfig_Applied_PerKeyErrorsListed_ABadConfigRefused()
    {
        var net = new FakeMarketplaceNet();
        PublishTickets(net);
        net.Publish("@acme/cli-dashboard-secret", [Version("1.0.0", JsonNode.Parse("""{"entries":[{"key":"token","type":"secret"}]}"""))]);
        var h = await StartAsync(net);

        Assert.Equal(string.Join('\n', "Error: validation failed", "  refreshSeconds: must be at least 5", "  mode: must be one of: compact, full", "  nope: unknown setting"),
            await h.Run($$$"""admin dashboard install {{{Tickets}}} --config '{"refreshSeconds":1,"mode":"huge","nope":1}'"""));
        Assert.Equal("""Error: invalid --config: use a JSON object, e.g. --config '{"key":"value"}'""", await h.Run($"admin dashboard install {Tickets} --config 'not json'"));
        Assert.Equal("""Error: invalid --config: use a JSON object, e.g. --config '{"key":"value"}'""", await h.Run($"admin dashboard install {Tickets} --config '[1]'"));
        Assert.Empty(CdnCalls(net));

        Assert.Equal(string.Join('\n', $"Installed {Tickets}@1.0.0: the dashboard page \"Tickets\" (/x/tickets).", $"Its settings: admin dashboard info {Tickets}"),
            await h.Run($$$"""admin dashboard install {{{Tickets}}} --config '{"refreshSeconds":60,"columns":["id"]}'"""));
        Assert.Equal(J("""{"refreshSeconds":60,"columns":["id"],"mode":"compact"}"""), JsonNode.Parse(await Values(h))!["values"]!.ToJsonString());

        Assert.Equal(string.Join('\n',
            "Installed @acme/cli-dashboard-secret@1.0.0: the dashboard page \"cli-dashboard-secret\" (/x/secret).",
            "Warning: the settings of @acme/cli-dashboard-secret@1.0.0 are invalid and were ignored: token: secret settings are not allowed (a dashboard plugin runs in the browser)"),
            await h.Run("admin dashboard install @acme/cli-dashboard-secret"));
    }

    [Fact]
    public async Task TheCommand_Info_ThePageFilesAndSettingsWithTheirValuesDefaultsAndRules()
    {
        var net = new FakeMarketplaceNet();
        PublishTickets(net);
        net.Publish("@acme/cli-dashboard-plain", [new FakeVersion("1.0.0", Css: null)]);
        var h = await StartAsync(net);
        await h.Run($$$"""admin dashboard install {{{Tickets}}} --config '{"refreshSeconds":60}'""");
        await h.Run("admin dashboard install @acme/cli-dashboard-plain");

        var info = (await h.Run($"admin dashboard info {Tickets}")).Split('\n');
        Assert.Equal($"{Tickets}@1.0.0", info[0]);
        Assert.Equal("  Page        Tickets (/x/tickets)", info[1]);
        Assert.Matches(@"^ {2}Installed {3}\d{4}-\d\d-\d\d \d\d:\d\d:\d\d by anonymous$", info[2]);
        Assert.Matches(@"^ {2}Files {7}extension\.js \(\d+ bytes\), extension\.css \(\d+ bytes\)$", info[3]);
        Assert.Equal(
        [
            "Settings (config set dashboard-plugins.tickets.<key> <value>; version 1):",
            "Key             Value              Type      Default            Rule",
            "refreshSeconds  60 *               number    30                 min 5; max 3600",
            "columns         [\"name\",\"status\"]  string[]  [\"name\",\"status\"]  -",
            "mode            compact            enum      compact            one of compact, full",
            "* changed from the default",
        ], info.Skip(4));
        var plain = (await h.Run("admin dashboard info @acme/cli-dashboard-plain")).Split('\n');
        Assert.Matches(@"^ {2}Files {7}extension\.js \(\d+ bytes\)$", plain[3]);
        Assert.Equal("  Settings    none", plain[4]);

        var json = JsonNode.Parse(await h.Run($"admin dashboard info {Tickets} --json"))!;
        Assert.Equal((Tickets, "tickets", true, "1"), (json["plugin"]!["name"]!.GetValue<string>(), json["plugin"]!["id"]!.GetValue<string>(),
            json["plugin"]!["hasConfig"]!.GetValue<bool>(), json["configVersion"]!.GetValue<string>()));
        Assert.Equal(J("""
            {"key":"refreshSeconds","label":"Refresh every","description":"Seconds between two refreshes","type":"number","default":30,"validation":{"min":5,"max":3600},"value":60,"isDefault":false}
            """), json["settings"]![0]!.ToJsonString());

        Assert.Equal("Error: not installed: @acme/none", await h.Run("admin dashboard info @acme/none"));
        Assert.Equal("Error: invalid package name", await h.Run("admin dashboard info ../x"));
        Assert.Equal("Error: name one package: admin dashboard info <name>", await h.Run("admin dashboard info"));
    }

    [Fact]
    public async Task TheCommand_UpdateWithConfig_AndTheSettingsItDropsReported()
    {
        var net = new FakeMarketplaceNet();
        PublishTickets(net, Version("1.0.0", SettingsV1));
        var h = await StartAsync(net);
        await h.Run($$$"""admin dashboard install {{{Tickets}}} --config '{"refreshSeconds":120,"mode":"full"}'""");
        Assert.Equal($"{Tickets} is already at 1.0.0, the newest version. Its settings were changed.",
            await h.Run($$$"""admin dashboard update {{{Tickets}}} --config '{"mode":"compact"}'"""));
        Assert.Equal("Error: validation failed\n  mode: must be one of: compact, full", await h.Run($$$"""admin dashboard update {{{Tickets}}} --config '{"mode":"x"}'"""));

        PublishTickets(net, Version("1.0.0", SettingsV1), Version("2.0.0", SettingsV2));
        Assert.Equal(string.Join('\n',
            $"Updated {Tickets} from 1.0.0 to 2.0.0.",
            "Settings dropped (the new version no longer accepts them): refreshSeconds, mode",
            $"Its settings: admin dashboard info {Tickets}"), await h.Run($$$"""admin dashboard update {{{Tickets}}} --config '{"theme":"dark"}'"""));
        Assert.Equal(J("""{"refreshSeconds":15,"columns":["name"],"theme":"dark"}"""), JsonNode.Parse(await Values(h))!["values"]!.ToJsonString());
    }
}
