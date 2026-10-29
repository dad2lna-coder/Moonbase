using System.Reflection;
using System.Runtime.Loader;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;

namespace Moonfin.Server.Plugins;

/// <summary>
/// Modules compiled into Moonbase, plus server dlls under <c>{data}/moonfin-plugins</c>.
/// </summary>
public sealed class MoonbaseModuleCatalog
{
    private static int _resolveHooked;

    private readonly List<IMoonbaseModule> _modules = new();
    private readonly Dictionary<string, PluginRouteHandler> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _started = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _notes = new();
    private readonly object _gate = new();
    private string? _loadedRoot;

    public IReadOnlyList<IMoonbaseModule> Modules
    {
        get
        {
            lock (_gate)
            {
                return _modules.ToArray();
            }
        }
    }

    public string? PluginsRoot { get; private set; }

    public IReadOnlyList<string> Notes
    {
        get
        {
            lock (_gate)
            {
                return _notes.ToArray();
            }
        }
    }

    public static MoonbaseModuleCatalog Discover(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        var catalog = new MoonbaseModuleCatalog();
        catalog.AddBuiltIn();
        var root = ResolvePluginsRoot(services, applicationHost);
        catalog.PluginsRoot = root;
        if (!string.IsNullOrWhiteSpace(root))
        {
            catalog.LoadExternal(root);
        }

        return catalog;
    }

