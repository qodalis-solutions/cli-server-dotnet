using System.Globalization;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>A dependency of a NuGet package (<c>&lt;dependency id version /&gt;</c> of its nuspec).</summary>
public sealed record NuGetDependency(string Id, string? Range);

/// <summary>What a server plugin's main assembly declares (read without loading it).</summary>
public sealed record CliPluginAssemblyInfo(bool HasEntry, bool AutoInstall, string? AutoInstallReason);

/// <summary>
/// Reading NuGet packages without NuGet: the nuspec (id, version, dependency groups), the <c>lib/&lt;tfm&gt;</c> folder
/// this server can load, NuGet version ranges, and a plugin assembly's <c>[assembly: CliPlugin]</c>.
/// </summary>
public static partial class NuGetPackage
{
    /// <summary>The runtime this server runs on.</summary>
    public static readonly Version RuntimeVersion = new(Environment.Version.Major, Environment.Version.Minor);

    [GeneratedRegex(@"^(?:net(?<v>\d+\.\d+)|netcoreapp(?<c>\d+\.\d+)|netstandard(?<s>\d+\.\d+)|\.netstandard(?<s>\d+\.\d+)|\.netcoreapp(?<c>\d+\.\d+))$", RegexOptions.IgnoreCase)]
    private static partial Regex Tfm();

    [GeneratedRegex(@"^(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+.*)?$")]
    private static partial Regex Version4();

    /// <summary>
    /// How well a target framework fits this server (higher is better), null when it cannot load: <c>net5.0</c>..this
    /// runtime, then <c>netcoreapp</c>, then <c>netstandard2.1</c>/<c>2.0</c>. Platform-specific ones (<c>-windows</c>) do not.
    /// </summary>
    public static (int Kind, Version Version)? TfmRank(string? tfm)
    {
        if (string.IsNullOrEmpty(tfm)) return null;
        var m = Tfm().Match(tfm.Trim());
        if (!m.Success) return null;
        if (m.Groups["v"].Success)
        {
            var v = Version.Parse(m.Groups["v"].Value);
            return v.Major >= 5 && v <= RuntimeVersion ? (3, v) : null;
        }
        if (m.Groups["c"].Success) return (2, Version.Parse(m.Groups["c"].Value));
        var s = Version.Parse(m.Groups["s"].Value);
        return s <= new Version(2, 1) ? (1, s) : null;
    }

    /// <summary>The best of <paramref name="tfms"/> for this server, null when none fits.</summary>
    public static string? BestTfm(IEnumerable<string> tfms) =>
        tfms.Select(t => (Tfm: t, Rank: TfmRank(t))).Where(t => t.Rank != null)
            .OrderByDescending(t => t.Rank!.Value.Kind).ThenByDescending(t => t.Rank!.Value.Version)
            .Select(t => t.Tfm).FirstOrDefault();

    /// <summary>Compares NuGet versions (1 to 4 parts, prereleases before their release, labels as semver).</summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = Version4().Match(a);
        var pb = Version4().Match(b);
        if (!pa.Success || !pb.Success) return Math.Sign(string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
        for (var i = 1; i <= 4; i++)
        {
            var na = pa.Groups[i].Success ? long.Parse(pa.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
            var nb = pb.Groups[i].Success ? long.Parse(pb.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
            if (na != nb) return na < nb ? -1 : 1;
        }
        var xa = pa.Groups[5].Success ? pa.Groups[5].Value : null;
        var xb = pb.Groups[5].Success ? pb.Groups[5].Value : null;
        if (xa == null && xb == null) return 0;
        if (xa == null) return 1;
        if (xb == null) return -1;
        return DashboardMarketplace.CompareVersions($"0.0.0-{xa}", $"0.0.0-{xb}");
    }

    /// <summary>True for a NuGet version (1 to 4 numeric parts, a prerelease label).</summary>
    public static bool IsVersion(string? version) => version != null && version.Length <= 64 && Version4().IsMatch(version);

    /// <summary>
    /// True when <paramref name="version"/> is in the NuGet range: <c>1.0</c> (at least), <c>[1.0]</c> (exactly),
    /// <c>[1.0,2.0)</c>, <c>(,2.0]</c>, ... An empty range accepts every version; one it cannot read accepts none.
    /// </summary>
    public static bool SatisfiesNuGetRange(string version, string? range)
    {
        var text = (range ?? string.Empty).Trim();
        if (text.Length == 0 || text == "*") return true;
        if (!IsVersion(version)) return false;
        if (text[0] is not ('[' or '('))
            return IsVersion(text) && CompareVersions(version, text) >= 0;
        if (text.Length < 2 || text[^1] is not (']' or ')')) return false;
        var lowInclusive = text[0] == '[';
        var highInclusive = text[^1] == ']';
        var inner = text[1..^1];
        var parts = inner.Split(',');
        if (parts.Length == 1)
        {
            var exact = parts[0].Trim();
            return lowInclusive && highInclusive && IsVersion(exact) && CompareVersions(version, exact) == 0;
        }
        if (parts.Length != 2) return false;
        var low = parts[0].Trim();
        var high = parts[1].Trim();
        if (low.Length > 0)
        {
            if (!IsVersion(low)) return false;
            var c = CompareVersions(version, low);
            if (c < 0 || (c == 0 && !lowInclusive)) return false;
        }
        if (high.Length > 0)
        {
            if (!IsVersion(high)) return false;
            var c = CompareVersions(version, high);
            if (c > 0 || (c == 0 && !highInclusive)) return false;
        }
        return true;
    }

    /// <summary>The lowest version of a NuGet range (what NuGet resolves to), null when it has none.</summary>
    public static string? RangeMinimum(string? range)
    {
        var text = (range ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        if (text[0] is not ('[' or '(')) return IsVersion(text) ? text : null;
        var low = text[1..].Split(',')[0].TrimEnd(']', ')').Trim();
        return IsVersion(low) ? low : null;
    }

    /// <summary>The nuspec of a package: its id, version and the dependencies of the group that fits this server best.</summary>
    public sealed record Nuspec(string Id, string Version, IReadOnlyList<NuGetDependency> Dependencies);

    /// <summary>Reads the <c>.nuspec</c> at the package's root. Throws <see cref="InvalidDataException"/>.</summary>
    public static Nuspec ReadNuspec(ZipArchive zip)
    {
        var entry = zip.Entries.FirstOrDefault(e => !e.FullName.Contains('/') && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("no .nuspec");
        XDocument doc;
        using (var stream = entry.Open())
        {
            doc = XDocument.Load(stream, LoadOptions.None);
        }
        var metadata = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata") ?? throw new InvalidDataException("no metadata");
        string? Value(string name) => metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        var id = Value("id");
        var version = Value("version");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version)) throw new InvalidDataException("no id or version");
        var dependencies = new List<NuGetDependency>();
        var deps = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "dependencies");
        if (deps != null)
        {
            static IEnumerable<NuGetDependency> Of(XElement parent) => parent.Elements().Where(e => e.Name.LocalName == "dependency")
                .Select(e => new NuGetDependency((string?)e.Attribute("id") ?? string.Empty, (string?)e.Attribute("version")))
                .Where(d => d.Id.Length > 0);
            var groups = deps.Elements().Where(e => e.Name.LocalName == "group").ToList();
            if (groups.Count == 0)
            {
                dependencies.AddRange(Of(deps));
            }
            else
            {
                var best = BestTfm(groups.Select(g => (string?)g.Attribute("targetFramework")).OfType<string>());
                var group = groups.FirstOrDefault(g => best != null && (string?)g.Attribute("targetFramework") == best)
                    ?? groups.FirstOrDefault(g => g.Attribute("targetFramework") == null);
                if (group != null) dependencies.AddRange(Of(group));
            }
        }
        return new Nuspec(id, version, dependencies);
    }

    /// <summary>The <c>lib/&lt;tfm&gt;</c> folder that fits this server best, null when none does.</summary>
    public static string? LibFolder(ZipArchive zip)
    {
        var tfms = zip.Entries.Select(e => e.FullName.Split('/'))
            .Where(p => p.Length == 3 && p[0].Equals("lib", StringComparison.OrdinalIgnoreCase) && p[2].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1]).Distinct(StringComparer.OrdinalIgnoreCase);
        var best = BestTfm(tfms);
        return best == null ? null : $"lib/{best}/";
    }

