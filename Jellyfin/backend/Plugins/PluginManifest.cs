using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Moonfin.Server.Plugins;

/// <summary>
/// plugin.json. Same shape the Moonfin client host uses, plus the server half and the
/// optional web entry the WebView loads.
/// </summary>
public sealed class PluginManifest
{
    public const int MaxIdLength = 64;

    private static readonly Regex IdPattern = new(
        "^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TokenPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> ServedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html",
        ".htm",
        ".js",
        ".mjs",
        ".css",
        ".json",
        ".map",
        ".svg",
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".webp",
        ".ico",
        ".txt",
        ".woff",
        ".woff2",
        ".ttf",
    };

    public static readonly IReadOnlySet<string> KnownSlots = new HashSet<string>(StringComparer.Ordinal)
    {
        "homeRow",
        "playback",
        "settings",
        "events",
    };

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("provides")]
    public List<string> Provides { get; set; } = new();

    [JsonPropertyName("requires")]
    public List<string> Requires { get; set; } = new();

    [JsonPropertyName("slots")]
    public List<string> Slots { get; set; } = new();

    [JsonPropertyName("client")]
    public PluginClientEntry? Client { get; set; }

    /// <summary>Type name of the <see cref="IMoonbaseModule"/> in the plugin's server dll. Omitted for a client-only plugin.</summary>
    [JsonPropertyName("server")]
    public string? Server { get; set; }

    [JsonPropertyName("minHostApi")]
    public int MinHostApi { get; set; } = MoonbasePlugins.HostApi;

    /// <summary><c>admin</c> (default) or <c>user</c>. Who may call this plugin's proxy.</summary>
    [JsonPropertyName("proxyAccess")]
    public string ProxyAccess { get; set; } = "admin";

    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && IdPattern.IsMatch(id);

    public static bool IsValidToken(string? token) =>
        !string.IsNullOrEmpty(token) && TokenPattern.IsMatch(token);

    /// <summary>Reads and normalizes a manifest. Returns null when the document is not a usable plugin.</summary>
    public static PluginManifest? TryParse(string json, out string? error)
    {
        error = null;
        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(json, PluginJson.Manifest);
        }
        catch (JsonException)
        {
            error = "plugin.json is not valid JSON.";
            return null;
        }

        if (manifest == null)
        {
            error = "plugin.json is empty.";
            return null;
        }

        manifest.Id = (manifest.Id ?? string.Empty).Trim().ToLowerInvariant();
        if (!IsValidId(manifest.Id))
        {
            error = "plugin.json id must be lowercase letters, numbers, and hyphens.";
            return null;
        }

        manifest.Name = TrimField(manifest.Name, 80);
        if (manifest.Name.Length == 0)
        {
            manifest.Name = manifest.Id;
        }

        manifest.Version = TrimField(manifest.Version, 32);
        if (manifest.Version.Length == 0)
        {
            error = "plugin.json version is required.";
            return null;
        }

        manifest.Description = TrimField(manifest.Description, 500);
        manifest.Server = string.IsNullOrWhiteSpace(manifest.Server) ? null : TrimField(manifest.Server, 200);
        if (manifest.MinHostApi <= 0)
        {
            error = "minHostApi must be 1 or greater.";
            return null;
        }

        var access = string.IsNullOrWhiteSpace(manifest.ProxyAccess)
            ? "admin"
            : manifest.ProxyAccess.Trim().ToLowerInvariant();
        if (access != "admin" && access != "user")
        {
            error = "proxyAccess must be \"admin\" or \"user\".";
            return null;
        }

        manifest.ProxyAccess = access;

        manifest.Provides = CleanTokens(manifest.Provides);
        manifest.Requires = CleanTokens(manifest.Requires);
        manifest.Slots = (manifest.Slots ?? new List<string>())
            .Where(slot => KnownSlots.Contains(slot))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (manifest.Client != null)
        {
            manifest.Client.Html = CleanRelative(manifest.Client.Html);
            manifest.Client.Js = CleanRelative(manifest.Client.Js);
            if (manifest.Client.Html == null && manifest.Client.Js == null)
            {
                manifest.Client = null;
            }
        }

        return manifest;
    }

    public string? ClientEntryUrl()
    {
        var relative = Client?.Html ?? Client?.Js;
        if (string.IsNullOrEmpty(relative))
        {
            return null;
        }

        return "/Moonfin/Plugins/" + Id + "/files/" + relative;
    }

    internal static bool IsAllowedClientFolder(string folder)
    {
        if (folder.Length == 0
            || folder.Contains("..", StringComparison.Ordinal)
            || folder.Contains(':', StringComparison.Ordinal)
            || folder.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        var first = folder.Split('/')[0];
        return first.Length > 0
            && first[0] != '.'
            && first[0] != '_'
            && !first.Equals("server", StringComparison.OrdinalIgnoreCase)
            && !first.Equals("data", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsBlockedClientFileName(string name) =>
        name.Equals("config.json", StringComparison.OrdinalIgnoreCase)
        || name.Equals("state.json", StringComparison.OrdinalIgnoreCase)
        || name.Equals("plugin.json", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(MoonbasePlugins.ModuleFileSuffix, StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".so", StringComparison.OrdinalIgnoreCase);

    private static List<string> CleanTokens(List<string>? tokens) =>
        (tokens ?? new List<string>())
            .Where(IsValidToken)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Keeps a client path only when the file route can serve it. <c>./</c> and empty
    /// segments are folded so the published entry URL is the path that will be requested.
    /// A root-level name stays a single file; it does not become the plugin root.
    /// </summary>
    private static string? CleanRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var relative = path.Replace('\\', '/').Trim();
        if (relative.Length == 0
            || relative.Contains('\0', StringComparison.Ordinal)
            || relative.Contains("..", StringComparison.Ordinal)
            || relative.Contains(':', StringComparison.Ordinal)
            || relative.Contains('%', StringComparison.Ordinal))
        {
            return null;
        }

        var segments = new List<string>();
        foreach (var segment in relative.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment[0] == '.' || segment[^1] == '.' || segment[^1] == ' ')
            {
                return null;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0 || !IsServedClientFile(segments[^1]))
        {
            return null;
        }

        if (segments.Count > 1 && !IsAllowedClientFolder(string.Join('/', segments.GetRange(0, segments.Count - 1))))
        {
            return null;
        }

        return string.Join('/', segments);
    }

    private static bool IsServedClientFile(string name) =>
        !IsBlockedClientFileName(name) && ServedExtensions.Contains(Path.GetExtension(name));

    private static string TrimField(string? value, int max)
    {
        var trimmed = (value ?? string.Empty).Trim().Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

public sealed class PluginClientEntry
{
    [JsonPropertyName("html")]
    public string? Html { get; set; }

    [JsonPropertyName("js")]
    public string? Js { get; set; }
}

public static class MoonbasePlugins
{
    public const int HostApi = 1;

    public const string FolderName = "moonfin-plugins";

    public const string StateFileName = "_state.json";

    /// <summary>On-disk name suffix for a server module. Jellyfin's plugin scan does not load it.</summary>
    public const string ModuleFileSuffix = ".dll.mbmod";
}
