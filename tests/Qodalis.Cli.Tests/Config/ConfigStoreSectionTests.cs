using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Qodalis.Cli.Abstractions.Config;

namespace Qodalis.Cli.Tests.Config;

/// <summary>The store contract on a section of a settings file next to other settings.</summary>
public sealed class JsonFileSectionConfigStoreContractTests : ConfigStoreContractTests, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("qcli-config-section-").FullName;

    public JsonFileSectionConfigStoreContractTests()
    {
        var file = Path.Combine(_dir, "app.json");
        File.WriteAllText(file, "{\n  \"name\": \"demo\",\n  \"app\": { \"port\": 3000 }\n}\n");
        Store = new JsonFileConfigStore(file, new JsonFileConfigStoreOptions { Section = "app.qcli" });
    }

    protected override ICliConfigStore Store { get; }

    public void Dispose() => Directory.Delete(_dir, true);
}

/// <summary>The store contract on appsettings.json, section Qcli:Config.</summary>
public sealed class AppSettingsConfigStoreContractTests : ConfigStoreContractTests, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("qcli-appsettings-").FullName;

    public AppSettingsConfigStoreContractTests()
    {
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"), "{\n  \"Logging\": { \"LogLevel\": { \"Default\": \"Information\" } }\n}\n");
        Store = new AppSettingsConfigStore(new AppSettingsConfigStoreOptions { ContentRoot = _dir });
    }

    protected override ICliConfigStore Store { get; }

    public void Dispose() => Directory.Delete(_dir, true);
}

public sealed class JsonFileSectionConfigStoreTests : IDisposable
{
    private const string Original = "{\n    \"name\": \"demo\",\n    \"port\": 3000,\n    \"nested\": {\n        \"list\": [1, 2]\n    }\n}\n";
    private readonly string _dir = Directory.CreateTempSubdirectory("qcli-config-section-").FullName;
    private readonly string _file;

