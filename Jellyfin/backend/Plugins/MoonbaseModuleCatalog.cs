using System.Reflection;
using System.Runtime.Loader;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;

namespace Moonfin.Server.Plugins;

/// <summary>
/// Modules compiled into Moonbase, plus server modules under <c>{data}/moonfin-plugins</c>.
/// Each external module loads in its own context bound to this already-loaded Moonfin.Server.
/// </summary>
public sealed class MoonbaseModuleCatalog
{
    private readonly List<IMoonbaseModule> _modules = new();
    private readonly Dictionary<string, PluginRouteHandler> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _started = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _register = new(StringComparer.OrdinalIgnoreCase);
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
        if (string.IsNullOrWhiteSpace(root))
        {
            // Same folder the registry uses once the plugin instance exists. RegisterServices
            // runs after the plugin constructor, so this is the path a restart will load.
            root = Path.Combine(MoonfinPlugin.ResolveDataFolderPath(), MoonbasePlugins.FolderName);
        }

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

    public bool ShouldRegisterServices(string id)
    {
        lock (_gate)
        {
            return _register.Contains(id);
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

            TryAdd(type, expectedId: null, registerServices: true);
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

        // Hide every installed plugin's server dll, including ones that stay unloaded,
        // so the next Jellyfin scan does not treat them as Jellyfin plugins.
        foreach (var dir in Directory.EnumerateDirectories(full))
        {
            var folder = Path.GetFileName(dir);
            if (folder.StartsWith(".", StringComparison.Ordinal) || folder.StartsWith("_", StringComparison.Ordinal))
            {
                continue;
            }

            ModuleAssemblyStore.HideServerAssemblies(dir, Note);
        }

        var discovered = new List<(PluginManifest Manifest, string Directory)>();
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

            discovered.Add((manifest, dir));
        }

        var host = PluginCapabilityResolver.HostCapabilities(MoonfinPlugin.Instance?.Configuration);
        var loadIds = PluginCapabilityResolver.SelectServerLoadIds(
            discovered.Select(item => item.Manifest).ToList(),
            MoonfinPluginRegistry.ReadDisabled(full),
            host,
            MoonbasePlugins.HostApi);
        var load = new HashSet<string>(loadIds, StringComparer.OrdinalIgnoreCase);
        foreach (var (manifest, dir) in discovered)
        {
            if (!load.Contains(manifest.Id))
            {
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
        var serverDir = Path.Combine(pluginDir, "server");
        foreach (var dll in CandidateDlls(pluginDir))
        {
            var fileName = Path.GetFileName(dll);
            Assembly assembly;
            var context = new PluginModuleLoadContext(manifest.Id, serverDir);
            try
            {
                assembly = context.LoadModule(dll);
            }
            catch (Exception ex)
            {
                Note(manifest.Id + " could not load " + fileName + " (" + Describe(ex) + ").");
                continue;
            }

            foreach (var type in TypesOrNote(assembly, manifest.Id, fileName))
            {
                if (!IsModuleType(type) || !TypeMatches(type, manifest.Server!))
                {
                    continue;
                }

                if (TryAdd(type, manifest.Id, registerServices: true))
                {
                    return true;
                }
            }
        }

        Note(manifest.Id + " server module " + manifest.Server + " was not found.");
        return false;
    }

    private bool TryAdd(Type type, string? expectedId, bool registerServices)
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
            if (registerServices)
            {
                _register.Add(module.Id);
            }
        }

        return true;
    }

    private static IEnumerable<string> CandidateDlls(string pluginDir)
    {
        var list = new List<string>();
        Collect(Path.Combine(pluginDir, "server"));
        Collect(pluginDir);
        return list;

        void Collect(string dir)
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch
            {
                return;
            }

            var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var modules = new List<string>();
            var leftovers = new List<string>();
            foreach (var path in files)
            {
                var name = Path.GetFileName(path);
                if (!name.EndsWith(MoonbasePlugins.ModuleFileSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var logical = ModuleAssemblyStore.LogicalAssemblyFileName(name);
                if (IsHostAssembly(logical))
                {
                    continue;
                }

                modules.Add(path);
                hidden.Add(logical);
            }

            foreach (var path in files)
            {
                var name = Path.GetFileName(path);
                if (!ModuleAssemblyStore.IsBareDll(name) || hidden.Contains(name) || IsHostAssembly(name))
                {
                    continue;
                }

                leftovers.Add(path);
            }

            list.AddRange(modules);
            list.AddRange(leftovers);
        }
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

    private IEnumerable<Type> TypesOrNote(Assembly assembly, string id, string fileName)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            Note(id + " could not load types from " + fileName + " (" + Describe(ex) + ").");
            return ex.Types.Where(type => type != null)!;
        }
        catch (Exception ex)
        {
            Note(id + " could not load types from " + fileName + " (" + Describe(ex) + ").");
            return Array.Empty<Type>();
        }
    }

    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Walk(ex);
        return parts.Count == 0 ? ex.GetType().Name : string.Join("; ", parts);

