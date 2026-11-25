using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Mcp;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Config;

/// <summary>
/// <c>server</c> (read-only): the server process: platform, .NET version, server version, environment, process id, host
/// name, operating system, start time and uptime.
/// </summary>
public sealed class CliServerConfigProvider : ICliConfigProvider
{
    private readonly DateTimeOffset _startedAt;
    private readonly string _version;
    private readonly Func<string?> _environment;

    public CliServerConfigProvider(string? version = null, Func<string?>? environment = null, DateTimeOffset? startedAt = null)
    {
        _version = version ?? ServerVersion;
        _environment = environment ?? (() => Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                                             ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"));
        _startedAt = startedAt ?? new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);
    }

    /// <summary>The version of the Qodalis.Cli package.</summary>
    public static string ServerVersion
    {
        get
        {
            var assembly = typeof(CliServerConfigProvider).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return informational?.Split('+')[0] ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
        }
    }

    public string Id => "server";
    public string Title => "Server";
    public string? Description => "The server process (read-only)";
    public int Order => 0;

    public Task<IReadOnlyList<CliConfigSection>> GetSectionsAsync(CliConfigContext context, CancellationToken cancellationToken = default)
    {
        var uptimeSeconds = Math.Max(0, Math.Floor((DateTimeOffset.UtcNow - _startedAt).TotalSeconds));
        var environment = _environment();
        IReadOnlyList<CliConfigSection> sections =
        [
            new()
            {
                Id = "runtime",
                Title = "Runtime",
                Entries =
                [
                    new() { Key = "platform", Label = "Platform", Description = "Server platform", Type = CliConfigEntryTypes.String, Value = "dotnet" },
                    new() { Key = "platformVersion", Label = "Platform version", Description = ".NET version", Type = CliConfigEntryTypes.String, Value = Environment.Version.ToString() },
                    new() { Key = "version", Label = "Server version", Description = "Version of Qodalis.Cli", Type = CliConfigEntryTypes.String, Value = _version },
                    new()
                    {
                        Key = "environment", Label = "Environment", Description = "ASPNETCORE_ENVIRONMENT", Type = CliConfigEntryTypes.String,
                        Value = environment ?? "Production",
                        Source = environment != null ? CliConfigSources.Environment : CliConfigSources.Default,
                    },
                    new() { Key = "pid", Label = "Process id", Description = "Process ID", Type = CliConfigEntryTypes.Number, Value = Environment.ProcessId },
                    new() { Key = "hostname", Label = "Host name", Description = "Name of the machine", Type = CliConfigEntryTypes.String, Value = Environment.MachineName },
                    new() { Key = "os", Label = "Operating system", Description = "OS platform and architecture", Type = CliConfigEntryTypes.String, Value = $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}" },
                    new() { Key = "startedAt", Label = "Started at", Description = "When the server started (UTC)", Type = CliConfigEntryTypes.String, Value = _startedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") },
                    new() { Key = "uptime", Label = "Uptime", Description = "Time since the server started", Type = CliConfigEntryTypes.Duration, Value = CliConfigValues.FormatDuration(uptimeSeconds * 1000) },
                ],
            },
        ];
        return Task.FromResult(sections);
    }
}

/// <summary>Which environment variables and configuration keys the <c>host</c> provider shows (<c>cli.ConfigureConfig</c>).</summary>
public sealed class CliHostEnvironmentOptions
{
    /// <summary>Names to show; <c>*</c> matches any run of characters (default every one).</summary>
    public List<string> Include { get; set; } = ["*"];

    /// <summary>Names to leave out, applied after <see cref="Include"/>.</summary>
    public List<string> Exclude { get; set; } = [];