    public JsonFileSectionConfigStoreTests()
    {
        _file = Path.Combine(_dir, "app.json");
        File.WriteAllText(_file, Original);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private static JsonFileConfigStoreOptions Section(string section) => new() { Section = section };

    private JsonNode Root() => JsonNode.Parse(File.ReadAllText(_file))!;

    [Fact]
    public void DescribesItself_ByTheFileAndTheSection()
    {
        var d = new JsonFileConfigStore(_file, Section("qcli")).Describe()!;
        Assert.Equal(("file", "JSON file", "app.json, section qcli", true), (d.Kind, d.Title, d.Detail, d.Persistent));
        Assert.Equal("config/app.json, section qcli",
            new JsonFileConfigStore(Path.Combine(Directory.GetCurrentDirectory(), "config", "app.json"), Section("qcli")).Describe()!.Detail);
        Assert.Equal("app.json, section app.qcli", new JsonFileConfigStore(_file, Section("app:qcli")).Describe()!.Detail);
        Assert.Equal("JSON file (app.json, section qcli), kept across restarts",
            CliConfigStoreHelpers.DescribeLine(new JsonFileConfigStore(_file, Section("qcli")).Describe()!));
    }

    [Fact]
    public async Task KeepsTheSettingsUnderTheSection_AndTheRestOfTheFileAsItWas()
    {
        var store = new JsonFileConfigStore(_file, Section("qcli"));
        await store.WriteAsync("custom", "custom", ConfigStoreContractTests.C("{\"motd\":\"hi\"}"));
        var text = await File.ReadAllTextAsync(_file);
        Assert.Equal(
            "{\n    \"name\": \"demo\",\n    \"port\": 3000,\n    \"nested\": {\n        \"list\": [\n            1,\n            2\n        ]\n    },\n" +
            "    \"qcli\": {\n        \"custom\": {\n            \"custom\": {\n                \"version\": 1,\n                \"values\": {\n                    \"motd\": \"hi\"\n                }\n            }\n        }\n    }\n}\n",
            text);
        Assert.Equal(["name", "port", "nested", "qcli"], Root().AsObject().Select(p => p.Key));
    }

    [Fact]
    public async Task KeepsTabs_CrLf_NoTrailingNewline_TheBom_AndCharactersAsTheyAre()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        await File.WriteAllBytesAsync(_file, bom.Concat(System.Text.Encoding.UTF8.GetBytes(
            "{\r\n\t\"conn\": \"Server=a;Password=p+&<x>\",\r\n\t\"name\": \"Café\"\r\n}")).ToArray());
        await new JsonFileConfigStore(_file, Section("qcli")).WriteAsync("p", "s", ConfigStoreContractTests.C("{\"a\":1}"));
        var bytes = await File.ReadAllBytesAsync(_file);
        Assert.Equal(bom, bytes.Take(3));
        var text = System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.StartsWith("{\r\n\t\"conn\": \"Server=a;Password=p+&<x>\",\r\n\t\"name\": \"Café\",\r\n\t\"qcli\": {\r\n\t\t\"p\": {", text);
        Assert.EndsWith("}", text);
        Assert.DoesNotContain("\n\n", text.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task ANestedSection_IsCreatedInsideTheExistingObject()
    {
        await File.WriteAllTextAsync(_file, "{ \"app\": { \"port\": 3000 } }");
        await new JsonFileConfigStore(_file, Section("app:qcli")).WriteAsync("p", "s", ConfigStoreContractTests.C("{\"a\":1}"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"app\":{\"port\":3000,\"qcli\":{\"p\":{\"s\":{\"version\":1,\"values\":{\"a\":1}}}}}}"), Root()));
        var read = await new JsonFileConfigStore(_file, Section("app.qcli")).ReadAsync("p", "s");
        Assert.Equal(("{\"a\":1}", 1L), (ConfigStoreContractTests.Values(read), read.Version));
    }

    [Fact]
    public async Task ReReadsTheRestOfTheFileOnEveryWrite_EditsMadeMeanwhileAreKept()
    {
        var store = new JsonFileConfigStore(_file, Section("qcli"));
        await store.WriteAsync("p", "s", ConfigStoreContractTests.C("{\"a\":1}"));
        var edited = Root().AsObject();
        edited["port"] = 4000;
        edited["added"] = "by hand";
        await File.WriteAllTextAsync(_file, edited.ToJsonString());
        await store.WriteAsync("p", "s", ConfigStoreContractTests.C("{\"b\":2}"));
        var root = Root();
        Assert.Equal((4000, "by hand"), (root["port"]!.GetValue<int>(), root["added"]!.GetValue<string>()));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"p\":{\"s\":{\"version\":2,\"values\":{\"a\":1,\"b\":2}}}}"), root["qcli"]));
    }

    [Fact]
    public async Task VersionsPersistAcrossInstances_AndAStaleVersionIsRefused()
    {
        await new JsonFileConfigStore(_file, Section("qcli")).WriteAsync("p", "s", ConfigStoreContractTests.C("{\"a\":1}"));
        var restarted = new JsonFileConfigStore(_file, Section("qcli"));
        Assert.Equal(1, (await restarted.ReadAsync("p", "s")).Version);
        var ex = await Assert.ThrowsAsync<CliConfigConflictException>(() => restarted.WriteAsync("p", "s", ConfigStoreContractTests.C("{\"a\":2}"), 0));
        Assert.Equal("1", ex.CurrentVersion);
        Assert.Equal(2, (await restarted.WriteAsync("p", "s", ConfigStoreContractTests.C("{\"a\":2}"), 1)).Version);
    }

    [Fact]
    public async Task WritesAtomically_NoTemporaryFileLeft_TheFileKeepsItsMode()
    {
        if (OperatingSystem.IsWindows()) return;
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        File.SetUnixFileMode(_file, mode);
        await new JsonFileConfigStore(_file, Section("qcli")).WriteAsync("p", "s", ConfigStoreContractTests.C("{\"a\":1}"));
        Assert.Equal(mode, File.GetUnixFileMode(_file));
        Assert.Equal(["app.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ANewSettingsFile_IsCreatedWithTheSection_ReadableByTheOwnerOnly()
    {
        var fresh = Path.Combine(_dir, "sub", "new.json");
        await new JsonFileConfigStore(fresh, Section("qcli")).WriteAsync("p", "s", ConfigStoreContractTests.C("{\"a\":1}"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"qcli\":{\"p\":{\"s\":{\"version\":1,\"values\":{\"a\":1}}}}}"), JsonNode.Parse(File.ReadAllText(fresh))));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fresh));
    }

    [Fact]
    public async Task ASectionThatIsNotAnObject_OrAFileThatIsNotJson_IsRefused_AndTheFileLeftUntouched()
    {
        await File.WriteAllTextAsync(_file, "{ \"qcli\": 5, \"app\": [1] }");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new JsonFileConfigStore(_file, Section("qcli")).ReadAsync("p", "s"));
        Assert.Contains("qcli is not an object", ex.Message);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new JsonFileConfigStore(_file, Section("app.qcli")).ReadAsync("p", "s"));
        Assert.Contains("app is not an object", ex.Message);

        var store = new JsonFileConfigStore(_file, Section("fresh"));
        await store.ReadAsync("p", "s");
        await File.WriteAllTextAsync(_file, "{oops");
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync("p", "s", ConfigStoreContractTests.C("{\"a\":1}")));
        Assert.Contains("is not valid JSON", ex.Message);
        Assert.Equal("{oops", await File.ReadAllTextAsync(_file));
        Assert.Equal(0, (await store.ReadAsync("p", "s")).Version);
    }

    [Fact]
    public async Task CommentsAndTrailingCommas_AreRead()
    {
        await File.WriteAllTextAsync(_file, "{\n  // the app\n  \"name\": \"demo\",\n}\n");
        await new JsonFileConfigStore(_file, Section("qcli")).WriteAsync("p", "s", ConfigStoreContractTests.C("{\"a\":1}"));
        Assert.Equal("demo", Root()["name"]!.GetValue<string>());
    }

    [Fact]
    public void AnEmptyPartOfTheSectionPath_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => new JsonFileConfigStore(_file, Section("app..qcli")));
        Assert.Equal("invalid config file section: app..qcli", ex.Message);
        Assert.Empty(new JsonFileConfigStore(_file, Section("  ")).SectionPath);
    }
}

