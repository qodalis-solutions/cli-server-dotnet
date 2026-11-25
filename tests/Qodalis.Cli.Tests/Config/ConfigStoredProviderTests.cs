using System.Text.Json;
using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions.Config;

namespace Qodalis.Cli.Tests.Config;

/// <summary>CliStoredConfigProvider (settings described in code, changes in a store) and the custom provider.</summary>
public class ConfigStoredProviderTests
{
    internal static readonly CliConfigContext Open = CliConfigContext.For(null);

    internal static Dictionary<string, JsonNode?> C(string json) => ConfigStoreContractTests.C(json);

    private static CliStoredConfigProvider Billing(Action<JsonNode?>? dueDays = null, Action<JsonNode?>? apiKey = null) =>
        new(new CliStoredConfigProviderOptions
        {
            Id = "billing",
            Title = "Billing",
            Description = "Invoices",
            Order = 5,
            RequiredPermission = "billing:admin",
            Sections =
            [
                new()
                {
                    Id = "invoices",
                    Title = "Invoices",
                    Settings =
                    [
                        new() { Key = "dueDays", Label = "Due in", Type = "number", Default = 30, Validation = new() { Min = 1, Max = 90 }, Apply = dueDays },
                        new() { Key = "apiKey", Type = "secret", Apply = apiKey },
                        new() { Key = "endpoint", Type = "url", Default = "https://pay.example", Sensitive = true },
                        new() { Key = "currency", Type = "enum", Default = "EUR", Validation = new() { Options = ["EUR", "USD"] }, Source = "environment" },
                        new() { Key = "region", Type = "string", Mutable = false, Value = () => "eu-1", RequiresRestart = true },
                        new() { Key = "even", Type = "number", Default = 2, Validate = v => CliConfigValues.AsNumber(v) % 2 == 0 ? null : "must be even" },
                    ],
                },
                new() { Id = "info", Settings = [new() { Key = "build", Type = "string", Mutable = false, Default = "1.2.3" }] },
            ],
        });

    private static async Task<(CliConfigSection Invoices, CliConfigSection Info)> SectionsAsync(CliStoredConfigProvider p)
    {
        var sections = await p.GetSectionsAsync(Open);
        return (sections[0], sections[1]);
    }

    private static CliConfigEntry Entry(CliConfigSection section, string key) => section.Entries.Single(e => e.Key == key);

    [Fact]
    public void RefusesInvalidOrDuplicateSectionsAndKeys()
    {
        static void Make(params CliConfigSectionDefinition[] sections) =>
            _ = new CliStoredConfigProvider(new CliStoredConfigProviderOptions { Id = "x", Title = "X", Sections = sections.ToList() });
        Assert.Equal("invalid or duplicate config section: a", Assert.Throws<ArgumentException>(() => Make(new CliConfigSectionDefinition { Id = "a" }, new CliConfigSectionDefinition { Id = "a" })).Message);
        Assert.StartsWith("invalid or duplicate config section", Assert.Throws<ArgumentException>(() => Make(new CliConfigSectionDefinition { Id = "bad id" })).Message);
        Assert.Equal("invalid or duplicate config key: a.k", Assert.Throws<ArgumentException>(() =>
            Make(new CliConfigSectionDefinition { Id = "a", Settings = [new() { Key = "k", Type = "string" }, new() { Key = "k", Type = "string" }] })).Message);
        Assert.StartsWith("invalid or duplicate config key", Assert.Throws<ArgumentException>(() =>
            Make(new CliConfigSectionDefinition { Id = "a", Settings = [new() { Key = "", Type = "string" }] })).Message);
    }

    [Fact]
    public void ExposesIdTitleDescriptionOrderAndRequiredPermission()
    {
        ICliConfigProvider p = Billing();
        Assert.Equal(("billing", "Billing", "Invoices", 5, "billing:admin"), (p.Id, p.Title, p.Description, p.Order, p.RequiredPermission));
    }

