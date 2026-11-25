using System.Reflection;
using System.Text.Json.Serialization;

namespace Qodalis.Cli.Models;

/// <summary>
/// A server plugin package the host installed (users, admin, jobs, a storage provider, ...), as listed by the
/// admin dashboard's plugins page. Read from the package's assembly: its name, version, description and icon.
/// </summary>
public class CliInstalledPlugin
{
    private const string PackagePrefix = "Qodalis.Cli.Plugin.";

    /// <summary>
    /// The <see cref="AssemblyMetadataAttribute"/> key of a package's icon. In the plugin's project:
    /// <c>&lt;AssemblyMetadata Include="QodalisCliIcon" Value="🧩" /&gt;</c>.
    /// </summary>
    public const string IconMetadataKey = "QodalisCliIcon";

    /// <summary>The package id, e.g. <c>Qodalis.Cli.Plugin.Users</c>.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>A short name, e.g. <c>Users</c> or <c>Jobs.EfCore</c>.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>The package version, without the build metadata (e.g. <c>1.0.0-beta.15</c>).</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>What the package does (its NuGet description).</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>One emoji or an image URL for the package (<see cref="IconMetadataKey"/>); <c>null</c> when it has none.</summary>
    [JsonPropertyName("icon")]
    public string? Icon { get; init; }

    /// <summary>
    /// How it was installed: null when the host added it in code, <see cref="MarketplaceSource"/> when it was installed
    /// at runtime from the marketplace.
    /// </summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    /// <summary>The <see cref="Source"/> of a plugin installed from the marketplace.</summary>
    public const string MarketplaceSource = "marketplace";

    /// <summary>Describes the package an assembly belongs to.</summary>
    public static CliInstalledPlugin FromAssembly(Assembly assembly)
    {
        var id = assembly.GetName().Name ?? assembly.FullName ?? "unknown";
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString();
        return new CliInstalledPlugin
        {
            Id = id,
            Name = id.StartsWith(PackagePrefix, StringComparison.Ordinal) ? id[PackagePrefix.Length..]
                : id == "Qodalis.Cli" ? "Core" : id,
            // Drop the source revision the SDK appends (1.0.0-beta.15+abc123).
            Version = version?.Split('+')[0],
            Description = assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description,
            Icon = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == IconMetadataKey && !string.IsNullOrWhiteSpace(a.Value))?.Value?.Trim(),
        };
    }
}

/// <summary>The server plugins installed on this host, in the order they were added (see <c>CliBuilder.AddInstalledPlugin</c>).</summary>
public class CliInstalledPlugins(IReadOnlyList<CliInstalledPlugin> plugins)
{
    /// <summary>Every installed plugin, the core server first.</summary>
    public IReadOnlyList<CliInstalledPlugin> All { get; } = plugins;
}