public sealed class AppSettingsConfigStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("qcli-appsettings-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void DescribesItself_AsTheSettingsFileAndTheSection()
    {
        var d = new AppSettingsConfigStore().Describe()!;
        Assert.Equal(("file", "appsettings.json", "section Qcli:Config", true), (d.Kind, d.Title, d.Detail, d.Persistent));
        Assert.Equal("appsettings.json (section Qcli:Config)", CliConfigStoreHelpers.DescribeName(d));
        Assert.Equal("appsettings.Development.json (section App:Settings)", CliConfigStoreHelpers.DescribeName(
            new AppSettingsConfigStore(new AppSettingsConfigStoreOptions { Environment = "Development", Section = "App:Settings" }).Describe()!));
        Assert.Equal("file", new AppSettingsConfigStore().Source);
        Assert.Equal(["Qcli", "Config"], new AppSettingsConfigStore().SectionPath);
    }

    [Fact]
    public async Task WritesTheProviderSectionUnderQcliConfig_WithItsVersion_TheRestKept()
    {
        var file = Path.Combine(_dir, "appsettings.Production.json");
        await File.WriteAllTextAsync(file, "{\n  \"ConnectionStrings\": { \"Db\": \"Data Source=app.db\" },\n  \"Qcli\": { \"Mcp\": true }\n}\n");
        var store = new AppSettingsConfigStore(new AppSettingsConfigStoreOptions { ContentRoot = _dir, Environment = "Production" });
        await store.WriteAsync("billing", "invoices", ConfigStoreContractTests.C("{\"dueDays\":45}"));
        var root = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(
            "{\"ConnectionStrings\":{\"Db\":\"Data Source=app.db\"},\"Qcli\":{\"Mcp\":true,\"Config\":{\"billing\":{\"invoices\":{\"version\":1,\"values\":{\"dueDays\":45}}}}}}"), root));
        Assert.False(File.Exists(Path.Combine(_dir, "appsettings.json")));
    }

    [Fact]
    public async Task TheHostsConfiguration_SeesTheChange_WithReloadOnChange()
    {
        var file = Path.Combine(_dir, "appsettings.json");
        await File.WriteAllTextAsync(file, "{\n  \"Logging\": { \"LogLevel\": { \"Default\": \"Information\" } }\n}\n");
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().SetBasePath(_dir).AddJsonFile("appsettings.json", optional: false, reloadOnChange: true).Build();
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = ChangeToken.OnChange(configuration.GetReloadToken, () => reloaded.TrySetResult());

        var store = new AppSettingsConfigStore(new AppSettingsConfigStoreOptions { ContentRoot = _dir });
        await store.WriteAsync("billing", "invoices", ConfigStoreContractTests.C("{\"dueDays\":45}"));
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (configuration["Qcli:Config:billing:invoices:values:dueDays"] == null && DateTime.UtcNow < deadline)
            await Task.WhenAny(reloaded.Task, Task.Delay(200));
        Assert.Equal("45", configuration["Qcli:Config:billing:invoices:values:dueDays"]);
        Assert.Equal("1", configuration["Qcli:Config:billing:invoices:version"]);
        Assert.Equal("Information", configuration["Logging:LogLevel:Default"]);
    }

    [Fact]
    public async Task AFilePathAndASection_CanBeGiven()
    {
        var file = Path.Combine(_dir, "settings.json");
        var store = new AppSettingsConfigStore(file, "App:Qcli");
        await store.WriteAsync("p", "s", ConfigStoreContractTests.C("{\"a\":1}"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"App\":{\"Qcli\":{\"p\":{\"s\":{\"version\":1,\"values\":{\"a\":1}}}}}}"),
            JsonNode.Parse(await File.ReadAllTextAsync(file))));
        Assert.Equal("App:Qcli", store.Section);
    }
}
