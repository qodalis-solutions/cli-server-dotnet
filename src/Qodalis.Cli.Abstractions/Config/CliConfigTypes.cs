using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Qodalis.Cli.Abstractions.Config;

// Config providers: the server's configuration, read and changed through providers that the server, its plugins and
// the host application register. The same contract and JSON in the Node, .NET and Python servers
// (cli-server-dashboard#61). Values are JSON (JsonNode): a secret is a string, a duration a string such as 30m.

/// <summary>The type of a setting; it decides the input the dashboard shows and how a value is validated.</summary>
public static class CliConfigEntryTypes
{
    public const string String = "string";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string StringList = "string[]";
    public const string Enum = "enum";
    public const string Secret = "secret";
    public const string Json = "json";
    public const string Duration = "duration";
    public const string Url = "url";

    /// <summary>Every entry type.</summary>
    public static readonly IReadOnlyList<string> All = [String, Number, Boolean, StringList, Enum, Secret, Json, Duration, Url];
}

/// <summary>
/// Where a setting's current value comes from: its <c>default</c>, a configuration <c>file</c>, an
/// <c>environment</c> variable, the <c>database</c>, or a change made at <c>runtime</c> (kept in memory).
/// </summary>
public static class CliConfigSources
{
    public const string Default = "default";
    public const string File = "file";
    public const string Environment = "environment";
    public const string Database = "database";
    public const string Runtime = "runtime";
}

/// <summary>The permissions, event names and naming rules of config providers.</summary>
public static class CliConfigPermissions
{
    /// <summary>See the providers, their sections and settings (secrets stay hidden).</summary>
    public const string Read = "config:read";

    /// <summary>Change settings.</summary>
    public const string Write = "config:write";
}

/// <summary>Constants of config providers.</summary>
public static class CliConfigConstants
{
    /// <summary>The event published after every change (audience <c>config:read</c>).</summary>
    public const string ChangedEvent = "config:changed";

    /// <summary>The audit event written for every change.</summary>
    public const string AuditEvent = "config.update";

    /// <summary>How a masked secret appears in audit entries.</summary>
    public const string SecretMask = "***";

    /// <summary>Default <see cref="ICliConfigProvider.Order"/>.</summary>
    public const int DefaultOrder = 100;

    /// <summary>Provider ids: <c>cors</c>, <c>my-app</c>.</summary>
    public static readonly Regex ProviderId = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);

    /// <summary>Section ids and setting keys: letters, digits, <c>_</c>, <c>-</c> and <c>.</c>.</summary>
    public static readonly Regex Key = new("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant);
}

