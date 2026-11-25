using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Abstractions.Restart;

namespace Qodalis.Cli.Config;

/// <summary>Converts what providers return into the JSON the API returns.</summary>
public static class CliConfigViews
{
    /// <summary>An entry as the API returns it: defaults filled, the value of a sensitive entry never included.</summary>
    public static CliConfigEntryView Entry(CliConfigEntry entry, bool writable)
    {
        var sensitive = entry.Sensitive || entry.Type == CliConfigEntryTypes.Secret;
        return new CliConfigEntryView
        {
            Key = entry.Key,
            Label = entry.Label ?? entry.Key,
            Description = entry.Description ?? string.Empty,
            Type = entry.Type,
            Value = sensitive ? null : entry.Value?.DeepClone(),
            Default = sensitive ? null : entry.Default?.DeepClone(),
            Source = entry.Source ?? CliConfigSources.Default,
            Mutable = writable && entry.Mutable,
            RequiresRestart = entry.RequiresRestart,
            Sensitive = sensitive,
            IsSet = sensitive ? entry.IsSet ?? !CliConfigValues.IsEmpty(entry.Value) : null,
            Validation = entry.Validation?.Clone(),
            Editor = string.IsNullOrEmpty(entry.Editor) ? null : entry.Editor,
        };
    }

    /// <summary>A provider's storage as the API returns it: its own, else <c>custom</c> (saved / read by the provider).</summary>
    public static CliConfigProviderStorage Storage(ICliConfigProvider provider)
    {
        var own = provider.Storage;
        if (own != null && !string.IsNullOrEmpty(own.Kind))
            return new CliConfigProviderStorage
            {
                Kind = own.Kind,
                Description = own.Description ?? string.Empty,
                Sources = (own.Sources ?? []).Where(s => s != null).ToList(),
            };
        return new CliConfigProviderStorage
        {
            Kind = CliConfigStorageKinds.Custom,
            Description = provider is ICliConfigWriter ? "Saved by the provider" : "Read by the provider",
        };
    }

    /// <summary>A section as the API returns it; <paramref name="writable"/>: the provider implements the writer.</summary>
    public static CliConfigSectionView Section(CliConfigSection section, bool writable)
    {
        var entries = (section.Entries ?? []).Select(e => Entry(e, writable)).ToList();
        var allowCustomKeys = writable && section.AllowCustomKeys;
        var mutable = allowCustomKeys || entries.Any(e => e.Mutable);
        return new CliConfigSectionView
        {
            Id = section.Id,
            Title = section.Title ?? section.Id,
            Description = section.Description ?? string.Empty,
            Mutable = mutable,
            Version = mutable ? section.Version : null,
            AllowCustomKeys = allowCustomKeys,
            Entries = entries,
        };
    }
}

/// <summary>
/// The server's config providers, read and changed with permissions, validation, conflicts, secrets masked, audit
/// (<c>config.update</c>) and events (<c>config:changed</c>), each stored provider with the store it saves to: the HTTP API
/// (admin plugin), the <c>config</c> command and the MCP tools all go through it.
/// </summary>
public sealed class CliConfigService : ICliConfigService
{
    private readonly IServiceProvider? _services;
    private readonly ILogger _logger;
    // The value of each requiresRestart entry changed so far, as it was at start (by pending id)
    private readonly Dictionary<string, JsonNode?> _startValues = new(StringComparer.Ordinal);

