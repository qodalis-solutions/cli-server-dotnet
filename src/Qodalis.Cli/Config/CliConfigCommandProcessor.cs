using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.Helpers;

namespace Qodalis.Cli.Config;

/// <summary>
/// <c>config</c>: the server's configuration from the terminal. <c>list</c>, <c>get &lt;provider&gt;[.&lt;section&gt;[.&lt;key&gt;]]</c>,
/// <c>set &lt;provider&gt;.&lt;section&gt;.&lt;key&gt; &lt;value&gt; [--expected-version v]</c>, <c>reset &lt;provider&gt;.&lt;section&gt;.&lt;key&gt;</c>.
/// <c>config:read</c> for the command, <c>config:write</c> for set and reset; secrets are accepted, never printed.
/// </summary>
public sealed class CliConfigCommandProcessor : CliCommandProcessor
{
    private readonly ConfigListProcessor _list;

    /// <param name="service">The server's configuration.</param>
    /// <param name="context">The caller of the command (protected servers), as a config context.</param>
    public CliConfigCommandProcessor(ICliConfigService service, Func<CliConfigContext> context)
    {
        _list = new ConfigListProcessor(service, context);
        Processors =
        [
            _list,
            new ConfigGetProcessor(service, context),
            new ConfigSetProcessor(service, context),
            new ConfigResetProcessor(service, context),
        ];
    }

    public override string Command { get; set; } = "config";
    public override string Description { get; set; } = "The server's configuration: list providers, show, change and reset settings";
    public override string? Module { get; set; } = "config";
    public override string? Icon { get; set; } = "⚙️";
    public override IEnumerable<string>? RequiredPermissions { get; set; } = [CliConfigPermissions.Read];

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
        _list.HandleAsync(command, cancellationToken);

    /// <summary>A value as printed: strings as they are, anything else as JSON; secrets as set / not set.</summary>
    public static string FormatValue(JsonNode? value, bool sensitive = false, bool? isSet = null)
    {
        if (sensitive) return isSet == true ? "(set)" : "(not set)";
        if (value == null) return "(none)";
        return CliConfigValues.AsString(value) ?? value.ToJsonString();
    }

    internal static string FormatValue(CliConfigEntryView entry) => FormatValue(entry.Value, entry.Sensitive, entry.IsSet);

    internal static string ErrorText(Exception ex)
    {
        if (ex is CliConfigException config)
        {
            var lines = new List<string> { $"Error: {config.Message}{(config.Version != null ? $" (current version {config.Version})" : "")}" };
            foreach (var (key, message) in config.Errors ?? new Dictionary<string, string>()) lines.Add($"  {key}: {message}");
            return string.Join('\n', lines);
        }
        return $"Error: {ex.Message}";
    }

    internal static string? ExpectedVersionOf(CliProcessCommand command)
    {
        // --expected-version: not --version, which the web CLI answers itself; raw API callers may still send it
        foreach (var name in new[] { "expected-version", "version" })
        {
            if (command.Args == null || !command.Args.TryGetValue(name, out var value) || value == null) continue;
            var text = value switch
            {
                JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null } => null,
                JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
                JsonElement e => e.GetRawText(),
                bool => null,
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };
            if (!string.IsNullOrEmpty(text)) return text;
        }
        return null;
    }

    internal sealed record ConfigPath(CliConfigProviderView Provider, CliConfigSectionView? Section, string? Key);

    /// <summary>
    /// Resolves <c>provider[.section[.key]]</c>: the provider id has no dots; the section is the longest section id the
    /// rest starts with (ids and keys may contain dots).
    /// </summary>
    internal static async Task<ConfigPath> ResolvePathAsync(ICliConfigService service, CliConfigContext context, string path,
        CancellationToken cancellationToken)
    {
        var parts = path.Split('.');
        var provider = await service.GetAsync(parts[0], context, cancellationToken);
        var rest = string.Join('.', parts.Skip(1));
        if (rest.Length == 0) return new ConfigPath(provider, null, null);
        var section = provider.Sections
            .Where(s => rest == s.Id || rest.StartsWith(s.Id + ".", StringComparison.Ordinal))
            .OrderByDescending(s => s.Id.Length)
            .FirstOrDefault() ?? throw new InvalidOperationException($"unknown config section: {parts[0]}.{rest}");
        var key = rest.Length > section.Id.Length ? rest[(section.Id.Length + 1)..] : null;
        return new ConfigPath(provider, section, string.IsNullOrEmpty(key) ? null : key);
    }

    internal static List<string> DescribeSection(string provider, CliConfigSectionView section)
    {
        var lines = new List<string>
        {
            $"{provider}.{section.Id}{(section.Title != section.Id ? $"  {section.Title}" : "")}{(section.Version != null ? $"  (version {section.Version})" : "  (read-only)")}",
        };
        var width = Math.Min(32, section.Entries.Select(e => e.Key.Length).DefaultIfEmpty(0).Max());
        foreach (var e in section.Entries)
        {
            var flags = new List<string> { e.Type, e.Source };
            if (!e.Mutable) flags.Add("read-only");
            if (e.RequiresRestart) flags.Add("restart");
            lines.Add($"  {e.Key.PadRight(width)}  {FormatValue(e)}  [{string.Join(", ", flags)}]");
        }
        if (section.Entries.Count == 0) lines.Add("  (no settings)");
        return lines;
    }

    internal static string DescribeEntry(string provider, CliConfigSectionView section, CliConfigEntryView e)
    {
        var lines = new List<string>
        {
            $"{provider}.{section.Id}.{e.Key}",
            $"  Label         {e.Label}",
        };
        if (!string.IsNullOrEmpty(e.Description)) lines.Add($"  Description   {e.Description}");
        lines.Add($"  Type          {e.Type}");
        lines.Add($"  Value         {FormatValue(e)}");
        if (!e.Sensitive) lines.Add($"  Default       {FormatValue(e.Default)}");
        lines.Add($"  Source        {e.Source}");
        lines.Add($"  Mutable       {(e.Mutable ? "yes" : "no")}{(e.RequiresRestart ? " (restart required)" : "")}");
        if (e.Validation is { } v)
        {
            var rules = new List<string>();
            if (v.Required == true) rules.Add("required");
            if (v.Min is { } min) rules.Add($"min {min.ToString(CultureInfo.InvariantCulture)}");
            if (v.Max is { } max) rules.Add($"max {max.ToString(CultureInfo.InvariantCulture)}");
            if (v.Pattern != null) rules.Add($"pattern {v.Pattern}");
            if (v.Options is { Count: > 0 } options) rules.Add($"one of {string.Join(", ", options)}");
            if (rules.Count > 0) lines.Add($"  Validation    {string.Join("; ", rules)}");
        }
        if (section.Version != null) lines.Add($"  Version       {section.Version}");
        return string.Join('\n', lines);
    }

    internal static CliCommandParameterDescriptor ExpectedVersionParameter() => new()
    {
        Name = "expected-version",
        Description = "The section version the change is based on (refused when stale)",
        Required = false,
        Type = CommandParameterType.String,
    };
}

