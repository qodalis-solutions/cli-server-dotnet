using System.Reflection;
using System.Text.Json.Serialization;

namespace Qodalis.Cli.Models;

/// <summary>
/// A server plugin package the host installed (users, admin, jobs, a storage provider, ...), as listed by the
/// admin dashboard's plugins page. Read from the package's assembly: its name, version and description.
/// </summary>
public class CliInstalledPlugin
{
    private const string PackagePrefix = "Qodalis.Cli.Plugin.";

    /// <summary>The package id, e.g. <c>Qodalis.Cli.Plugin.Users</c>.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>A short name, e.g. <c>Users</c> or <c>Jobs.EfCore</c>.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

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
        };
    }
}

/// <summary>The server plugins installed on this host, in the order they were added (see <c>CliBuilder.AddInstalledPlugin</c>).</summary>
public class CliInstalledPlugins(IReadOnlyList<CliInstalledPlugin> plugins)
{
    public IReadOnlyList<CliInstalledPlugin> All { get; } = plugins;
}
