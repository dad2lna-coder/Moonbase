using System.Text.Json;
using Moonfin.Server.Services;

namespace Moonfin.Server.Plugins;

/// <summary>
/// Installed plugins live under the Moonbase data folder so a plugin update never deletes them.
/// </summary>
public sealed class MoonfinPluginRegistry
{
    private readonly MoonbaseModuleCatalog _catalog;
    private readonly object _gate = new();

    public MoonfinPluginRegistry(MoonbaseModuleCatalog catalog)
    {
        _catalog = catalog;
    }

    public string Root
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_catalog.PluginsRoot))
            {
                return _catalog.PluginsRoot;
            }

            return Path.Combine(MoonfinPlugin.ResolveDataFolderPath(), MoonbasePlugins.FolderName);
        }
    }

    public IReadOnlyList<PluginCapabilityResolver.PluginStatus> List()
    {
        var root = Root;
        _catalog.EnsureExternalLoaded(root);
        var manifests = ReadManifests(root);
        var disabled = ReadDisabled(root);
        var host = PluginCapabilityResolver.HostCapabilities(MoonfinPlugin.Instance?.Configuration);
        return PluginCapabilityResolver.Resolve(
            manifests,
            _catalog.ServerProvides(),
            _catalog.FailedIds(),
            disabled,
            host,
            MoonbasePlugins.HostApi);
    }

    public Dictionary<string, bool> PingFlags() => PluginCapabilityResolver.ToPingFlags(List());

    public bool IsActive(string id) =>
        List().Any(status => string.Equals(status.Manifest.Id, id, StringComparison.OrdinalIgnoreCase) && status.Active);

    public PluginCapabilityResolver.PluginStatus? Find(string id) =>
        List().FirstOrDefault(status => string.Equals(status.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));

    public PluginCapabilityResolver.PluginStatus? SetEnabled(string id, bool enabled, IServiceProvider services)
    {
        if (!PluginManifest.IsValidId(id) || Find(id) == null)
        {
            return null;
        }

        var wasActive = IsActive(id);
        lock (_gate)
        {
            var disabled = ReadDisabled(Root);
            if (enabled)
            {
                disabled.Remove(id);
            }
            else
            {
                disabled.Add(id);
            }

            WriteDisabled(Root, disabled);
        }

        var status = Find(id);
        if (wasActive && status is not { Active: true })
        {
            _catalog.Stop(id);
        }
        else if (!wasActive && status is { Active: true })
        {
            _catalog.StartWhere(services, candidate => string.Equals(candidate, id, StringComparison.OrdinalIgnoreCase));
        }

        return status;
    }

    public bool TryDelete(string id, out string? error)
    {
        error = null;
        if (!PluginManifest.IsValidId(id))
        {
            error = "Unknown plugin.";
            return false;
        }

        var dir = Path.Combine(Root, id);
        var full = Path.GetFullPath(dir);
        var root = Path.GetFullPath(Root);
        var rootSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootSep, StringComparison.Ordinal) || !Directory.Exists(full))
        {
            error = "Unknown plugin.";
            return false;
        }

        _catalog.Stop(id);
        try
        {
            Directory.Delete(full, recursive: true);
        }
        catch (Exception)
        {
            var trash = Path.Combine(root, ".trash");
            Directory.CreateDirectory(trash);
            try
            {
                Directory.Move(full, Path.Combine(trash, id + "-" + Guid.NewGuid().ToString("N")));
            }
            catch (Exception)
            {
                error = "The plugin is still loaded. Restart Jellyfin and delete it again.";
                return false;
            }
        }

        lock (_gate)
        {
            var disabled = ReadDisabled(root);
            if (disabled.Remove(id))
            {
                WriteDisabled(root, disabled);
            }
        }

        return true;
    }

    public string? ReadConfig(string id)
    {
        var path = ConfigPath(id);
        return path != null && File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void WriteConfig(string id, string json)
    {
        var path = ConfigPath(id) ?? throw new InvalidOperationException("Unknown plugin.");
        PluginConfigStore.Write(path, json);
    }

    public string DataPath(string id, Guid userId, string key)
    {
        if (!PluginManifest.IsValidId(id) || !PluginManifest.IsValidToken(key))
        {
            throw new InvalidOperationException("Invalid plugin data key.");
        }

        return Path.Combine(Root, id, "data", userId.ToString("N"), key + ".json");
    }

    private string? ConfigPath(string id)
    {
        if (!PluginManifest.IsValidId(id))
        {
            return null;
        }

        var dir = Path.Combine(Root, id);
        return Directory.Exists(dir) ? Path.Combine(dir, "config.json") : null;
    }

    private static List<PluginManifest> ReadManifests(string root)
    {
        var list = new List<PluginManifest>();
        if (!Directory.Exists(root))
        {
            return list;
        }

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var folder = Path.GetFileName(dir);
            if (folder.StartsWith(".", StringComparison.Ordinal) || folder.StartsWith("_", StringComparison.Ordinal))
            {
                continue;
            }

            var path = Path.Combine(dir, "plugin.json");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var manifest = PluginManifest.TryParse(File.ReadAllText(path), out _);
                if (manifest == null || !string.Equals(folder, manifest.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                list.Add(manifest);
            }
            catch
            {
                // One bad folder does not hide the rest.
            }
        }

        return list;
    }

    private static HashSet<string> ReadDisabled(string root)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(root, MoonbasePlugins.StateFileName);
        if (!File.Exists(path))
        {
            return set;
        }

        try
        {
            var state = JsonSerializer.Deserialize<PluginStateFile>(File.ReadAllText(path), PluginJson.Manifest);
            if (state?.Disabled == null)
            {
                return set;
            }

            foreach (var id in state.Disabled)
            {
                if (PluginManifest.IsValidId(id))
                {
                    set.Add(id);
                }
            }
        }
        catch
        {
            // A corrupt state file means nothing is disabled, which is the default.
        }

        return set;
    }

    private static void WriteDisabled(string root, IEnumerable<string> disabled)
    {
        Directory.CreateDirectory(root);
        var state = new PluginStateFile
        {
            Disabled = disabled.OrderBy(id => id, StringComparer.Ordinal).ToList(),
        };
        AtomicFile.WriteAllText(
            Path.Combine(root, MoonbasePlugins.StateFileName),
            JsonSerializer.Serialize(state, PluginJson.CamelCase));
    }

    private sealed class PluginStateFile
    {
        public List<string> Disabled { get; set; } = new();
    }
}
