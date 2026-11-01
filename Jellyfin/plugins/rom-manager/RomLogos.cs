using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Moonfin.Plugins.RomManager;

/// <summary>
/// Console and developer logos from <c>{romRoot}/.logos/{consoles|developers}/</c>. The
/// folder is a dot folder, so ROM indexing and Jellyfin scans skip it like <c>.bios</c>.
/// A request name is never joined into a path: it is folded and matched against the
/// files that already exist in the folder, so "Master System", "mastersystem" and
/// "Master_System.png" all find <c>Master System.svg</c>, and "../x" finds nothing.
/// PNG wins over SVG for the same name so a dropped-in PNG overrides a pack SVG.
/// Logos go back as data: URIs for an &lt;img&gt; tag; an SVG shown that way cannot run script.
/// </summary>
public static class RomLogos
{
    public const string FolderName = ".logos";

    public const int MaxBytes = 512 * 1024;

    public const int MaxNames = 500;

    public static readonly IReadOnlyList<string> Kinds = new[] { "consoles", "developers" };

    private static readonly string[] Extensions = { ".png", ".svg", ".webp" };

    private static readonly Regex SafeName = new(@"^[\p{L}\p{N}][\p{L}\p{N} _.&'+()!-]{0,79}$", RegexOptions.CultureInvariant);

    private static readonly ConcurrentDictionary<string, CachedLogo> Cache = new(StringComparer.Ordinal);

    private sealed record CachedLogo(long Length, DateTime Written, string DataUri);

    public static bool IsKind(string? kind) => kind != null && Kinds.Contains(kind, StringComparer.Ordinal);

    /// <summary>True when a requested logo name is acceptable. Rejects separators, dot-dot, and control characters.</summary>
    public static bool IsSafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
        {
            return false;
        }

        if (name.Contains("..", StringComparison.Ordinal) || name.Contains('/') || name.Contains('\\') || name.Contains(':') || name.Contains('\0'))
        {
            return false;
        }

        return SafeName.IsMatch(name);
    }

    /// <summary>Data URIs for the names that have a logo. Unknown, unsafe, oversized or non-image files are left out.</summary>
    public static Dictionary<string, string> Load(string romRoot, string kind, IEnumerable<string> names)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!IsKind(kind))
        {
            return found;
        }

        var folder = Path.GetFullPath(Path.Combine(romRoot, FolderName, kind));
        var files = Index(folder);
        if (files.Count == 0)
        {
            return found;
        }

        foreach (var name in names.Take(MaxNames))
        {
            if (!IsSafeName(name) || found.ContainsKey(name))
            {
                continue;
            }

            if (!files.TryGetValue(RomDevelopers.Fold(name), out var file))
            {
                continue;
            }

            var uri = Read(folder, file);
            if (uri != null)
            {
                found[name] = uri;
            }
        }

        return found;
    }

    // Fold key -> file path, PNG before SVG before WebP. Symlinks and anything outside the folder are skipped.
    private static Dictionary<string, string> Index(string folder)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(folder))
        {
            return map;
        }

        foreach (var ext in Extensions)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*" + ext, SearchOption.TopDirectoryOnly).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!file.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var key = RomDevelopers.Fold(Path.GetFileNameWithoutExtension(file));
                if (key.Length > 0)
                {
                    map.TryAdd(key, file);
                }
            }
        }

        return map;
    }

    private static string? Read(string folder, string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.LinkTarget != null || info.Length <= 0 || info.Length > MaxBytes)
            {
                return null;
            }

            var full = Path.GetFullPath(info.FullName);
            if (!string.Equals(Path.GetDirectoryName(full), folder, StringComparison.Ordinal))
            {
                return null;
            }

            if (Cache.TryGetValue(full, out var hit) && hit.Length == info.Length && hit.Written == info.LastWriteTimeUtc)
            {
                return hit.DataUri;
            }

            var bytes = File.ReadAllBytes(full);
            var type = Sniff(bytes, Path.GetExtension(full));
            if (type == null)
            {
                return null;
            }

            var uri = "data:" + type + ";base64," + Convert.ToBase64String(bytes);
            if (Cache.Count > 512)
            {
                Cache.Clear();
            }

            Cache[full] = new CachedLogo(info.Length, info.LastWriteTimeUtc, uri);
            return uri;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The image type from the file's own bytes, so a renamed non-image is refused.</summary>
    public static string? Sniff(byte[] bytes, string extension)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
        {
            return "image/png";
        }

        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return "image/webp";
        }

        if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            var head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 2048)).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            if (head.StartsWith('<') && head.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            {
                return "image/svg+xml";
            }
        }

        return null;
    }
}