    public void EnsureExternalLoaded(string pluginsRoot)
    {
        if (string.IsNullOrWhiteSpace(pluginsRoot))
        {
            return;
        }

        PluginsRoot ??= pluginsRoot;
        if (string.Equals(_loadedRoot, Path.GetFullPath(pluginsRoot), StringComparison.Ordinal))
        {
            return;
        }

        LoadExternal(pluginsRoot);
    }

    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> ServerProvides()
    {
        lock (_gate)
        {
            return _modules.ToDictionary(
                module => module.Id,
                module => (IReadOnlyCollection<string>)module.Provides.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlySet<string> FailedIds()
    {
        lock (_gate)
        {
            return new HashSet<string>(_failed, StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool TryGetRoute(string id, string method, string path, out PluginRouteHandler? handler)
    {
        lock (_gate)
        {
            return _routes.TryGetValue(RouteKey(id, method, path), out handler);
        }
    }

    public void StartWhere(IServiceProvider services, Func<string, bool> isActive, Action<string>? onError = null)
    {
        List<IMoonbaseModule> snapshot;
        lock (_gate)
        {
            snapshot = _modules.ToList();
        }

        foreach (var module in snapshot)
        {
            if (!isActive(module.Id))
            {
                continue;
            }

            lock (_gate)
            {
                if (!_started.Add(module.Id))
                {
                    continue;
                }
            }

            try
            {
                module.OnStart(services);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _started.Remove(module.Id);
                }

                onError?.Invoke(module.Id + " OnStart failed: " + ex.GetType().Name);
            }
        }
    }

    public void Stop(string id)
    {
        IMoonbaseModule? module;
        lock (_gate)
        {
            if (!_started.Remove(id))
            {
                return;
            }

            module = _modules.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        try
        {
            module?.OnStop();
        }
        catch
        {
            // Stopping a plugin must not take the host request down.
        }
    }

    public void StopAll()
    {
        List<string> ids;
        lock (_gate)
        {
            ids = _started.ToList();
        }

        foreach (var id in ids)
        {
            Stop(id);
        }
    }

    private void AddBuiltIn()
    {
        foreach (var type in SafeTypes(typeof(IMoonbaseModule).Assembly))
        {
            if (!IsModuleType(type))
            {
                continue;
            }

            TryAdd(type, expectedId: null);
        }
    }

    private void LoadExternal(string pluginsRoot)
    {
        EnsureResolveHook();
        var full = Path.GetFullPath(pluginsRoot);
        _loadedRoot = full;
        if (!Directory.Exists(full))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(full))
        {
            var folder = Path.GetFileName(dir);
            if (folder.StartsWith(".", StringComparison.Ordinal) || folder.StartsWith("_", StringComparison.Ordinal))
            {
                continue;
            }

            var manifestPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            PluginManifest? manifest;
            try
            {
                manifest = PluginManifest.TryParse(File.ReadAllText(manifestPath), out _);
            }
            catch
            {
                continue;
            }

            if (manifest == null || string.IsNullOrEmpty(manifest.Server))
            {
                continue;
            }

            if (!string.Equals(folder, manifest.Id, StringComparison.Ordinal))
            {
                Note(manifest.Id + " folder name does not match its id.");
                continue;
            }

            if (!TryLoadModule(dir, manifest))
            {
                lock (_gate)
                {
                    _failed.Add(manifest.Id);
                }
            }
        }
    }

    private bool TryLoadModule(string pluginDir, PluginManifest manifest)
    {
        foreach (var dll in CandidateDlls(pluginDir))
        {
            Assembly assembly;
            try
            {
                assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
            }
            catch (Exception ex)
            {
                Note(manifest.Id + " could not load " + Path.GetFileName(dll) + " (" + ex.GetType().Name + ").");
                continue;
            }

            foreach (var type in SafeTypes(assembly))
            {
                if (!IsModuleType(type) || !TypeMatches(type, manifest.Server!))
                {
                    continue;
                }

                if (TryAdd(type, manifest.Id))
                {
                    return true;
                }
            }
        }

        Note(manifest.Id + " server module " + manifest.Server + " was not found.");
        return false;
    }

    private bool TryAdd(Type type, string? expectedId)
    {
        IMoonbaseModule module;
        try
        {
            module = (IMoonbaseModule)Activator.CreateInstance(type)!;
        }
        catch (Exception ex)
        {
            Note((expectedId ?? type.Name) + " could not be created (" + ex.GetType().Name + ").");
            return false;
        }

        if (!PluginManifest.IsValidId(module.Id))
        {
            return false;
        }

        if (expectedId != null && !string.Equals(module.Id, expectedId, StringComparison.OrdinalIgnoreCase))
        {
            Note(expectedId + " module id does not match plugin.json.");
            return false;
        }

        lock (_gate)
        {
            if (_modules.Any(existing => string.Equals(existing.Id, module.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var table = new RouteTable(module.Id, _routes);
            try
            {
                module.MapRoutes(table);
            }
            catch (Exception ex)
            {
                Note(module.Id + " MapRoutes failed (" + ex.GetType().Name + ").");
            }

            _modules.Add(module);
            _failed.Remove(module.Id);
        }

        return true;
    }

    private static IEnumerable<string> CandidateDlls(string pluginDir)
    {
        var list = new List<string>();
        var serverDir = Path.Combine(pluginDir, "server");
        if (Directory.Exists(serverDir))
        {
            list.AddRange(Directory.GetFiles(serverDir, "*.dll"));
        }

        list.AddRange(Directory.GetFiles(pluginDir, "*.dll"));
        return list.Where(path => !IsHostAssembly(Path.GetFileName(path)));
    }

    private static bool IsHostAssembly(string fileName) =>
        fileName.Equals("Moonfin.Server.dll", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("SharpCompress.dll", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("MediaBrowser.", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("Newtonsoft.Json.dll", StringComparison.OrdinalIgnoreCase);

    private static bool IsModuleType(Type type) =>
        type is { IsClass: true, IsAbstract: false }
        && typeof(IMoonbaseModule).IsAssignableFrom(type)
        && type.GetConstructor(Type.EmptyTypes) != null;

    private static bool TypeMatches(Type type, string serverName) =>
        string.Equals(type.Name, serverName, StringComparison.Ordinal)
        || string.Equals(type.FullName, serverName, StringComparison.Ordinal);

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null)!;
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    private static string? ResolvePluginsRoot(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        var paths = FindPaths(services, applicationHost);
        if (!string.IsNullOrWhiteSpace(paths?.PluginConfigurationsPath))
        {
            return Path.Combine(paths.PluginConfigurationsPath, "Moonfin", MoonbasePlugins.FolderName);
        }

        return null;
    }

    private static IApplicationPaths? FindPaths(IServiceCollection services, object host)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ImplementationInstance is IApplicationPaths paths)
            {
                return paths;
            }
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var property in host.GetType().GetProperties(flags))
        {
            if (!typeof(IApplicationPaths).IsAssignableFrom(property.PropertyType) || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            try
            {
                if (property.GetValue(host) is IApplicationPaths paths)
                {
                    return paths;
                }
            }
            catch
            {
                // A host that hides its paths just defers plugin loading until the data folder exists.
            }
        }

        return null;
    }

    private static void EnsureResolveHook()
    {
        if (Interlocked.Exchange(ref _resolveHooked, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var requested = new AssemblyName(args.Name).Name;
            if (string.Equals(requested, "Moonfin.Server", StringComparison.Ordinal))
            {
                return typeof(IMoonbaseModule).Assembly;
            }

            return null;
        };
    }

    private static string RouteKey(string id, string method, string path) =>
        id + " " + method.ToUpperInvariant() + " " + path.Trim('/').Replace('\\', '/');

    private void Note(string message)
    {
        lock (_gate)
        {
            _notes.Add(message);
        }
    }

    private sealed class RouteTable : IMoonbaseRouteTable
    {
        private readonly string _id;
        private readonly Dictionary<string, PluginRouteHandler> _routes;

        public RouteTable(string id, Dictionary<string, PluginRouteHandler> routes)
        {
            _id = id;
            _routes = routes;
        }

        public void MapGet(string relativePath, PluginRouteHandler handler) => Map("GET", relativePath, handler);

        public void MapPost(string relativePath, PluginRouteHandler handler) => Map("POST", relativePath, handler);

        public void MapPut(string relativePath, PluginRouteHandler handler) => Map("PUT", relativePath, handler);

        public void MapDelete(string relativePath, PluginRouteHandler handler) => Map("DELETE", relativePath, handler);

        private void Map(string method, string relativePath, PluginRouteHandler handler)
        {
            var path = (relativePath ?? string.Empty).Trim('/').Replace('\\', '/');
            if (path.Length == 0 || path.Contains("..", StringComparison.Ordinal))
            {
                return;
            }

            var head = path.Split('/')[0];
            if (head.Equals("files", StringComparison.OrdinalIgnoreCase)
                || head.Equals("Data", StringComparison.OrdinalIgnoreCase)
                || head.Equals("Proxy", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _routes[RouteKey(_id, method, path)] = handler;
        }
    }
}