        void Walk(Exception? item)
        {
            if (item == null)
            {
                return;
            }

            if (item is ReflectionTypeLoadException reflection)
            {
                var any = false;
                if (reflection.LoaderExceptions != null)
                {
                    foreach (var inner in reflection.LoaderExceptions)
                    {
                        if (inner == null)
                        {
                            continue;
                        }

                        any = true;
                        Walk(inner);
                    }
                }

                if (!any)
                {
                    Push(reflection);
                }

                return;
            }

            Push(item);
            if (item.InnerException != null)
            {
                Walk(item.InnerException);
            }
        }

        void Push(Exception item)
        {
            var message = item.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (message.Length > 500)
            {
                message = message[..500];
            }

            var text = item.GetType().Name + ": " + message;
            if (seen.Add(text))
            {
                parts.Add(text);
            }
        }
    }

    private static Assembly? FindLoaded(AssemblyLoadContext context, string simpleName)
    {
        foreach (var assembly in context.Assemblies)
        {
            if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.Ordinal))
            {
                return assembly;
            }
        }

        return null;
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
        // Each loaded copy of this assembly has its own statics. The guard lives on the
        // AppDomain so a second Moonfin.Server (tests, or a nested host) cannot register
        // a hook that hands out the wrong Moonfin.Server.
        const string key = "Moonfin.ModuleResolveHook";
        if (AppDomain.CurrentDomain.GetData(key) != null)
        {
            return;
        }

        AppDomain.CurrentDomain.SetData(key, "1");
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var requested = new AssemblyName(args.Name).Name;
            if (!string.Equals(requested, "Moonfin.Server", StringComparison.Ordinal))
            {
                return null;
            }

            var requesting = args.RequestingAssembly;
            if (requesting != null)
            {
                var context = AssemblyLoadContext.GetLoadContext(requesting);
                if (context != null && !ReferenceEquals(context, AssemblyLoadContext.Default))
                {
                    return null;
                }
            }

            return FindLoaded(AssemblyLoadContext.Default, "Moonfin.Server");
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

    /// <summary>
    /// One context per module. Load and the Resolving handler both return this process's
    /// Moonfin.Server — the copy that defined <see cref="IMoonbaseModule"/> — so the cast
    /// in TryAdd is the same type Jellyfin loaded. Load has to answer: a null result lets
    /// the default context supply its own Moonfin.Server before Resolving runs.
    /// </summary>
    private sealed class PluginModuleLoadContext : AssemblyLoadContext
    {
        private static int _sequence;

        private readonly string _serverDir;
        private string? _mainAssembly;

        public PluginModuleLoadContext(string id, string serverDir)
            : base("moonfin-plugin-" + id + "-" + Interlocked.Increment(ref _sequence), isCollectible: false)
        {
            _serverDir = serverDir;
            Resolving += (_, name) => ResolveDependency(name);
        }

        public Assembly LoadModule(string path)
        {
            _mainAssembly ??= Path.GetFileNameWithoutExtension(ModuleAssemblyStore.LogicalAssemblyFileName(Path.GetFileName(path)));
            try
            {
                return LoadFromAssemblyPath(path);
            }
            catch (Exception ex) when (UseStream(path, ex))
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return LoadFromStream(stream);
            }
        }

        protected override Assembly? Load(AssemblyName assemblyName) => ResolveDependency(assemblyName);

        private Assembly? ResolveDependency(AssemblyName assemblyName)
        {
            var simple = assemblyName.Name;
            if (string.IsNullOrEmpty(simple) || string.Equals(simple, _mainAssembly, StringComparison.Ordinal))
            {
                return null;
            }

            // Same Type object as this catalog. Never the default context's Moonfin.Server
            // when Jellyfin (or a test host) loaded this one somewhere else.
            if (string.Equals(simple, "Moonfin.Server", StringComparison.Ordinal))
            {
                return typeof(IMoonbaseModule).Assembly;
            }

            var host = AssemblyLoadContext.GetLoadContext(typeof(IMoonbaseModule).Assembly) ?? Default;
            var found = FindLoaded(host, simple);
            if (found != null)
            {
                return found;
            }

            if (!ReferenceEquals(host, Default))
            {
                found = FindLoaded(Default, simple);
                if (found != null && !string.Equals(found.GetName().Name, "Moonfin.Server", StringComparison.Ordinal))
                {
                    return found;
                }
            }

            if (IsHostAssemblyName(simple))
            {
                return null;
            }

            var path = Probe(_serverDir, simple);
            return path == null ? null : LoadModule(path);
        }

        private static bool UseStream(string path, Exception ex) =>
            path.EndsWith(MoonbasePlugins.ModuleFileSuffix, StringComparison.OrdinalIgnoreCase)
            && ex is ArgumentException or NotSupportedException or BadImageFormatException or FileLoadException;

        private static string? Probe(string directory, string simpleName)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            var mbmod = Path.Combine(directory, simpleName + MoonbasePlugins.ModuleFileSuffix);
            if (File.Exists(mbmod))
            {
                return mbmod;
            }

            var dll = Path.Combine(directory, simpleName + ".dll");
            if (File.Exists(dll))
            {
                return dll;
            }

            return null;
        }

        private static bool IsHostAssemblyName(string simpleName) =>
            simpleName.Equals("Moonfin.Server", StringComparison.Ordinal)
            || simpleName.Equals("SharpCompress", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("MediaBrowser.", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            || simpleName.Equals("Newtonsoft.Json", StringComparison.OrdinalIgnoreCase);
    }
}