    public CliConfigService(CliConfigRegistry registry, IServiceProvider? services = null, ILogger<CliConfigService>? logger = null)
    {
        Registry = registry;
        _services = services;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public CliConfigRegistry Registry { get; }

    /// <summary>Where changes are audited; default the server's <see cref="IAuditStore"/> (audit or users plugin).</summary>
    public Func<IAuditStore?>? AuditStore { get; set; }

    /// <summary>The bus <c>config:changed</c> is published on; default the server's <see cref="ICliEventBus"/>.</summary>
    public Func<ICliEventBus?>? EventBus { get; set; }

    /// <summary>
    /// The restart service a change to a <c>requiresRestart</c> entry is marked pending with
    /// (<c>config:&lt;provider&gt;.&lt;section&gt;.&lt;key&gt;</c>); default the server's <see cref="ICliServerRestart"/>.
    /// </summary>
    public Func<ICliServerRestart?>? Restart { get; set; }

    private IAuditStore? Audit => AuditStore != null ? AuditStore() : _services?.GetService<IAuditStore>();
    private ICliServerRestart? RestartService => Restart != null ? Restart() : _services?.GetService<ICliServerRestart>();
    private ICliEventBus? Bus => EventBus != null ? EventBus() : _services?.GetService<ICliEventBus>();

    /// <summary>Completes once every provider has applied its stored settings.</summary>
    public Task ReadyAsync() => Registry.ReadyAsync();

    private static void RequirePermission(CliConfigContext context, string permission)
    {
        if (!context.HasPermission(permission))
            throw new CliConfigException(403, $"forbidden: missing permission {permission}");
    }

    private static bool CanSee(ICliConfigProvider provider, CliConfigContext context) =>
        string.IsNullOrEmpty(provider.RequiredPermission) || context.HasPermission(provider.RequiredPermission);

    private ICliConfigProvider Provider(string providerId, CliConfigContext context)
    {
        var provider = string.IsNullOrEmpty(providerId) ? null : Registry.Find(providerId);
        if (provider == null) throw new CliConfigException(404, $"unknown config provider: {providerId}");
        if (!CanSee(provider, context))
            throw new CliConfigException(403, $"forbidden: missing permission {provider.RequiredPermission}");
        return provider;
    }

    private async Task<CliConfigProviderView> ViewAsync(ICliConfigProvider provider, CliConfigContext context,
        CancellationToken cancellationToken)
    {
        var writer = provider is ICliConfigWriter;
        var sections = await provider.GetSectionsAsync(context, cancellationToken);
        return new CliConfigProviderView
        {
            Id = provider.Id,
            Title = provider.Title,
            Description = provider.Description ?? string.Empty,
            Order = provider.Order,
            Writable = writer && context.HasPermission(CliConfigPermissions.Write),
            Storage = CliConfigViews.Storage(provider),
            Store = DescribeProviderStore(provider),
            Sections = (sections ?? []).Select(s => CliConfigViews.Section(s, writer)).ToList(),
        };
    }

    /// <summary>The store providers use unless they have their own: its own description (<c>custom</c> when it has none).</summary>
    public CliConfigStoreDescription DescribeDefaultStore() => CliConfigStoreHelpers.Describe(Registry.DefaultStore);

    /// <summary>The store a provider's changes are saved to; null for a provider without one (read-only, or saving itself).</summary>
    public CliConfigStoreDescription? DescribeProviderStore(ICliConfigProvider provider) =>
        Registry.StoreOf(provider) is { } store ? CliConfigStoreHelpers.Describe(store) : null;

    public async Task<IReadOnlyList<CliConfigProviderView>> ListAsync(CliConfigContext context, CancellationToken cancellationToken = default)
    {
        RequirePermission(context, CliConfigPermissions.Read);
        await ReadyAsync();
        var views = new List<CliConfigProviderView>();
        foreach (var provider in Registry.Providers)
        {
            if (!CanSee(provider, context)) continue;
            try
            {
                views.Add(await ViewAsync(provider, context, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One failing provider does not hide the others
                _logger.LogError(ex, "Config provider '{Provider}' failed", provider.Id);
            }
        }
        return views;
    }

    public async Task<CliConfigProviderView> GetAsync(string providerId, CliConfigContext context, CancellationToken cancellationToken = default)
    {
        RequirePermission(context, CliConfigPermissions.Read);
        await ReadyAsync();
        return await ViewAsync(Provider(providerId, context), context, cancellationToken);
    }

    public async Task<CliConfigUpdateResponse> UpdateAsync(string providerId, string sectionId, CliConfigUpdateRequest request,
        CliConfigContext context, CancellationToken cancellationToken = default)
    {
        RequirePermission(context, CliConfigPermissions.Write);
        await ReadyAsync();
        var provider = Provider(providerId, context);
        if (provider is not ICliConfigWriter writer) throw new CliConfigException(400, $"config provider {provider.Id} is read-only");
        var changes = request?.Changes;
        if (changes == null || changes.Count == 0) throw new CliConfigException(400, "changes must be a non-empty object");
        var expected = request!.Version;

        var section = (await provider.GetSectionsAsync(context, cancellationToken)).FirstOrDefault(s => s.Id == sectionId)
            ?? throw new CliConfigException(404, $"unknown config section: {provider.Id}.{sectionId}");
        var before = CliConfigViews.Section(section, true);
        if (!before.Mutable) throw new CliConfigException(400, $"config section {provider.Id}.{sectionId} is read-only");

        // The server's checks: the key exists and is mutable, the value has the type and passes the rules
        var errors = new Dictionary<string, string>();
        foreach (var (key, value) in changes)
        {
            var entry = section.Entries.FirstOrDefault(e => e.Key == key);
            if (entry == null)
            {
                if (!before.AllowCustomKeys) errors[key] = "unknown setting";
                else if (!CliConfigConstants.Key.IsMatch(key)) errors[key] = "invalid key: use letters, digits, _, - and .";
                else if (value != null && CliConfigValues.Validate(CliConfigEntryTypes.Json, null, null, value) is { } jsonError)
                    errors[key] = jsonError;
                continue;
            }
            if (!entry.Mutable)
            {
                errors[key] = "read-only";
                continue;
            }
            if (CliConfigValues.Validate(entry, value) is { } error) errors[key] = error;
        }
        if (errors.Count == 0)
        {
            foreach (var (key, message) in await writer.ValidateAsync(sectionId, changes, context, cancellationToken))
                errors[key] = message;
        }
        if (errors.Count > 0) throw new CliConfigException(400, "validation failed", errors);
        if (expected != null && before.Version != null && expected != before.Version)
            throw new CliConfigException(409, "version conflict", null, before.Version);

        CliConfigUpdateResult result;
        try
        {
            result = await writer.UpdateAsync(sectionId, changes, expected, context, cancellationToken);
        }
        catch (CliConfigConflictException ex)
        {
            throw new CliConfigException(409, "version conflict", null, ex.CurrentVersion);
        }
        catch (CliConfigValidationException ex)
        {
            throw new CliConfigException(400, "validation failed", ex.Errors);
        }

        var updated = (await provider.GetSectionsAsync(context, cancellationToken)).FirstOrDefault(s => s.Id == sectionId);
        var after = CliConfigViews.Section(updated ?? section, true);
        var auditChanges = new JsonArray();
        foreach (var key in result.Applied)
        {
            var old = before.Entries.FirstOrDefault(e => e.Key == key);
            var now = after.Entries.FirstOrDefault(e => e.Key == key);
            changes.TryGetValue(key, out var raw);
            auditChanges.Add(new JsonObject
            {
                ["key"] = key,
                ["old"] = AuditValue(old, null, old: true),
                ["new"] = now != null || raw != null ? AuditValue(now ?? old, raw, old: false) : null,
            });
        }
        TrackRestart(provider, sectionId, result.Applied, before, after);
        var storeKind = DescribeProviderStore(provider)?.Kind;
        await AuditAsync(provider.Id, sectionId, result.Version, auditChanges, context, storeKind);
        await PublishAsync(provider.Id, sectionId, result, context, storeKind, cancellationToken);
        return new CliConfigUpdateResponse
        {
            Provider = provider.Id,
            Section = after.Mutable ? after.WithVersion(result.Version) : after,
            Applied = result.Applied.ToList(),
            RequiresRestart = result.RequiresRestart,
            Version = result.Version,
        };
    }

    /// <summary>
    /// Marks a changed <c>requiresRestart</c> entry as waiting for a restart, and clears it when its value is set back to
    /// what it was at start (a secret, whose value is never seen, stays marked).
    /// </summary>
    private void TrackRestart(ICliConfigProvider provider, string sectionId, IEnumerable<string> applied,
        CliConfigSectionView before, CliConfigSectionView after)
    {
        var restart = RestartService;
        if (restart == null) return;
        foreach (var key in applied)
        {
            var old = before.Entries.FirstOrDefault(e => e.Key == key);
            var now = after.Entries.FirstOrDefault(e => e.Key == key);
            if (old?.RequiresRestart != true && now?.RequiresRestart != true) continue;
            var id = $"config:{provider.Id}.{sectionId}.{key}";
            var sensitive = old?.Sensitive == true || now?.Sensitive == true;
            JsonNode? start;
            lock (_startValues)
            {
                if (!_startValues.TryGetValue(id, out start))
                {
                    start = old?.Value?.DeepClone();
                    _startValues[id] = start;
                }
            }
            var unchanged = !sensitive && JsonNode.DeepEquals(now?.Value, start);
            try
            {
                if (unchanged) restart.ClearRequired(id);
                else restart.MarkRequired(new CliRestartRequirementInput(id, "config", $"{provider.Title}: {(now ?? old)?.Label ?? key} changed"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Marking a restart failed");
            }
        }
    }

    /// <summary>How a value appears in the audit log: a secret as <c>***</c>, or null when cleared/unset.</summary>
    private static JsonNode? AuditValue(CliConfigEntryView? view, JsonNode? raw, bool old)
    {
        if (view?.Sensitive == true)
        {
            if (old) return view.IsSet == true ? CliConfigConstants.SecretMask : null;
            return raw == null || CliConfigValues.AsString(raw) == "" ? null : CliConfigConstants.SecretMask;
        }
        return (old ? view?.Value : view?.Value ?? raw)?.DeepClone();
    }

    private async Task AuditAsync(string providerId, string sectionId, string version, JsonArray changes, CliConfigContext context,
        string? storeKind)
    {
        var store = Audit;
        if (store == null) return;
        var userName = context.Caller?.UserName ?? "anonymous";
        var properties = new Dictionary<string, object?>
        {
            ["provider"] = providerId,
            ["section"] = sectionId,
            ["version"] = version,
        };
        if (storeKind != null) properties["store"] = storeKind;
        if (context.Via != null) properties["via"] = context.Via;
        properties["changes"] = changes;
        try
        {
            await store.LogAsync(new AuditEntry
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Event = CliConfigConstants.AuditEvent,
                Username = userName,
                UserId = context.Caller?.UserId,
                ActorId = context.Caller?.UserId,
                Actor = userName,
                Target = $"config:{providerId}.{sectionId}",
                Details = $"{providerId}.{sectionId}: {string.Join(", ", changes.Select(c => c?["key"]?.GetValue<string>()))}",
                Properties = properties,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auditing a config change failed");
        }
    }

    private async Task PublishAsync(string providerId, string sectionId, CliConfigUpdateResult result, CliConfigContext context,
        string? storeKind, CancellationToken cancellationToken)
    {
        var bus = Bus;
        if (bus == null) return;
        var data = new Dictionary<string, object?>
        {
            ["provider"] = providerId,
            ["section"] = sectionId,
            ["keys"] = result.Applied.ToList(),
            ["version"] = result.Version,
            ["requiresRestart"] = result.RequiresRestart,
        };
        if (storeKind != null) data["store"] = storeKind;
        if (!string.IsNullOrEmpty(context.Caller?.UserName)) data["user"] = context.Caller.UserName;
        try
        {
            await bus.PublishAsync(CliConfigConstants.ChangedEvent, data, CliEventAudience.WithPermission(CliConfigPermissions.Read),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Publishing a config change failed");
        }
    }
}