internal sealed class ConfigListProcessor(ICliConfigService service, Func<CliConfigContext> context) : CliCommandProcessor
{
    public override string Command { get; set; } = "list";
    public override string Description { get; set; } = "Lists the config providers and their sections";
    public override IEnumerable<string>? RequiredPermissions { get; set; } = [CliConfigPermissions.Read];

    public override async Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var providers = await service.ListAsync(context(), cancellationToken);
            if (providers.Count == 0) return "No config providers.";
            var lines = new List<string>();
            foreach (var p in providers)
            {
                lines.Add($"{p.Id}  {p.Title}{(p.Writable ? "" : "  (read-only)")}");
                foreach (var s in p.Sections)
                    lines.Add($"  {s.Id.PadRight(16)} {s.Entries.Count.ToString(CultureInfo.InvariantCulture).PadLeft(3)} setting(s){(s.Version != null ? $"  version {s.Version}" : "")}");
            }
            return string.Join('\n', lines);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CliConfigCommandProcessor.ErrorText(ex);
        }
    }
}

internal sealed class ConfigGetProcessor(ICliConfigService service, Func<CliConfigContext> context) : CliCommandProcessor
{
    public override string Command { get; set; } = "get";
    public override string Description { get; set; } = "Shows a provider, a section or one setting: config get <provider>[.<section>[.<key>]]";
    public override bool? ValueRequired { get; set; } = true;
    public override IEnumerable<string>? RequiredPermissions { get; set; } = [CliConfigPermissions.Read];
    public override IEnumerable<CliCommandExample>? Examples { get; set; } =
    [
        new("config get cors", "Every section of the cors provider"),
        new("config get cors.cors.allowedOrigins", "One setting with its default, source and rules"),
    ];

    public override async Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = CliValueHelpers.GetPositionals(command).FirstOrDefault();
            if (string.IsNullOrEmpty(path)) return "Error: usage: config get <provider>[.<section>[.<key>]]";
            var (provider, section, key) = await CliConfigCommandProcessor.ResolvePathAsync(service, context(), path, cancellationToken);
            if (section == null)
            {
                var lines = new List<string>
                {
                    $"{provider.Id}  {provider.Title}{(string.IsNullOrEmpty(provider.Description) ? "" : $"  {provider.Description}")}",
                };
                foreach (var s in provider.Sections) lines.AddRange(CliConfigCommandProcessor.DescribeSection(provider.Id, s));
                return string.Join('\n', lines);
            }
            if (key == null) return string.Join('\n', CliConfigCommandProcessor.DescribeSection(provider.Id, section));
            var entry = section.Entries.FirstOrDefault(e => e.Key == key);
            if (entry == null) return $"Error: unknown setting: {provider.Id}.{section.Id}.{key}";
            return CliConfigCommandProcessor.DescribeEntry(provider.Id, section, entry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CliConfigCommandProcessor.ErrorText(ex);
        }
    }
}