    [Fact]
    public async Task Entries_CarryDefaultsSourcesFlagsAndRules_ReadOnlyValuesAreLive()
    {
        var (invoices, info) = await SectionsAsync(Billing());
        Assert.Equal(("invoices", "Invoices", "0"), (invoices.Id, invoices.Title, invoices.Version));
        var due = Entry(invoices, "dueDays");
        Assert.Equal(("Due in", "number", 30.0, 30.0, "default", true, false, false),
            (due.Label, due.Type, CliConfigValues.AsNumber(due.Value), CliConfigValues.AsNumber(due.Default), due.Source, due.Mutable, due.RequiresRestart, due.Sensitive));
        Assert.Equal((1.0, 90.0), (due.Validation!.Min, due.Validation.Max));
        Assert.Equal("environment", Entry(invoices, "currency").Source);
        var region = Entry(invoices, "region");
        Assert.Equal(("eu-1", false, true), (CliConfigValues.AsString(region.Value), region.Mutable, region.RequiresRestart));
        // A section without a mutable setting has no version
        Assert.Null(info.Version);
        Assert.Equal(("build", "1.2.3", false), (info.Entries[0].Key, CliConfigValues.AsString(info.Entries[0].Value), info.Entries[0].Mutable));
    }

    [Fact]
    public async Task NeverReturnsASecretOrSensitiveValue_OnlyIsSet()
    {
        var p = Billing();
        await p.UpdateAsync("invoices", C("{\"apiKey\":\"sk_live_123\"}"), null, Open);
        var (invoices, _) = await SectionsAsync(p);
        foreach (var key in new[] { "apiKey", "endpoint" })
        {
            var e = Entry(invoices, key);
            Assert.Equal((null, null, true, true), (e.Value, e.Default, e.IsSet, e.Sensitive));
        }
        var json = JsonSerializer.Serialize(invoices);
        Assert.DoesNotContain("sk_live_123", json);
        Assert.DoesNotContain("pay.example", json);
        // The host's code reads it
        Assert.Equal("sk_live_123", CliConfigValues.AsString(p.GetValue("invoices", "apiKey")));
    }

    [Fact]
    public async Task GetValue_StoredElseDefault_ReadOnlyLive_UnknownNull()
    {
        var p = Billing();
        Assert.Equal(30, CliConfigValues.AsNumber(p.GetValue("invoices", "dueDays")));
        await p.UpdateAsync("invoices", C("{\"dueDays\":45}"), null, Open);
        Assert.Equal(45, CliConfigValues.AsNumber(p.GetValue("invoices", "dueDays")));
        Assert.Equal("eu-1", CliConfigValues.AsString(p.GetValue("invoices", "region")));
        Assert.Null(p.GetValue("invoices", "nope"));
        Assert.Null(p.GetValue("nope", "dueDays"));
        Assert.Equal("1.2.3", CliConfigValues.AsString(p.GetValue("info", "build")));
    }

    [Fact]
    public async Task Validate_UnknownKeysReadOnlyKeysAndCustomRules()
    {
        var p = Billing();
        var errors = await p.ValidateAsync("invoices", C("{\"nope\":1,\"region\":\"x\",\"even\":3,\"dueDays\":10}"), Open);
        Assert.Equal(new Dictionary<string, string> { ["nope"] = "unknown setting", ["region"] = "read-only", ["even"] = "must be even" }, errors);
        Assert.Empty(await p.ValidateAsync("invoices", C("{\"even\":null}"), Open));
        Assert.Empty(await p.ValidateAsync("missing", C("{\"a\":1}"), Open));
    }

    [Fact]
    public async Task Update_StoresBumpsTheVersionAppliesAndReportsRestart()
    {
        var applied = new List<JsonNode?>();
        var p = Billing(dueDays: applied.Add);
        var result = await p.UpdateAsync("invoices", C("{\"dueDays\":60}"), "0", Open);
        Assert.Equal(["dueDays"], result.Applied);
        Assert.Equal((false, "1"), (result.RequiresRestart, result.Version));
        Assert.Equal(60, CliConfigValues.AsNumber(Assert.Single(applied)));
        var (invoices, _) = await SectionsAsync(p);
        Assert.Equal("1", invoices.Version);
        Assert.Equal((60.0, "runtime"), (CliConfigValues.AsNumber(Entry(invoices, "dueDays").Value), Entry(invoices, "dueDays").Source));
    }

