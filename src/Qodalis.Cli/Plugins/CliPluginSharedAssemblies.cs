using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Qodalis.Cli.Plugins;

/// <summary>
/// The folders of the installed plugins that share their assemblies with the others (a database driver:
/// <see cref="CliPluginContext.ShareAssemblies"/>). A plugin's context looks an assembly up there when neither the host
/// nor its own folder has it, and loads it in its own context: the driver binds to that plugin's copy of what it builds
/// on (EF Core), never to another plugin's.
/// <para>
/// The host's code (a store or the databases plugin the host references) asks for a driver through
/// <see cref="CliPluginLoadContext.FindAssembly(string, AssemblyLoadContext)"/>: the assembly is then loaded in the
/// server's own context from a shared folder (<see cref="LoadIntoDefault"/>), and so are the assemblies it needs in turn,
/// and nothing else: a plugin's failed lookup never loads a shared assembly in the server's context.
/// </para>
/// </summary>
internal sealed class CliPluginSharedAssemblies
{
    private readonly List<string> _dirs = [];
    private readonly object _lock = new();

    // The folders shared in this process (every server's), for the host's code; the assemblies loaded from them in the
    // server's own context, whose dependencies are resolved from them too
    private static readonly List<string> HostDirs = [];
    private static readonly HashSet<Assembly> LoadedInDefault = [];
    private static bool _hooked;

    /// <summary>The shared folders, in the order they were added.</summary>
    public IReadOnlyList<string> Dirs
    {
        get
        {
            lock (_lock) return [.. _dirs];
        }
    }

    /// <summary>Shares <paramref name="dir"/> (once); false when it already is.</summary>
    public bool Add(string dir)
    {
        var full = Path.GetFullPath(dir);
        lock (_lock)
        {
            if (_dirs.Contains(full, StringComparer.Ordinal)) return false;
            _dirs.Add(full);
        }
        lock (HostDirs)
        {
            if (!HostDirs.Contains(full, StringComparer.Ordinal)) HostDirs.Add(full);
            if (!_hooked)
            {
                _hooked = true;
                AppDomain.CurrentDomain.AssemblyResolve += ResolveForLoaded;
                AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveUnmanagedForLoaded;
            }
        }
        return true;
    }

    /// <summary>The file of a managed assembly in the first shared folder that has it; null when none does (or the host has it).</summary>
    public string? ResolveManaged(AssemblyName assemblyName) => ResolveManaged(Dirs, assemblyName);

    /// <summary>The file of a native library in the first shared folder that has it; null when none does.</summary>
    public string? ResolveUnmanaged(string name) => ResolveUnmanaged(Dirs, name);

    private static string? ResolveManaged(IEnumerable<string> dirs, AssemblyName assemblyName) =>
        dirs.Select(d => CliPluginLoadContext.ResolveManagedIn(d, assemblyName)).FirstOrDefault(p => p != null);

    private static string? ResolveUnmanaged(IEnumerable<string> dirs, string name) =>
        dirs.Select(d => CliPluginLoadContext.ResolveUnmanagedIn(d, name)).FirstOrDefault(p => p != null);

    private static List<string> SharedWithHost()
    {
        lock (HostDirs) return [.. HostDirs];
    }

    /// <summary>
    /// <paramref name="name"/> loaded in the server's own context from a folder shared in this process (the host's code
    /// asked for a driver it does not reference); null when no shared folder has it.
    /// </summary>
    public static Assembly? LoadIntoDefault(string name)
    {
        var path = ResolveManaged(SharedWithHost(), new AssemblyName(name));
        return path == null ? null : LoadIntoDefaultFrom(path);
    }

    private static Assembly LoadIntoDefaultFrom(string path)
    {
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        lock (LoadedInDefault) LoadedInDefault.Add(assembly);
        return assembly;
    }

    private static bool WasLoadedInDefault(Assembly? assembly)
    {
        if (assembly == null) return false;
        lock (LoadedInDefault) return LoadedInDefault.Contains(assembly);
    }

    // An assembly a shared one needs, in the server's context: from the shared folders too
    private static Assembly? ResolveForLoaded(object? sender, ResolveEventArgs args) =>
        WasLoadedInDefault(args.RequestingAssembly) && ResolveManaged(SharedWithHost(), new AssemblyName(args.Name)) is { } path
            ? LoadIntoDefaultFrom(path)
            : null;

    private static IntPtr ResolveUnmanagedForLoaded(Assembly assembly, string name) =>
        WasLoadedInDefault(assembly) && ResolveUnmanaged(SharedWithHost(), name) is { } path ? NativeLibrary.Load(path) : IntPtr.Zero;
}
