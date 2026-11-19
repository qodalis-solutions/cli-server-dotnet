using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Qodalis.Cli.Security;

/// <summary>Where a catalog permission is used.</summary>
public static class CliPermissionSurface
{
    /// <summary>A permission the CLI server, its plugins or its commands check (granted to users and <c>cli</c> tokens).</summary>
    public const string Cli = "cli";

    /// <summary>A permission the host application's own routes check (the only ones a <c>host</c> token may carry).</summary>
    public const string Host = "host";
}

/// <summary>A permission users can choose when they grant it (to a group or an API token).</summary>
/// <param name="Name">A concrete permission (<c>files:read</c>): lower case, one <c>:</c>, no wildcard.</param>
/// <param name="Description">One line shown next to the name.</param>
/// <param name="Group">The heading the permission is listed under.</param>
/// <param name="Surface"><see cref="CliPermissionSurface.Cli"/> or <see cref="CliPermissionSurface.Host"/>.</param>
public sealed record CliPermission(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("surface")] string Surface);

/// <summary>
/// The permissions the server, its plugins and the host declare, in registration order. The catalog is for
/// choosing permissions, not for validating them: groups and <c>cli</c> tokens still accept any permission.
/// </summary>
public interface ICliPermissionCatalog
{
    /// <summary>Every entry in registration order (core, plugins, host).</summary>
    IReadOnlyList<CliPermission> All { get; }

    /// <summary>The entry named <paramref name="name"/> (case-insensitive), or null.</summary>
    CliPermission? Find(string name);
}

/// <summary>Collects entries for <see cref="Extensions.CliBuilder.AddPermissions"/>; every entry belongs to the last <see cref="Group"/>.</summary>
public sealed class CliPermissionCatalogBuilder
{
    private readonly string _surface;
    private string? _group;

    internal CliPermissionCatalogBuilder(string surface) => _surface = surface;

    internal List<CliPermission> Entries { get; } = [];

    /// <summary>Starts a group: the following <see cref="Add"/> calls are listed under <paramref name="group"/>.</summary>
    public CliPermissionCatalogBuilder Group(string group)
    {
        _group = group;
        return this;
    }

    /// <summary>Adds a permission to the current group.</summary>
    public CliPermissionCatalogBuilder Add(string name, string description)
    {
        Entries.Add(new CliPermission(name, description, _group!, _surface));
        return this;
    }
}

/// <summary>The catalog <see cref="Extensions.CliBuilder"/> fills at startup; read-only once resolved.</summary>
internal sealed class CliPermissionCatalog : ICliPermissionCatalog
{
    private static readonly Regex NamePattern = new("^[a-z0-9_.-]+:[a-z0-9_.-]+$", RegexOptions.CultureInvariant);

    private readonly List<CliPermission> _entries = [];
    private readonly Dictionary<string, CliPermission> _byName = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _frozen;

    public IReadOnlyList<CliPermission> All => _entries;

    public CliPermission? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Stops registration; called when the catalog is first resolved.</summary>
    public CliPermissionCatalog Freeze()
    {
        _frozen = true;
        return this;
    }

    /// <summary>Validates and adds <paramref name="entries"/> all or nothing.</summary>
    public void AddRange(IReadOnlyList<CliPermission> entries)
    {
        if (_frozen)
            throw new InvalidOperationException("permissions must be registered at startup, before the catalog is used");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            // The duplicate check comes first so FILES:READ is reported as a duplicate of files:read.
            if (_byName.ContainsKey(entry.Name ?? string.Empty) || !seen.Add(entry.Name ?? string.Empty))
                throw new InvalidOperationException($"permission already registered: {entry.Name}");
            if (!IsValid(entry))
                throw new ArgumentException($"invalid permission entry: {entry.Name}");
        }

        foreach (var entry in entries)
        {
            _entries.Add(entry);
            _byName[entry.Name] = entry;
        }
    }

    private static bool IsValid(CliPermission entry) =>
        entry.Name != null && NamePattern.IsMatch(entry.Name)
        && entry.Description is { Length: >= 1 and <= 200 } && !entry.Description.Any(char.IsControl)
        && entry.Group is { Length: >= 1 and <= 64 } && !entry.Group.Any(char.IsControl)
        && entry.Surface is CliPermissionSurface.Cli or CliPermissionSurface.Host;

    /// <summary>The route-policy permissions core checks (contract C2), registered by every <see cref="Extensions.CliBuilder"/>.</summary>
    internal static void AddCoreEntries(CliPermissionCatalogBuilder p) => p
        .Group("Commands")
        .Add(CliRoutePolicy.CommandsExecute, "Run commands on the server")
        .Add(CliRoutePolicy.ShellExecute, "Open an interactive shell on the server")
        .Group("Files")
        .Add(CliRoutePolicy.FilesRead, "List, read and download files")
        .Add(CliRoutePolicy.FilesWrite, "Upload, create and delete files")
        .Group("Data")
        .Add(CliRoutePolicy.DataQuery, "Query the data explorer's sources")
        .Group("Jobs")
        .Add(CliRoutePolicy.JobsRead, "See jobs and their runs")
        .Add(CliRoutePolicy.JobsManage, "Start, stop, pause and change jobs")
        .Group("Server")
        .Add(CliRoutePolicy.LogsRead, "Read the server's logs")
        .Add(CliRoutePolicy.AdminAccess, "Open the admin dashboard and its server pages")
        .Group("Audit")
        .Add(CliRoutePolicy.AuditRead, "Read the audit log")
        .Add(CliRoutePolicy.AuditClear, "Clear the audit log");
}
