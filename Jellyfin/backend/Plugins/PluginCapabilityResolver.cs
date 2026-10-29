namespace Moonfin.Server.Plugins;

/// <summary>
/// Decides which plugins are active. An unmet requirement is dormant, not a failure.
/// A capability flag is on only when the server module that listed it is active.
/// </summary>
public static class PluginCapabilityResolver
{
    public sealed record PluginStatus(
        PluginManifest Manifest,
        bool Enabled,
        bool Active,
        string? DormantReason,
        bool ServerLoaded,
        IReadOnlyList<string> EffectiveProvides);

    public static HashSet<string> HostCapabilities(PluginConfiguration? config)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (config?.EnableSettingsSync == true)
        {
            set.Add("settingsSync");
        }

        if (config?.SeerrEnabled == true)
        {
            set.Add("seerr");
        }

        if (!string.IsNullOrWhiteSpace(config?.MdblistApiKey))
        {
            set.Add("mdblist");
        }

        if (!string.IsNullOrWhiteSpace(config?.TmdbApiKey))
        {
            set.Add("tmdb");
        }

        // These routes ship in Moonbase itself. They are host flags, not plugin flags.
        set.Add("tmdbThumbs");
        set.Add("tmdbDetails");
        set.Add("tmdbReviews");
        set.Add("tmdbPersonLinks");
        set.Add("messages");

        if (config?.RecommendationsProviderEnabled ?? true)
        {
            set.Add("recommendations");
        }

        if (config?.LiveTvDetectIncompleteRecordings ?? true)
        {
            set.Add("incompleteRecordings");
        }

        return set;
    }

    public static IReadOnlyList<PluginStatus> Resolve(
        IReadOnlyList<PluginManifest> manifests,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> serverProvides,
        IReadOnlySet<string> serverFailed,
        IReadOnlySet<string> disabled,
        IReadOnlySet<string> hostCapabilities,
        int hostApi)
    {
        var items = new List<WorkItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in manifests)
        {
            if (manifest == null || !seen.Add(manifest.Id))
            {
                continue;
            }

            serverProvides.TryGetValue(manifest.Id, out var moduleProvides);
            var loaded = moduleProvides != null;
            var failed = serverFailed.Contains(manifest.Id);
            var enabled = !disabled.Contains(manifest.Id);
            var serverDeclared = !string.IsNullOrEmpty(manifest.Server);
            var hostOk = manifest.MinHostApi <= hostApi;
            var granted = new List<string>();
            if (loaded && moduleProvides != null)
            {
                var moduleSet = new HashSet<string>(moduleProvides, StringComparer.Ordinal);
                foreach (var flag in manifest.Provides)
                {
                    if (moduleSet.Contains(flag))
                    {
                        granted.Add(flag);
                    }
                }
            }

            string? blocked = null;
            var canActivate = enabled && hostOk;
            if (!enabled)
            {
                canActivate = false;
            }
            else if (!hostOk)
            {
                blocked = "Requires host API " + manifest.MinHostApi + "; this Moonbase supports " + hostApi + ".";
                canActivate = false;
            }
            else if (serverDeclared && failed)
            {
                blocked = "Server module failed to load.";
                canActivate = false;
            }
            else if (serverDeclared && !loaded)
            {
                blocked = "Restart Jellyfin to load the server module.";
                canActivate = false;
            }

            items.Add(new WorkItem
            {
                Manifest = manifest,
                Enabled = enabled,
                ServerLoaded = loaded,
                CanActivate = canActivate,
                BlockedReason = blocked,
                Granted = granted,
            });
        }

        var available = new HashSet<string>(hostCapabilities, StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool progressed;
        do
        {
            progressed = false;
            foreach (var item in items)
            {
                if (!item.CanActivate || active.Contains(item.Manifest.Id))
                {
                    continue;
                }

                if (item.Manifest.Requires.Any(flag => !available.Contains(flag)))
                {
                    continue;
                }

                active.Add(item.Manifest.Id);
                if (item.ServerLoaded)
                {
                    foreach (var flag in item.Granted)
                    {
                        available.Add(flag);
                    }
                }

                progressed = true;
            }
        }
        while (progressed);

        var statuses = new List<PluginStatus>(items.Count);
        foreach (var item in items)
        {
            var isActive = active.Contains(item.Manifest.Id);
            string? reason = null;
            if (item.Enabled && !isActive)
            {
                reason = item.BlockedReason;
                if (reason == null)
                {
                    var missing = item.Manifest.Requires
                        .Where(flag => !available.Contains(flag))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(flag => flag, StringComparer.Ordinal)
                        .ToList();
                    reason = missing.Count == 0
                        ? "Requirements are not met."
                        : "Missing required capabilities: " + string.Join(", ", missing) + ".";
                }
            }

            statuses.Add(new PluginStatus(
                item.Manifest,
                item.Enabled,
                isActive,
                reason,
                item.ServerLoaded,
                isActive && item.ServerLoaded ? item.Granted : Array.Empty<string>()));
        }

        return statuses;
    }

    public static Dictionary<string, bool> ToPingFlags(IReadOnlyList<PluginStatus> statuses)
    {
        var flags = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var status in statuses)
        {
            foreach (var flag in status.Manifest.Provides)
            {
                flags.TryAdd(flag, false);
            }
        }

        foreach (var status in statuses)
        {
            if (!status.Active || !status.ServerLoaded)
            {
                continue;
            }

            foreach (var flag in status.EffectiveProvides)
            {
                flags[flag] = true;
            }
        }

        return new Dictionary<string, bool>(flags, StringComparer.Ordinal);
    }

    private sealed class WorkItem
    {
        public PluginManifest Manifest { get; set; } = new();

        public bool Enabled { get; set; }

        public bool ServerLoaded { get; set; }

        public bool CanActivate { get; set; }

        public string? BlockedReason { get; set; }

        public List<string> Granted { get; set; } = new();
    }
}