/// <summary>Validation rules of a setting (checked by the server on every change).</summary>
public sealed class CliConfigValidation
{
    /// <summary><c>number</c>/<c>duration</c> (milliseconds): the smallest value; <c>string</c>/<c>secret</c>/<c>url</c>: the shortest length; <c>string[]</c>: the fewest items.</summary>
    [JsonPropertyName("min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Min { get; set; }

    /// <summary>Same as <see cref="Min"/>, the largest.</summary>
    [JsonPropertyName("max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Max { get; set; }

    /// <summary>A regular expression the value must match; each item of a <c>string[]</c>.</summary>
    [JsonPropertyName("pattern")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pattern { get; set; }

    /// <summary>The allowed values of an <c>enum</c>, or of each item of a <c>string[]</c> / <c>string</c>.</summary>
    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Options { get; set; }

    /// <summary>The value may not be empty (<c>null</c>, <c>''</c> or <c>[]</c>).</summary>
    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Required { get; set; }

    /// <summary>A copy.</summary>
    public CliConfigValidation Clone() => new()
    {
        Min = Min,
        Max = Max,
        Pattern = Pattern,
        Options = Options?.ToList(),
        Required = Required,
    };
}

/// <summary>
/// One setting, as a provider describes it. The server fills the defaults (<c>label</c> = <c>key</c>,
/// <c>description</c> = <c>''</c>, <c>source</c> = <c>default</c>) and never returns the value or default of a
/// sensitive entry (every <c>secret</c> is sensitive): it answers <c>value: null</c> and <c>isSet</c>.
/// </summary>
public sealed class CliConfigEntry
{
    public required string Key { get; init; }
    public string? Label { get; init; }
    public string? Description { get; init; }

    /// <summary>One of <see cref="CliConfigEntryTypes"/>.</summary>
    public required string Type { get; init; }

    /// <summary>The current value. Providers may leave a secret's value out and set <see cref="IsSet"/> instead.</summary>
    public JsonNode? Value { get; init; }

    /// <summary>The value the setting has when nothing sets it (null when none).</summary>
    public JsonNode? Default { get; init; }

    /// <summary>One of <see cref="CliConfigSources"/>; default <c>default</c>.</summary>
    public string? Source { get; init; }

    /// <summary>The setting can be changed.</summary>
    public bool Mutable { get; init; }

    /// <summary>A change takes effect only after the server restarts.</summary>
    public bool RequiresRestart { get; init; }

    /// <summary>The value is never returned (always true for a <c>secret</c>).</summary>
    public bool Sensitive { get; init; }

    /// <summary>For a sensitive entry whose value the provider leaves out: whether one is set.</summary>
    public bool? IsSet { get; init; }

    public CliConfigValidation? Validation { get; init; }

    /// <summary>A hint for a richer input: <c>tags</c>, <c>cron</c>, <c>code</c>, <c>origins</c>, <c>textarea</c>, ...</summary>
    public string? Editor { get; init; }
}

/// <summary>A group of settings of a provider, changed together (one version).</summary>
public sealed class CliConfigSection
{
    /// <summary>Unique within the provider: letters, digits, <c>_</c>, <c>-</c> and <c>.</c>.</summary>
    public required string Id { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public List<CliConfigEntry> Entries { get; init; } = [];

    /// <summary>The section's version (an opaque string): a change made against an older one gets 409. Writable sections only.</summary>
    public string? Version { get; init; }

    /// <summary>Keys not in <see cref="Entries"/> may be added (and removed with null): a free key/value section.</summary>
    public bool AllowCustomKeys { get; init; }
}

/// <summary>The signed-in caller of a config request.</summary>
public sealed record CliConfigCaller(string? UserId, string? UserName, IReadOnlyList<string>? Permissions);

/// <summary>Who reads or changes the configuration, and what they may do.</summary>
public sealed class CliConfigContext
{
    private readonly Func<string, bool> _hasPermission;

    public CliConfigContext(CliConfigCaller? caller, Func<string, bool> hasPermission, string? via = null)
    {
        Caller = caller;
        _hasPermission = hasPermission;
        Via = via;
    }

    /// <summary>The signed-in caller; null on a server without authentication.</summary>
    public CliConfigCaller? Caller { get; }

    /// <summary>Where the request comes from: <c>api</c>, <c>terminal</c> (the <c>config</c> command) or <c>mcp</c>.</summary>
    public string? Via { get; }

    /// <summary>True when the caller holds the permission (always true on a server without authentication).</summary>
    public bool HasPermission(string permission) => _hasPermission(permission);

    /// <summary>
    /// The context of a caller with these permissions; without a caller (a server without authentication) every
    /// permission is granted.
    /// </summary>
    public static CliConfigContext For(CliConfigCaller? caller, string? via = null) =>
        new(caller, permission => caller == null || HasCliPermission(caller.Permissions, permission), via);

    /// <summary>
    /// <c>resource:action</c> permission matching: a <c>*</c> as the last segment matches that level and everything
    /// below (<c>*</c>, <c>config:*</c>); a <c>*</c> elsewhere matches one segment. Case-insensitive.
    /// </summary>
    public static bool PermissionMatches(string granted, string required)
    {
        var g = granted.Trim().ToLowerInvariant().Split(':');
        var r = required.Trim().ToLowerInvariant().Split(':');
        for (var i = 0; i < g.Length; i++)
        {
            if (g[i] == "*" && i == g.Length - 1) return true;
            if (i >= r.Length || (g[i] != "*" && g[i] != r[i])) return false;
        }
        return g.Length == r.Length;
    }

    /// <summary>True when one of <paramref name="granted"/> matches <paramref name="required"/>.</summary>
    public static bool HasCliPermission(IEnumerable<string>? granted, string required) =>
        granted?.Any(p => p != null && PermissionMatches(p, required)) == true;
}

/// <summary>
/// A config provider: a named group of sections. Read-only unless it also implements <see cref="ICliConfigWriter"/>.
/// Registered with <c>cli.AddConfigProvider&lt;T&gt;()</c> or a module's <see cref="ICliModule.ConfigProviders"/>.
/// </summary>
public interface ICliConfigProvider
{
    /// <summary>Unique: lower-case letters, digits, <c>_</c> and <c>-</c> (<c>cors</c>, <c>my-app</c>).</summary>
    string Id { get; }

    string Title { get; }

    string? Description => null;

    /// <summary>Sort order of the providers (ascending, then by id); default 100.</summary>
    int Order => CliConfigConstants.DefaultOrder;

    /// <summary>A permission needed, besides <c>config:read</c> / <c>config:write</c>, to see or change this provider.</summary>
    string? RequiredPermission => null;

    /// <summary>
    /// Where its settings come from. Without it a writer is described as <c>custom</c> "Saved by the provider", a
    /// read-only provider as <c>custom</c> "Read by the provider".
    /// </summary>
    CliConfigProviderStorage? Storage => null;

    Task<IReadOnlyList<CliConfigSection>> GetSectionsAsync(CliConfigContext context, CancellationToken cancellationToken = default);
}

/// <summary>What a writer's <see cref="ICliConfigWriter.UpdateAsync"/> did.</summary>
public sealed class CliConfigUpdateResult
{
    /// <summary>The keys changed (or reset), in the order given.</summary>
    [JsonPropertyName("applied")]
    public List<string> Applied { get; init; } = [];

    /// <summary>True when one of them takes effect only after a restart.</summary>
    [JsonPropertyName("requiresRestart")]
    public bool RequiresRestart { get; init; }

    /// <summary>The section's new version.</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = "0";
}

/// <summary>
/// The write side of a provider. The server checks the permissions, that each key exists and is mutable, and its type
/// and validation rules before calling <see cref="ValidateAsync"/>, then <see cref="UpdateAsync"/>. A null value resets
/// the setting to its default (a secret: clears it).
/// </summary>
public interface ICliConfigWriter
{
    /// <summary>Further checks (e.g. between keys); per-key errors, empty when valid.</summary>
    Task<IReadOnlyDictionary<string, string>> ValidateAsync(string sectionId, IReadOnlyDictionary<string, JsonNode?> changes,
        CliConfigContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the changes. Throws <see cref="CliConfigConflictException"/> when <paramref name="expectedVersion"/> is
    /// given and is not the section's current version, <see cref="CliConfigValidationException"/> for late errors.
    /// </summary>
    Task<CliConfigUpdateResult> UpdateAsync(string sectionId, IReadOnlyDictionary<string, JsonNode?> changes,
        string? expectedVersion, CliConfigContext context, CancellationToken cancellationToken = default);
}

/// <summary>The section changed since the expected version (HTTP 409).</summary>
public sealed class CliConfigConflictException(string currentVersion) : Exception("version conflict")
{
    public string CurrentVersion { get; } = currentVersion;
}

/// <summary>Per-key validation errors found while updating (HTTP 400).</summary>
public sealed class CliConfigValidationException(IReadOnlyDictionary<string, string> errors) : Exception("validation failed")
{
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}

/// <summary>
/// A refused read or change, as the API answers it: <see cref="Status"/> is the HTTP status and the body is
/// <c>{ error, errors?, version? }</c> (<c>errors</c>: per-key messages on 400; <c>version</c>: the current version on 409).
/// </summary>
public sealed class CliConfigException : Exception
{
    public CliConfigException(int status, string message, IReadOnlyDictionary<string, string>? errors = null, string? version = null)
        : base(message)
    {
        Status = status;
        Errors = errors;
        Version = version;
    }

    public int Status { get; }
    public IReadOnlyDictionary<string, string>? Errors { get; }
    public string? Version { get; }

    /// <summary>The response body.</summary>
    public JsonObject ToJson()
    {
        var body = new JsonObject { ["error"] = Message };
        if (Errors != null)
        {
            var errors = new JsonObject();
            foreach (var (key, value) in Errors) errors[key] = value;
            body["errors"] = errors;
        }
        if (Version != null) body["version"] = Version;
        return body;
    }
}

/// <summary>What kind of store keeps the writable settings.</summary>
public static class CliConfigStoreKinds
{
    /// <summary>In memory: lost on restart.</summary>
    public const string Memory = "memory";
    public const string File = "file";
    public const string Database = "database";
    public const string Redis = "redis";
    public const string Custom = "custom";
}

/// <summary>
/// Where changes are saved: what a store says about itself (<see cref="ICliConfigStore.Describe"/>), as each stored
/// provider's <c>store</c> and the top-level <c>defaultStore</c> of <c>GET /api/v1/qcli/config</c>. <see cref="Detail"/>
/// names the file (relative to the app), the database and table, or the Redis host and key prefix, never credentials or
/// a connection string.
/// </summary>
public sealed class CliConfigStoreDescription
{
    /// <summary>One of <see cref="CliConfigStoreKinds"/>.</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = CliConfigStoreKinds.Custom;

    /// <summary><c>In memory</c>, <c>JSON file</c>, <c>Database</c>, ...</summary>
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;

    /// <summary><c>qcli-config.json</c>, <c>SQL Server database qcli, table QcliConfig</c>, <c>cache:6379/0, prefix qcli:config</c>; empty when nothing to add.</summary>
    [JsonPropertyName("detail")] public string Detail { get; init; } = string.Empty;

    /// <summary>Changes are kept across restarts.</summary>
    [JsonPropertyName("persistent")] public bool Persistent { get; init; }
}

/// <summary>Where a provider's settings come from (<see cref="CliConfigProviderStorage.Kind"/>).</summary>
public static class CliConfigStorageKinds
{
    /// <summary>Changes are saved to its config store (the provider view's <c>store</c>).</summary>
    public const string Store = "store";
    /// <summary>The running process.</summary>
    public const string Runtime = "runtime";
    /// <summary>The options the server or a plugin was created with.</summary>
    public const string Options = "options";
    /// <summary>Environment variables.</summary>
    public const string Environment = "environment";
    /// <summary>The host's configuration (<c>IConfiguration</c>).</summary>
    public const string Configuration = "configuration";
    public const string Custom = "custom";
}

/// <summary>Where a provider reads its settings from and saves changes to (<c>storage</c> of a provider view).</summary>
public sealed class CliConfigProviderStorage
{
    /// <summary>One of <see cref="CliConfigStorageKinds"/>.</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = CliConfigStorageKinds.Custom;

    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;

    /// <summary>The origins in the order they apply (later ones override earlier ones): <c>Server options</c>, <c>appsettings.json</c>, ...</summary>
    [JsonPropertyName("sources")] public List<string> Sources { get; init; } = [];
}

// ---------------------------------------------------------------- the JSON the server returns

/// <summary>An entry as the API returns it: every field present (<c>validation</c>, <c>editor</c>, <c>isSet</c> when relevant).</summary>
public sealed class CliConfigEntryView
{
    [JsonPropertyName("key")] public string Key { get; init; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; init; } = CliConfigEntryTypes.String;

    /// <summary>Null for a sensitive entry.</summary>
    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public JsonNode? Value { get; init; }

    /// <summary>Null for a sensitive entry.</summary>
    [JsonPropertyName("default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public JsonNode? Default { get; init; }

    [JsonPropertyName("source")] public string Source { get; init; } = CliConfigSources.Default;
    [JsonPropertyName("mutable")] public bool Mutable { get; init; }
    [JsonPropertyName("requiresRestart")] public bool RequiresRestart { get; init; }
    [JsonPropertyName("sensitive")] public bool Sensitive { get; init; }

    /// <summary>Sensitive entries only.</summary>
    [JsonPropertyName("isSet")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsSet { get; init; }

    [JsonPropertyName("validation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CliConfigValidation? Validation { get; init; }

    [JsonPropertyName("editor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Editor { get; init; }
}

/// <summary>A section as the API returns it.</summary>
public sealed class CliConfigSectionView
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;

    /// <summary>True when an entry is mutable or keys may be added.</summary>
    [JsonPropertyName("mutable")] public bool Mutable { get; init; }

    /// <summary>The version to send back with a change; null for a read-only section.</summary>
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Version { get; init; }

    [JsonPropertyName("allowCustomKeys")] public bool AllowCustomKeys { get; init; }
    [JsonPropertyName("entries")] public List<CliConfigEntryView> Entries { get; init; } = [];

    /// <summary>A copy with another version.</summary>
    public CliConfigSectionView WithVersion(string? version) => new()
    {
        Id = Id,
        Title = Title,
        Description = Description,
        Mutable = Mutable,
        Version = version,
        AllowCustomKeys = AllowCustomKeys,
        Entries = Entries,
    };
}

/// <summary>A provider as the API returns it (<c>GET /api/v1/qcli/config[/{provider}]</c>).</summary>
public sealed class CliConfigProviderView
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
    [JsonPropertyName("order")] public int Order { get; init; }

    /// <summary>True when the provider implements the writer and the caller may change settings.</summary>
    [JsonPropertyName("writable")] public bool Writable { get; init; }

    /// <summary>Where its settings come from (<c>sources</c> always present).</summary>
    [JsonPropertyName("storage")] public CliConfigProviderStorage Storage { get; init; } = new();

    /// <summary>
    /// The store its changes are saved to (providers whose storage is <c>store</c>: every
    /// <see cref="CliStoredConfigProvider"/>); each provider may have its own. Absent for other providers.
    /// </summary>
    [JsonPropertyName("store")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CliConfigStoreDescription? Store { get; init; }

    [JsonPropertyName("sections")] public List<CliConfigSectionView> Sections { get; init; } = [];
}

/// <summary>
/// The body of <c>GET /api/v1/qcli/config</c>: the store providers use unless they have their own
/// (<c>defaultStore</c>), and the providers, each stored one with its <c>store</c>.
/// </summary>
public sealed class CliConfigListView
{
    [JsonPropertyName("defaultStore")] public CliConfigStoreDescription DefaultStore { get; init; } = new();
    [JsonPropertyName("providers")] public List<CliConfigProviderView> Providers { get; init; } = [];
}

/// <summary>The body of <c>PATCH /api/v1/qcli/config/{provider}/{section}</c>.</summary>
public sealed class CliConfigUpdateRequest
{
    /// <summary>Key to new value; null resets to the default (clears a secret). Must not be empty.</summary>
    public IReadOnlyDictionary<string, JsonNode?>? Changes { get; init; }

    /// <summary>The section's version the change is based on; a stale one gets 409. Optional.</summary>
    public string? Version { get; init; }
}

/// <summary>The answer to a successful change.</summary>
public sealed class CliConfigUpdateResponse
{
    [JsonPropertyName("provider")] public string Provider { get; init; } = string.Empty;

    /// <summary>The section as it is now.</summary>
    [JsonPropertyName("section")] public CliConfigSectionView Section { get; init; } = new();

    [JsonPropertyName("applied")] public List<string> Applied { get; init; } = [];
    [JsonPropertyName("requiresRestart")] public bool RequiresRestart { get; init; }
    [JsonPropertyName("version")] public string Version { get; init; } = "0";
}

/// <summary>
/// The server's configuration service: the providers, filtered by permission, with secrets masked; changes validated,
/// audited and published. The admin plugin serves it over HTTP; the <c>config</c> command and the MCP tools use it too.
/// </summary>
public interface ICliConfigService
{
    /// <summary>The providers the caller may see, sorted. Throws <see cref="CliConfigException"/> 403 without <c>config:read</c>.</summary>
    Task<IReadOnlyList<CliConfigProviderView>> ListAsync(CliConfigContext context, CancellationToken cancellationToken = default);

    /// <summary>The store providers use unless they have their own (its own description).</summary>
    CliConfigStoreDescription DescribeDefaultStore();

    /// <summary>One provider. Throws <see cref="CliConfigException"/> 403 / 404.</summary>
    Task<CliConfigProviderView> GetAsync(string providerId, CliConfigContext context, CancellationToken cancellationToken = default);

    /// <summary>Changes a section. Throws <see cref="CliConfigException"/> 400 / 403 / 404 / 409.</summary>
    Task<CliConfigUpdateResponse> UpdateAsync(string providerId, string sectionId, CliConfigUpdateRequest request,
        CliConfigContext context, CancellationToken cancellationToken = default);
}