    [Fact]
    public async Task Null_ResetsToTheDefaultAndAppliesIt()
    {
        var applied = new List<JsonNode?>();
        var p = Billing(dueDays: applied.Add);
        await p.UpdateAsync("invoices", C("{\"dueDays\":60}"), null, Open);
        await p.UpdateAsync("invoices", C("{\"dueDays\":null}"), null, Open);
        Assert.Equal(30, CliConfigValues.AsNumber(applied[^1]));
        var (invoices, _) = await SectionsAsync(p);
        Assert.Equal((30.0, "default"), (CliConfigValues.AsNumber(Entry(invoices, "dueDays").Value), Entry(invoices, "dueDays").Source));
    }

    [Fact]
    public async Task ASecret_IsReplacedAndCleared_NeverReadBack()
    {
        var applied = new List<JsonNode?>();
        var p = Billing(apiKey: applied.Add);
        await p.UpdateAsync("invoices", C("{\"apiKey\":\"one\"}"), null, Open);
        await p.UpdateAsync("invoices", C("{\"apiKey\":\"two\"}"), null, Open);
        Assert.Equal("two", CliConfigValues.AsString(p.GetValue("invoices", "apiKey")));
        await p.UpdateAsync("invoices", C("{\"apiKey\":null}"), null, Open);
        Assert.Null(applied[^1]);
        var (invoices, _) = await SectionsAsync(p);
        Assert.Equal((null, false), (Entry(invoices, "apiKey").Value, Entry(invoices, "apiKey").IsSet));
    }

    [Fact]
    public async Task AStaleOrMalformedVersion_IsAConflictWithTheCurrentVersion()
    {
        var p = Billing();
        await p.UpdateAsync("invoices", C("{\"dueDays\":10}"), "0", Open);
        var stale = await Assert.ThrowsAsync<CliConfigConflictException>(() => p.UpdateAsync("invoices", C("{\"dueDays\":11}"), "0", Open));
        Assert.Equal("1", stale.CurrentVersion);
        var bad = await Assert.ThrowsAsync<CliConfigConflictException>(() => p.UpdateAsync("invoices", C("{\"dueDays\":11}"), "abc", Open));
        Assert.Equal("1", bad.CurrentVersion);
        Assert.Equal(10, CliConfigValues.AsNumber(p.GetValue("invoices", "dueDays")));
    }

    [Fact]
    public async Task RequiresRestart_WhenAChangedSettingNeedsOne()
    {
        var p = new CliStoredConfigProvider(new CliStoredConfigProviderOptions
        {
            Id = "x",
            Title = "X",
            Sections = [new() { Id = "s", Settings = [new() { Key = "port", Type = "number", Default = 1, RequiresRestart = true }, new() { Key = "b", Type = "boolean", Default = false }] }],
        });
        Assert.False((await p.UpdateAsync("s", C("{\"b\":true}"), null, Open)).RequiresRestart);
        Assert.True((await p.UpdateAsync("s", C("{\"port\":2,\"b\":false}"), null, Open)).RequiresRestart);
    }

    [Fact]
    public async Task AnUnknownSection_Throws() =>
        Assert.Equal("unknown config section: billing.nope",
            (await Assert.ThrowsAsync<ArgumentException>(() => Billing().UpdateAsync("nope", C("{\"a\":1}"), null, Open))).Message);

    [Fact]
    public async Task AttachStore_AppliesStoredValues_MutableSettingsOnly_AndUsesTheStoreSource()
    {
        var store = new InMemoryConfigStore();
        await store.WriteAsync("billing", "invoices", C("{\"dueDays\":14,\"region\":\"stored-but-read-only\",\"apiKey\":\"k\"}"));
        var dueDays = new List<JsonNode?>();
        var apiKey = new List<JsonNode?>();
        var p = Billing(dueDays.Add, apiKey.Add);
        await p.AttachStoreAsync(store);
        await p.ReadyAsync();
        Assert.Equal(14, CliConfigValues.AsNumber(Assert.Single(dueDays)));
        Assert.Equal("k", CliConfigValues.AsString(Assert.Single(apiKey)));
        var (invoices, _) = await SectionsAsync(p);
        Assert.Equal("1", invoices.Version);
        Assert.Equal((14.0, "runtime"), (CliConfigValues.AsNumber(Entry(invoices, "dueDays").Value), Entry(invoices, "dueDays").Source));
        Assert.Equal("eu-1", CliConfigValues.AsString(Entry(invoices, "region").Value));
    }