    /// <summary>Further names whose values are secrets (masked), besides the built-in rule.</summary>
    public List<string> Secrets { get; set; } = [];
}

/// <summary>A server option the <c>host</c> provider lists.</summary>
public sealed record CliHostOption(string Key, string? Label, string? Description, JsonNode? Value);

/// <summary>
/// <c>host</c> (read-only): the host's configuration. Section <c>options</c>: the options the server was created with;
/// <c>environment</c>: the process environment variables; <c>configuration</c>: the host's <see cref="IConfiguration"/>
/// (appsettings, user secrets, command line, ...; the keys of environment variables are in <c>environment</c>). Secrets
/// are masked (<c>value: null</c>, <c>isSet: true</c>; see <see cref="IsSecret"/>).
/// </summary>
public sealed class CliHostConfigProvider : ICliConfigProvider
{
    /// <summary>
    /// Names treated as secrets: anything with SECRET, PASSWORD/PASSWD/PWD_, TOKEN, KEY (API_KEY, _KEY, KEY_, :Key),
    /// PRIVATE, CREDENTIAL, AUTH, DSN, CONNECTION STRING, COOKIE, SALT, SIGNATURE or CERT in it.
    /// </summary>
    private static readonly Regex SecretName = new(
        @"(SECRET|PASSWORD|PASSWD|PWD_|TOKEN|APIKEY|API_KEY|(^|_)KEY($|_)|PRIVATE|CREDENTIAL|AUTH|DSN|CONN(ECTION)?_?STR(ING)?|COOKIE|SALT|SIGNATURE|CERT)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary><c>scheme://user:password@host</c>: a URL carrying credentials.</summary>
    private static readonly Regex UrlWithCredentials = new(@"^[a-z][a-z0-9+.-]*://[^/\s@]*:[^/\s@]*@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A connection string with a password (<c>Password=...</c>, <c>Pwd=...</c>).</summary>
    private static readonly Regex ConnectionStringPassword = new(@"(^|;)\s*(password|pwd)\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly Func<IEnumerable<CliHostOption>> _options;
    private readonly IConfiguration? _configuration;
    private readonly Func<IDictionary<string, string?>> _environment;
    private readonly List<Regex> _include;
    private readonly List<Regex> _exclude;
    private readonly List<string> _secrets;

    public CliHostConfigProvider(Func<IEnumerable<CliHostOption>> options, IConfiguration? configuration = null,
        CliHostEnvironmentOptions? environment = null, Func<IDictionary<string, string?>>? variables = null)
    {
        _options = options;
        _configuration = configuration;
        var env = environment ?? new CliHostEnvironmentOptions();
        _include = env.Include.Select(GlobToRegex).ToList();
        _exclude = env.Exclude.Select(GlobToRegex).ToList();
        _secrets = env.Secrets.ToList();
        _environment = variables ?? (() => Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => e.Value as string));
    }

    public string Id => "host";
    public string Title => "Host";
    public string? Description => "Server options, environment variables and configuration (read-only, secrets masked)";
    public int Order => 20;

    private static Regex GlobToRegex(string glob) =>
        new("^" + Regex.Escape(glob).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when a variable or configuration key holds a secret: by its name, a configured name, or its value (a URL with credentials, a connection string with a password).</summary>
    public static bool IsSecret(string name, string? value, IEnumerable<string>? extra = null)
    {
        if (SecretName.IsMatch(name.Replace(':', '_').Replace("__", "_"))) return true;
        if (extra?.Any(pattern => GlobToRegex(pattern).IsMatch(name)) == true) return true;
        return value != null && (UrlWithCredentials.IsMatch(value) || ConnectionStringPassword.IsMatch(value));
    }

    private bool Shown(string name) => _include.Any(r => r.IsMatch(name)) && !_exclude.Any(r => r.IsMatch(name));

    private CliConfigEntry Variable(string name, string? value, string source) =>
        IsSecret(name, value, _secrets)
            ? new CliConfigEntry { Key = name, Type = CliConfigEntryTypes.Secret, IsSet = !string.IsNullOrEmpty(value), Sensitive = true, Source = source }
            : new CliConfigEntry { Key = name, Type = CliConfigEntryTypes.String, Value = value ?? string.Empty, Source = source };

    private static string TypeOf(JsonNode? value) => value switch
    {
        JsonArray => CliConfigEntryTypes.StringList,
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.Number => CliConfigEntryTypes.Number,
        JsonValue v when v.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => CliConfigEntryTypes.Boolean,
        _ => CliConfigEntryTypes.String,
    };

    public Task<IReadOnlyList<CliConfigSection>> GetSectionsAsync(CliConfigContext context, CancellationToken cancellationToken = default)
    {
        var environment = _environment()
            .Where(p => Shown(p.Key))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => Variable(p.Key, p.Value, CliConfigSources.Environment))
            .ToList();

        var sections = new List<CliConfigSection>
        {
            new()
            {
                Id = "options",
                Title = "Server options",
                Description = "The options the server was created with",
                Entries = _options().Select(o => new CliConfigEntry
                {
                    Key = o.Key,
                    Label = o.Label,
                    Description = o.Description,
                    Type = TypeOf(o.Value),
                    Value = o.Value?.DeepClone(),
                }).ToList(),
            },
            new() { Id = "environment", Title = "Environment variables", Description = "Secrets are masked", Entries = environment },
        };

        if (_configuration is IConfigurationRoot root)
        {
            var entries = new List<CliConfigEntry>();
            foreach (var (key, value) in root.AsEnumerable().OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (value == null || !Shown(key)) continue;
                // The provider that supplies the value: environment variables are listed in their own section
                var provider = root.Providers.Reverse().FirstOrDefault(p => p.TryGet(key, out _));
                if (provider is EnvironmentVariablesConfigurationProvider) continue;
                var source = provider is FileConfigurationProvider ? CliConfigSources.File : CliConfigSources.Default;
                entries.Add(Variable(key, value, source));
            }
            sections.Add(new CliConfigSection
            {
                Id = "configuration",
                Title = "Configuration",
                Description = "The host's IConfiguration (appsettings, user secrets, command line); secrets are masked",
                Entries = entries,
            });
        }
        return Task.FromResult<IReadOnlyList<CliConfigSection>>(sections);
    }
}

/// <summary>How CORS headers are decided.</summary>
public static class CliCorsMode
{
    /// <summary>CORS headers for the allowed origins (<c>SetAllowedOrigins</c>).</summary>
    public const string Origins = "origins";

    /// <summary>No CORS policy from the library (no allowed origins at startup: the host configures CORS, if any).</summary>
    public const string Disabled = "disabled";
}

/// <summary>The runtime providers of the core: <c>cors</c>, <c>mcp</c> and <c>filesystem</c>.</summary>
public static class CliRuntimeConfigProviders
{
    /// <summary>An origin: <c>http(s)://host[:port]</c> or <c>http(s)://host:*</c> (any port), no path.</summary>
    private static readonly Regex OriginPattern = new(@"^https?://[^\s/:?#]+(:(\d{1,5}|\*))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The first invalid origin of a list, as an error message; null when every one is valid.</summary>
    public static string? InvalidOrigin(JsonNode? value)
    {
        foreach (var origin in CliConfigValues.AsStringList(value) ?? [])
        {
            var match = OriginPattern.Match(origin.Trim().TrimEnd('/'));
            var port = match.Groups[2].Value;
            var valid = match.Success && (port.Length == 0 || port == "*" || int.Parse(port, System.Globalization.CultureInfo.InvariantCulture) <= 65535);
            if (!valid) return $"'{origin}' is not an origin (http(s)://host[:port], or :* for any port)";
        }
        return null;
    }

    /// <summary>
    /// <c>cors</c>: the allowed origins, changeable at runtime. <paramref name="origins"/> is the server's live list
    /// (<see cref="CliServerOptions.AllowedOrigins"/>: CORS, WebSocket upgrades, the users plugin's CSRF check): changes
    /// replace its items.
    /// </summary>
    public static CliStoredConfigProvider Cors(List<string> origins, string mode)
    {
        var initial = origins.ToList();
        var gate = new object();
        return new CliStoredConfigProvider(new CliStoredConfigProviderOptions
        {
            Id = "cors",
            Title = "CORS",
            Description = "Origins allowed to call the server from a browser and to open its WebSockets",
            Order = 30,
            Sections =
            [
                new CliConfigSectionDefinition
                {
                    Id = "cors",
                    Title = "CORS",
                    Settings =
                    [
                        new CliConfigSettingDefinition
                        {
                            Key = "mode",
                            Label = "Mode",
                            Description = "origins: CORS headers for the allowed origins (set at startup); disabled: the library registers no CORS policy (WebSocket and CSRF checks still use the allowed origins)",
                            Type = CliConfigEntryTypes.Enum,
                            Mutable = false,
                            Default = mode,
                            Validation = new CliConfigValidation { Options = [CliCorsMode.Origins, CliCorsMode.Disabled] },
                        },
                        new CliConfigSettingDefinition
                        {
                            Key = "allowedOrigins",
                            Label = "Allowed origins",
                            Description = "Exact origins (http://host:port; http://host:* for any port). Used for CORS, WebSocket upgrades, the MCP endpoint and CSRF checks",
                            Type = CliConfigEntryTypes.StringList,
                            Default = CliConfigValues.StringList(initial),
                            Editor = "origins",
                            Validate = InvalidOrigin,
                            Apply = value =>
                            {
                                var next = (CliConfigValues.AsStringList(value) ?? initial).Select(o => o.Trim()).ToList();
                                lock (gate)
                                {
                                    origins.Clear();
                                    origins.AddRange(next);
                                }
                            },
                        },
                    ],
                },
            ],
        });
    }

    /// <summary>
    /// <c>mcp</c>: the MCP endpoint's tool lists, instructions and read-only mode, changeable at runtime
    /// (<paramref name="options"/> is the endpoint's live options). Read-only when MCP is off.
    /// </summary>
    public static CliStoredConfigProvider Mcp(McpOptions? options, string path)
    {
        var enabled = options is { Enabled: true };
        var live = options ?? new McpOptions();
        var tools = live.Tools?.ToList() ?? [];
        var exclude = live.Exclude?.ToList() ?? [];
        var instructions = live.Instructions ?? string.Empty;
        var readOnly = live.ReadOnly;
        List<string>? List(JsonNode? value) => CliConfigValues.AsStringList(value) is { Count: > 0 } items ? items : null;
        return new CliStoredConfigProvider(new CliStoredConfigProviderOptions
        {
            Id = "mcp",
            Title = "MCP",
            Description = "The MCP endpoint: which tools AI clients see",
            Order = 40,
            Sections =
            [
                new CliConfigSectionDefinition
                {
                    Id = "mcp",
                    Title = "MCP endpoint",
                    Settings =
                    [
                        new CliConfigSettingDefinition { Key = "enabled", Label = "Enabled", Description = "The MCP endpoint is served (cli.AddMcp())", Type = CliConfigEntryTypes.Boolean, Mutable = false, RequiresRestart = true, Default = enabled },
                        new CliConfigSettingDefinition { Key = "path", Label = "Path", Description = "Where the endpoint is served", Type = CliConfigEntryTypes.String, Mutable = false, Default = path },
                        new CliConfigSettingDefinition
                        {
                            Key = "tools", Label = "Allowed tools", Description = "Allow-list of tool names (a trailing * matches a prefix); empty: every tool",
                            Type = CliConfigEntryTypes.StringList, Default = CliConfigValues.StringList(tools), Editor = "tags", Mutable = enabled,
                            Validation = new CliConfigValidation { Pattern = "^[a-z0-9_*-]{1,64}$" },
                            Apply = value => live.Tools = List(value),
                        },
                        new CliConfigSettingDefinition
                        {
                            Key = "exclude", Label = "Excluded tools", Description = "Deny-list of tool names, applied after the allow-list",
                            Type = CliConfigEntryTypes.StringList, Default = CliConfigValues.StringList(exclude), Editor = "tags", Mutable = enabled,
                            Validation = new CliConfigValidation { Pattern = "^[a-z0-9_*-]{1,64}$" },
                            Apply = value => live.Exclude = List(value),
                        },
                        new CliConfigSettingDefinition
                        {
                            Key = "instructions", Label = "Instructions", Description = "Returned to clients by initialize",
                            Type = CliConfigEntryTypes.String, Default = instructions, Editor = "textarea", Mutable = enabled,
                            Validation = new CliConfigValidation { Max = 4000 },
                            Apply = value => live.Instructions = CliConfigValues.AsString(value) is { Length: > 0 } text ? text : null,
                        },
                        new CliConfigSettingDefinition
                        {
                            Key = "readOnly", Label = "Read-only", Description = "Only tools annotated read-only (readOnlyHint) are listed and callable",
                            Type = CliConfigEntryTypes.Boolean, Default = readOnly, Mutable = enabled,
                            Apply = value => live.ReadOnly = CliConfigValues.AsBoolean(value) == true,
                        },
                    ],
                },
            ],
        });
    }

    /// <summary>
    /// <c>filesystem</c> (read-only): the filesystem API's storage and allowed roots. Read-only on purpose: widening what
    /// the server may read is a deployment decision, not a form field.
    /// </summary>
    public static CliStoredConfigProvider FileSystem(string storage, IReadOnlyList<string>? roots) =>
        new(new CliStoredConfigProviderOptions
        {
            Id = "filesystem",
            Title = "Filesystem",
            Description = "The filesystem API (/api/qcli/fs)",
            Order = 50,
            Sections =
            [
                new CliConfigSectionDefinition
                {
                    Id = "filesystem",
                    Title = "Filesystem",
                    Settings =
                    [
                        new CliConfigSettingDefinition { Key = "storage", Label = "Storage", Description = "The storage provider", Type = CliConfigEntryTypes.String, Mutable = false, Default = storage },
                        new CliConfigSettingDefinition
                        {
                            Key = "allowedPaths", Label = "Allowed roots", Description = "Directories the API may access (OS storage); set in code",
                            Type = CliConfigEntryTypes.StringList, Mutable = false, RequiresRestart = true,
                            Default = CliConfigValues.StringList(roots ?? []),
                        },
                    ],
                },
            ],
        });
}
