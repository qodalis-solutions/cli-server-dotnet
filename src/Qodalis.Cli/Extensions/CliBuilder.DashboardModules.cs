using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Qodalis.Cli.Models;

namespace Qodalis.Cli.Extensions;

public partial class CliBuilder
{
    /// <summary><c>/*! &lt;package&gt; &lt;version&gt; */</c>, the first line of a module's <c>extension.js</c> (the dashboard repo's module builds).</summary>
    private static readonly Regex ModuleBanner = new(@"^/\*!\s+((?:@[a-z0-9-~][a-z0-9-._~]*/)?[a-z0-9-~][a-z0-9-._~]*)\s+(\S{1,50})\s+\*/", RegexOptions.CultureInvariant);

    /// <summary>
    /// The npm package and version of a module's build: the banner of its <c>extension.js</c>, else its
    /// <c>package.json</c>. Null when neither says.
    /// </summary>
    public static (string Package, string Version)? DashboardModuleIdentity(IFileProvider files)
    {
        ArgumentNullException.ThrowIfNull(files);
        try
        {
            var js = files.GetFileInfo("extension.js");
            if (js.Exists)
            {
                using var reader = new StreamReader(js.CreateReadStream());
                var head = new char[300];
                var read = reader.ReadBlock(head, 0, head.Length);
                var banner = ModuleBanner.Match(new string(head, 0, read));
                if (banner.Success) return (banner.Groups[1].Value, banner.Groups[2].Value);
            }
            var pkg = files.GetFileInfo("package.json");
            if (pkg.Exists)
            {
                using var stream = pkg.CreateReadStream();
                using var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    && doc.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                    && name.GetString()!.Contains("cli-dashboard-", StringComparison.Ordinal))
                    return (name.GetString()!, version.GetString()!);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable: no identity
        }
        return null;
    }

    private readonly Dictionary<string, (CliDashboardModule Module, IFileProvider Files)> _dashboardModules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IFileProvider?> _dashboardModuleOverrides = new(StringComparer.Ordinal);
    // Every module a plugin added, with or without a build, so ConfigureDashboardModule can register one later
    private readonly Dictionary<string, CliDashboardModule> _dashboardModuleDefinitions = new(StringComparer.Ordinal);
    private bool _dashboardModuleFilter;

    /// <summary>
    /// Adds a dashboard module embedded in <paramref name="assembly"/> under <c>wwwroot/&lt;id&gt;</c> (or
    /// <c>wwwroot/&lt;build&gt;</c>, <see cref="CliDashboardModule.Build"/>; the plugin's
    /// build of <c>@qodalis/cli-dashboard-module-&lt;id&gt;</c>): serves its files at the module's prefix, outside the
    /// API and to anyone (the dashboard's <c>import()</c> sends no token; the page's API enforces access), and
    /// registers it as a dashboard extension. Does nothing when the assembly carries no build, when the CLI's routes
    /// are off, or when the module is already registered.
    /// </summary>
    public CliBuilder AddDashboardModule(CliDashboardModule module, Assembly assembly) =>
        AddDashboardModule(module, EmbeddedDashboardModule(assembly, module.BuildId));

    /// <summary>
    /// Adds a dashboard module from <paramref name="files"/> (<c>extension.js</c>, optional <c>extension.css</c>); see
    /// <see cref="AddDashboardModule(CliDashboardModule, Assembly)"/>. A null provider, or one without
    /// <c>extension.js</c>, leaves the page out.
    /// </summary>
    public CliBuilder AddDashboardModule(CliDashboardModule module, IFileProvider? files)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (!ServerOptions.CliRoutes || _dashboardModules.ContainsKey(module.Id)) return this;
        _dashboardModuleDefinitions.TryAdd(module.Id, module);
        if (_dashboardModuleOverrides.TryGetValue(module.Id, out var chosen) || _dashboardModuleOverrides.TryGetValue(module.BuildId, out chosen)) files = chosen;
        if (!HasModuleEntry(files)) return this;