    [Fact]
    public async Task TwoInstancesOnOneStore_ChangesReachTheStore()
    {
        var store = new InMemoryConfigStore();
        var a = Billing();
        await a.AttachStoreAsync(store);
        await a.UpdateAsync("invoices", C("{\"dueDays\":7}"), null, Open);
        var stored = await store.ReadAsync("billing", "invoices");
        Assert.Equal(("{\"dueDays\":7}", 1L), (stored.ValuesToJson().ToJsonString(), stored.Version));
        // A second instance (a restart) sees it
        var b = Billing();
        await b.AttachStoreAsync(store);
        Assert.Equal(7, CliConfigValues.AsNumber(b.GetValue("invoices", "dueDays")));
    }

    private sealed class FailingStore : ICliConfigStore
    {
        public string Source => "database";
        public Task<CliConfigStoredSection> ReadAsync(string provider, string section, CancellationToken cancellationToken = default) =>
            Task.FromException<CliConfigStoredSection>(new InvalidOperationException("db down"));
        public Task<CliConfigStoredSection> WriteAsync(string provider, string section, IReadOnlyDictionary<string, JsonNode?> changes,
            long? expectedVersion = null, CancellationToken cancellationToken = default) =>
            Task.FromException<CliConfigStoredSection>(new InvalidOperationException("db down"));
    }

    [Fact]
    public async Task AFailingStore_FailsReady()
    {
        var p = Billing();
        Assert.Equal("db down", (await Assert.ThrowsAsync<InvalidOperationException>(() => p.AttachStoreAsync(new FailingStore()))).Message);
        Assert.Equal("db down", (await Assert.ThrowsAsync<InvalidOperationException>(() => p.ReadyAsync())).Message);
    }

    [Fact]
    public async Task WithoutAStore_TheProviderKeepsChangesInMemory()
    {
        var p = Billing();
        await p.ReadyAsync();
        await p.UpdateAsync("invoices", C("{\"dueDays\":3}"), null, Open);
        Assert.Equal(3, CliConfigValues.AsNumber(p.GetValue("invoices", "dueDays")));
    }

    [Fact]
    public async Task Custom_OneFreeSection_KeysAddedWithTheirTypeInferred_RemovedWithNull()
    {
        var p = CliCustomConfigProvider.Create();
        Assert.Equal(("custom", "Custom", 1000), (p.Id, p.Title, p.Order));
        var custom = (await p.GetSectionsAsync(Open))[0];
        Assert.Equal(("custom", true, "0", 0), (custom.Id, custom.AllowCustomKeys, custom.Version, custom.Entries.Count));
        await p.UpdateAsync("custom", C("{\"motd\":\"hello\",\"retries\":3,\"beta\":true,\"tags\":[\"a\"],\"limits\":{\"a\":1}}"), "0", Open);
        custom = (await p.GetSectionsAsync(Open))[0];
        Assert.Equal(
            ["motd string \"hello\" True runtime", "retries number 3 True runtime", "beta boolean true True runtime",
             "tags string[] [\"a\"] True runtime", "limits json {\"a\":1} True runtime"],
            custom.Entries.Select(e => $"{e.Key} {e.Type} {e.Value!.ToJsonString()} {e.Mutable} {e.Source}"));
        Assert.Equal("hello", CliConfigValues.AsString(p.GetValue("custom", "motd")));
        await p.UpdateAsync("custom", C("{\"motd\":null}"), "1", Open);
        custom = (await p.GetSectionsAsync(Open))[0];
        Assert.Equal(["retries", "beta", "tags", "limits"], custom.Entries.Select(e => e.Key));
        Assert.Null(p.GetValue("custom", "motd"));
    }

    [Fact]
    public async Task Custom_RefusesKeysThatAreNotIdentifiers() =>
        Assert.Equal(new Dictionary<string, string> { ["bad key"] = "invalid key: use letters, digits, _, - and ." },
            await CliCustomConfigProvider.Create().ValidateAsync("custom", C("{\"bad key\":1,\"ok.key-1_x\":2}"), Open));

    [Fact]
    public void Custom_Options()
    {
        var p = CliCustomConfigProvider.Create("app", "App", "mine", 7);
        Assert.Equal(("app", "App", "mine", 7), (p.Id, p.Title, p.Description, p.Order));
    }
}