internal sealed class ConfigSetProcessor(ICliConfigService service, Func<CliConfigContext> context) : CliCommandProcessor
{
    public override string Command { get; set; } = "set";
    public override string Description { get; set; } = "Changes a setting: config set <provider>.<section>.<key> <value> [--expected-version v]";
    public override bool? ValueRequired { get; set; } = true;
    public override IEnumerable<string>? RequiredPermissions { get; set; } = [CliConfigPermissions.Write];
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } = [CliConfigCommandProcessor.ExpectedVersionParameter()];
    public override IEnumerable<CliCommandExample>? Examples { get; set; } =
    [
        new("config set cors.cors.allowedOrigins https://app.example.com,http://localhost:*", "A list: comma-separated or a JSON array"),
        new("config set mcp.mcp.readOnly true --expected-version 3", "Refused when the section changed since version 3"),
        new("config set custom.custom.motd \"Hello there\"", "A free key of the custom provider"),
    ];

    public override async Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var positionals = CliValueHelpers.GetPositionals(command);
            if (positionals.Count < 2 || string.IsNullOrEmpty(positionals[0]))
                return "Error: usage: config set <provider>.<section>.<key> <value> [--expected-version v]";
            var ctx = context();
            var (provider, section, key) = await CliConfigCommandProcessor.ResolvePathAsync(service, ctx, positionals[0], cancellationToken);
            if (section == null || key == null) return "Error: name a setting: <provider>.<section>.<key>";
            var text = string.Join(' ', positionals.Skip(1));
            var entry = section.Entries.FirstOrDefault(e => e.Key == key);
            JsonNode? value;
            try
            {
                if (entry != null)
                {
                    value = CliConfigValues.ParseInput(entry.Type, text);
                }
                else
                {
                    // A new key of a free section: JSON when it parses (numbers, booleans, lists), else the text
                    try
                    {
                        value = JsonNode.Parse(text);
                    }
                    catch (JsonException)
                    {
                        value = JsonValue.Create(text);
                    }
                }
            }
            catch (FormatException ex)
            {
                return $"Error: validation failed\n  {key}: {ex.Message}";
            }
            var result = await service.UpdateAsync(provider.Id, section.Id, new CliConfigUpdateRequest
            {
                Changes = new Dictionary<string, JsonNode?> { [key] = value },
                Version = CliConfigCommandProcessor.ExpectedVersionOf(command),
            }, ctx, cancellationToken);
            var updated = result.Section.Entries.FirstOrDefault(e => e.Key == key);
            var shown = updated != null ? CliConfigCommandProcessor.FormatValue(updated) : "";
            var lines = new List<string> { $"Updated {provider.Id}.{section.Id}.{key}{(shown != "" ? $" = {shown}" : "")} (version {result.Version})" };
            if (result.RequiresRestart) lines.Add("Restart the server for the change to take effect.");
            return string.Join('\n', lines);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CliConfigCommandProcessor.ErrorText(ex);
        }
    }
}

internal sealed class ConfigResetProcessor(ICliConfigService service, Func<CliConfigContext> context) : CliCommandProcessor
{
    public override string Command { get; set; } = "reset";
    public override string Description { get; set; } = "Resets a setting to its default (clears a secret, removes a free key): config reset <provider>.<section>.<key>";
    public override bool? ValueRequired { get; set; } = true;
    public override IEnumerable<string>? RequiredPermissions { get; set; } = [CliConfigPermissions.Write];
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } = [CliConfigCommandProcessor.ExpectedVersionParameter()];
    public override IEnumerable<CliCommandExample>? Examples { get; set; } =
    [
        new("config reset cors.cors.allowedOrigins", "Back to the origins the server was started with"),
    ];

    public override async Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = CliValueHelpers.GetPositionals(command).FirstOrDefault();
            if (string.IsNullOrEmpty(path)) return "Error: usage: config reset <provider>.<section>.<key>";
            var ctx = context();
            var (provider, section, key) = await CliConfigCommandProcessor.ResolvePathAsync(service, ctx, path, cancellationToken);
            if (section == null || key == null) return "Error: name a setting: <provider>.<section>.<key>";
            var result = await service.UpdateAsync(provider.Id, section.Id, new CliConfigUpdateRequest
            {
                Changes = new Dictionary<string, JsonNode?> { [key] = null },
                Version = CliConfigCommandProcessor.ExpectedVersionOf(command),
            }, ctx, cancellationToken);
            var updated = result.Section.Entries.FirstOrDefault(e => e.Key == key);
            var lines = new List<string>
            {
                $"Reset {provider.Id}.{section.Id}.{key}{(updated != null ? $" = {CliConfigCommandProcessor.FormatValue(updated)}" : "")} (version {result.Version})",
            };
            if (result.RequiresRestart) lines.Add("Restart the server for the change to take effect.");
            return string.Join('\n', lines);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CliConfigCommandProcessor.ErrorText(ex);
        }
    }
}