    /// <summary>
    /// The files of the package's <c>lib/&lt;tfm&gt;</c> folder (assemblies, their symbols and docs, no folders below),
    /// by file name.
    /// </summary>
    public static IReadOnlyList<ZipArchiveEntry> LibFiles(ZipArchive zip, string folder) =>
        zip.Entries.Where(e => e.FullName.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                && e.FullName.Length > folder.Length && !e.FullName[folder.Length..].Contains('/')
                && Path.GetExtension(e.FullName).ToLowerInvariant() is ".dll" or ".pdb" or ".xml" or ".json")
            .ToList();

    /// <summary>
    /// Reads the plugin's assembly attributes without loading it: <c>[assembly: CliPlugin(...)]</c>, and
    /// <c>[assembly: AssemblyMetadata("QodalisCliAutoInstall", "false")]</c> with <c>QodalisCliAutoInstallReason</c>.
    /// Throws <see cref="BadImageFormatException"/> when it is not an assembly.
    /// </summary>
    public static CliPluginAssemblyInfo ReadPluginAssembly(Stream assembly)
    {
        using var pe = new PEReader(assembly, PEStreamOptions.LeaveOpen | PEStreamOptions.PrefetchEntireImage);
        if (!pe.HasMetadata) throw new BadImageFormatException("not a .NET assembly");
        var md = pe.GetMetadataReader();
        var hasEntry = false;
        bool? autoInstall = null;
        string? reason = null;
        foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = md.GetCustomAttribute(handle);
            var (ns, name) = AttributeType(md, attribute);
            if (ns == "Qodalis.Cli.Plugins" && name == "CliPluginAttribute")
            {
                hasEntry = true;
            }
            else if (ns == "System.Reflection" && name == "AssemblyMetadataAttribute")
            {
                var blob = md.GetBlobReader(attribute.Value);
                if (blob.Length < 2 || blob.ReadUInt16() != 1) continue;
                var key = blob.ReadSerializedString();
                var value = blob.ReadSerializedString();
                if (key == "QodalisCliAutoInstall") autoInstall = !string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
                else if (key == "QodalisCliAutoInstallReason") reason = value;
            }
        }
        return new CliPluginAssemblyInfo(hasEntry, autoInstall ?? true, autoInstall == false ? reason : null);
    }

    private static (string? Namespace, string? Name) AttributeType(MetadataReader md, CustomAttribute attribute)
    {
        EntityHandle type;
        if (attribute.Constructor.Kind == HandleKind.MemberReference)
            type = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
        else if (attribute.Constructor.Kind == HandleKind.MethodDefinition)
            type = md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
        else
            return (null, null);
        return type.Kind switch
        {
            HandleKind.TypeReference => (md.GetString(md.GetTypeReference((TypeReferenceHandle)type).Namespace),
                md.GetString(md.GetTypeReference((TypeReferenceHandle)type).Name)),
            HandleKind.TypeDefinition => (md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)type).Namespace),
                md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)type).Name)),
            _ => (null, null),
        };
    }
}