        AddDashboardExtension(DashboardModuleExtension(module, files!));
        Serve(module, files!);
        return this;
    }

    private void Serve(CliDashboardModule module, IFileProvider files)
    {
        _dashboardModules[module.Id] = (module, files);
        if (_dashboardModuleFilter) return;
        _dashboardModuleFilter = true;
        Services.AddTransient<IStartupFilter>(_ => new DashboardModulesStartupFilter(_dashboardModules));
    }

    /// <summary>
    /// Serves another build of a dashboard module (<paramref name="files"/>), or leaves the page out (null). Works
    /// before or after the plugin adds the module (also when the plugin's package carries no build). Pages built from
    /// this module's build (<see cref="CliDashboardModule.Build"/>) follow it.
    /// </summary>
    public CliBuilder ConfigureDashboardModule(string id, IFileProvider? files)
    {
        _dashboardModuleOverrides[id] = files;
        foreach (var module in _dashboardModuleDefinitions.Values.Where(m => m.Id == id || (m.Build == id && !_dashboardModuleOverrides.ContainsKey(m.Id))).ToList())
        {
            var index = _dashboardModules.ContainsKey(module.Id) ? _dashboardExtensions.FindIndex(e => e.Id == module.Id) : -1;
            _dashboardModules.Remove(module.Id);
            if (index >= 0) _dashboardExtensions.RemoveAt(index);
            if (!HasModuleEntry(files)) continue;

            _dashboardExtensions.Insert(index >= 0 ? index : _dashboardExtensions.Count, DashboardModuleExtension(module, files!).Validate());
            Serve(module, files!);
        }
        return this;
    }

    /// <summary>The build embedded in <paramref name="assembly"/> under <c>wwwroot/&lt;id&gt;</c> (a module's build id), or null.</summary>
    public static IFileProvider? EmbeddedDashboardModule(Assembly assembly, string id)
    {
        try
        {
            var files = new ManifestEmbeddedFileProvider(assembly, $"wwwroot/{id}");
            return files.GetFileInfo("extension.js").Exists ? files : null;
        }
        catch (InvalidOperationException)
        {
            // No embedded files at all
            return null;
        }
    }

    /// <summary>
    /// The module's dashboard extension; the URLs carry the files' hash (<c>?v=</c>), so a new build loads at once. Its
    /// <c>package</c> and <c>version</c> come from the build (<see cref="DashboardModuleIdentity"/>).
    /// </summary>
    public static CliDashboardExtension DashboardModuleExtension(CliDashboardModule module, IFileProvider files)
    {
        string? Version(string file)
        {
            var info = files.GetFileInfo(file);
            if (!info.Exists) return null;
            using var stream = info.CreateReadStream();
            return Convert.ToHexStringLower(SHA256.HashData(stream))[..12];
        }

        var prefix = module.ResolvedPrefix;
        var css = Version("extension.css");
        var identity = DashboardModuleIdentity(files);
        return new CliDashboardExtension
        {
            Package = identity?.Package,
            Version = identity?.Version,
            Id = module.Id,
            Title = module.Title,
            Titles = module.Titles,
            Icon = module.Icon,
            Group = module.Group,
            Order = module.Order,
            Permission = module.Permission,
            Widgets = module.Widgets,
            Search = module.Search,
            Entry = $"{prefix}/extension.js?v={Version("extension.js")}",
            Styles = css == null ? null : [$"{prefix}/extension.css?v={css}"],
        };
    }

    private static bool HasModuleEntry(IFileProvider? files) => files != null && files.GetFileInfo("extension.js").Exists;

    /// <summary>Serves every registered module's files at its prefix, before the rest of the pipeline (no auth).</summary>
    private sealed class DashboardModulesStartupFilter(Dictionary<string, (CliDashboardModule Module, IFileProvider Files)> modules) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            foreach (var (module, files) in modules.Values)
            {
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = files,
                    RequestPath = module.ResolvedPrefix,
                    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
                });
            }
            next(app);
        };
    }
}
