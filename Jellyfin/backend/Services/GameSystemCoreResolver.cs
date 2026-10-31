using System.IO.Compression;
using System.Text;
using Moonfin.Romm;

namespace Moonfin.Server.Services;

/// <summary>
/// Centralizes the static policy used to identify playable files and select EmulatorJS cores.
/// Playback selection remains strict and single-result; artwork callers may use the separate
/// multi-candidate suggestion method.
/// </summary>
internal static class GameSystemCoreResolver
{
    internal const string DefaultCore = "nes";

    private static readonly IReadOnlyDictionary<string, string> ExtensionToCore =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".nes"] = "nes",
            [".fds"] = "nes",
            [".sfc"] = "snes",
            [".smc"] = "snes",
            [".gb"] = "gb",
            [".gbc"] = "gb",
            [".gba"] = "gba",
            [".md"] = "segaMD",
            [".gen"] = "segaMD",
            [".smd"] = "segaMD",
            [".sms"] = "segaMS",
            [".gg"] = "segaGG",
            [".sg"] = "segaMS",
            [".n64"] = "n64",
            [".z64"] = "n64",
            [".v64"] = "n64",
            [".nds"] = "nds",
            [".vb"] = "vb",
            [".a26"] = "atari2600",
            [".a78"] = "atari7800",
            [".lnx"] = "lynx",
            [".ws"] = "ws",
            [".wsc"] = "ws",
            [".ngp"] = "ngp",
            // OPEN QUESTION (raised during MAME branch review, 2026-08-04 - not yet answered by
            // the repo owner): ".ngc" is mapped to Neo Geo Pocket (ngp) here, but ".ngc" is more
            // commonly a Nintendo GameCube disc image. This mapping is byte-for-byte pre-existing
            // - it predates this review and was NOT changed as part of it. Do not "fix" this
            // unilaterally; it may be intentional (e.g. disambiguating some Neo Geo Pocket rip
            // convention this library actually sees) or it may be a genuine bug that silently
            // misroutes GameCube images to the wrong core. Confirm intent with the repo owner
            // before touching this line.
            [".ngc"] = "ngp",
            [".pce"] = "pce",
            [".chd"] = "psx",
            [".pbp"] = "psx",
            [".cso"] = "psp",
            [".iso"] = "psp",
            // GD-ROM index and CDI images. A folder named for another system still wins.
            [".gdi"] = "dreamcast",
            [".cdi"] = "dreamcast",
            // Shared by several disc systems. The folder name wins; this is only the fallback.
            [".cue"] = "psx",
            // EmulatorJS 4.2.3 data/cores/cores.json names the installed Fuse core "fuse".
            [".tzx"] = "fuse",
            [".tap"] = "fuse",
            [".z80"] = "fuse",
            [".rzx"] = "fuse",
            [".scl"] = "fuse",
            [".trd"] = "fuse",
        };

    private static readonly IReadOnlyDictionary<string, string> SystemNameToCore =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nes"] = "nes",
            ["famicom"] = "nes",
            ["snes"] = "snes",
            ["superfamicom"] = "snes",
            ["supernintendo"] = "snes",
            ["gb"] = "gb",
            ["gameboy"] = "gb",
            ["gbc"] = "gb",
            ["gameboycolor"] = "gb",
            ["gba"] = "gba",
            ["gameboyadvance"] = "gba",
            ["genesis"] = "segaMD",
            ["megadrive"] = "segaMD",
            ["segagenesis"] = "segaMD",
            ["mastersystem"] = "segaMS",
            ["sms"] = "segaMS",
            ["gamegear"] = "segaGG",
            ["gg"] = "segaGG",
            ["n64"] = "n64",
            ["nintendo64"] = "n64",
            ["nds"] = "nds",
            ["nintendods"] = "nds",
            ["virtualboy"] = "vb",
            ["atari2600"] = "atari2600",
            ["atari7800"] = "atari7800",
            ["lynx"] = "lynx",
            ["wonderswan"] = "ws",
            ["neogeopocket"] = "ngp",
            ["pcengine"] = "pce",
            ["turbografx16"] = "pce",
            ["psx"] = "psx",
            ["ps1"] = "psx",
            ["psone"] = "psx",
            ["playstation"] = "psx",
            ["psp"] = "psp",
            ["playstationportable"] = "psp",
            ["dreamcast"] = "dreamcast",
            ["dc"] = "dreamcast",
            ["segadreamcast"] = "dreamcast",
            ["zxspectrum"] = "fuse",
            ["mame"] = "mame",
            ["arcade"] = "arcade",
        };

    private static readonly HashSet<string> BiosExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".bin", ".bios", ".rom", ".img", ".sys", ".bs" };

    private static readonly HashSet<string> ArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z" };

    private static readonly HashSet<string> DreamcastExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".chd", ".cdi", ".gdi", ".cue", ".zip", ".7z" };

    internal const string DreamcastCore = "dreamcast";

    internal static bool IsRomFile(string path) =>
        ExtensionToCore.ContainsKey(Path.GetExtension(path)) || IsArchive(path);

    internal static bool IsArchive(string path) => ArchiveExtensions.Contains(Path.GetExtension(path));

    internal static bool IsBiosFile(string path) => BiosExtensions.Contains(Path.GetExtension(path));

    internal static bool IsKnownRomExtension(string path) => ExtensionToCore.ContainsKey(Path.GetExtension(path));

    internal static string ResolveSystemCore(string systemName, IReadOnlyList<string> games, out bool wasDefaulted)
    {
        wasDefaulted = false;
        var normalized = NormalizeSystemName(systemName);
        if (SystemNameToCore.TryGetValue(normalized, out var core))
        {
            return core;
        }

        if (TryFuzzyMatchSystemCore(normalized, out var fuzzyCore))
        {
            return fuzzyCore;
        }

        foreach (var rom in games)
        {
            if (ExtensionToCore.TryGetValue(Path.GetExtension(rom), out var byExtension))
            {
                return byExtension;
            }
        }

        wasDefaulted = true;
        return DefaultCore;
    }

    internal static IReadOnlyList<string> FuzzySuggestCores(string systemName, int maxSuggestions)
    {
        if (maxSuggestions <= 0)
        {
            return Array.Empty<string>();
        }

        var normalized = NormalizeSystemName(systemName);
        if (normalized.Length < 3)
        {
            return Array.Empty<string>();
        }

        var scored = new List<(string Core, int Diff)>();
        foreach (var kv in SystemNameToCore)
        {
            if (IsFuzzyMatch(normalized, kv.Key))
            {
                scored.Add((kv.Value, Math.Abs(kv.Key.Length - normalized.Length)));
            }
        }

        return scored.OrderBy(s => s.Diff).Select(s => s.Core)
            .Distinct(StringComparer.Ordinal).Take(maxSuggestions).ToList();
    }

    internal static bool IsArcadeFamilyCore(string core) =>
        string.Equals(core, "mame", StringComparison.Ordinal) ||
        string.Equals(core, "arcade", StringComparison.Ordinal);

    internal static bool IsDreamcastCore(string? core) =>
        string.Equals(core, DreamcastCore, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// False for cores EmulatorJS does not ship. Dreamcast (Flycast) is one of those, so a web
    /// client can say the system is unsupported instead of loading a core that does not exist.
    /// </summary>
    internal static bool SupportsEmulatorJs(string? core) => !IsDreamcastCore(core);

    internal static bool IsDiscIndex(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".gdi", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cue", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldPreserveArchiveForCore(string core, string romPath) =>
        string.Equals(Path.GetExtension(romPath), ".zip", StringComparison.OrdinalIgnoreCase) &&
        (IsArcadeFamilyCore(core) || (IsDreamcastCore(core) && ZipHasDiscIndexAndTracks(romPath)));

    internal static bool IsPlayableFileForCore(string core, string romPath)
    {
        if (IsDreamcastCore(core))
        {
            return DreamcastExtensions.Contains(Path.GetExtension(romPath));
        }

        return IsRomFile(romPath) && (!IsArcadeFamilyCore(core) ||
            string.Equals(Path.GetExtension(romPath), ".zip", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The same file rules, handed to the RomM reader so both library kinds agree.</summary>
    internal static readonly RommFileRules RommRules = new(
        IsPlayableFileForCore,
        IsArchive,
        IsKnownRomExtension,
        ShouldPreserveArchiveForCore);

    /// <summary>
    /// File names a .gdi or .cue in this directory names as tracks. A .gdi is handled the same way
    /// as a .cue: the index is the game, and the tracks it names are not games of their own.
    /// </summary>
    internal static HashSet<string> DiscTrackNames(IEnumerable<string> files)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (!IsDiscIndex(file))
            {
                continue;
            }

            foreach (var name in ReadDiscTrackNames(file))
            {
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    internal static IEnumerable<string> ReadDiscTrackNames(string indexPath)
    {
        try
        {
            var info = new FileInfo(indexPath);
            if (!info.Exists || info.Length <= 0 || info.Length > 1024 * 1024)
            {
                return Array.Empty<string>();
            }

            var text = File.ReadAllText(indexPath);
            return Path.GetExtension(indexPath).Equals(".gdi", StringComparison.OrdinalIgnoreCase)
                ? GdiTrackNames(text)
                : CueTrackNames(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    internal static IReadOnlyList<string> CueTrackNames(string text)
    {
        var names = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length < 5 || !line.StartsWith("FILE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!char.IsWhiteSpace(line[4]))
            {
                continue;
            }

            var rest = line[4..].TrimStart();
            string name;
            if (rest.StartsWith('"'))
            {
                var end = rest.IndexOf('"', 1);
                if (end <= 1)
                {
                    continue;
                }

                name = rest[1..end];
            }
            else
            {
                var space = rest.IndexOf(' ');
                name = space < 0 ? rest : rest[..space];
            }

            var fileName = Path.GetFileName(name.Replace('\\', '/'));
            if (!string.IsNullOrEmpty(fileName))
            {
                names.Add(fileName);
            }
        }

        return names;
    }

    internal static IReadOnlyList<string> GdiTrackNames(string text)
    {
        var names = new List<string>();
        var skippedCount = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (!skippedCount && line.IndexOf(' ') < 0 && int.TryParse(line, out _))
            {
                skippedCount = true;
                continue;
            }

            skippedCount = true;
            var token = NthToken(line, 4);
            if (string.IsNullOrEmpty(token))
            {
                continue;
            }

            var fileName = Path.GetFileName(token.Replace('\\', '/'));
            if (!string.IsNullOrEmpty(fileName))
            {
                names.Add(fileName);
            }
        }

        return names;
    }

    private static bool ZipHasDiscIndexAndTracks(string romPath)
    {
        try
        {
            if (!File.Exists(romPath))
            {
                return false;
            }

            using var zip = ZipFile.OpenRead(romPath);
            var files = 0;
            var index = false;
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                files++;
                if (IsDiscIndex(entry.Name))
                {
                    index = true;
                }
            }

            return index && files > 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static string? NthToken(string line, int index)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < line.Length && tokens.Count <= index)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i]))
            {
                i++;
            }

            if (i >= line.Length)
            {
                break;
            }

            if (line[i] == '"')
            {
                var end = line.IndexOf('"', i + 1);
                if (end < 0)
                {
                    tokens.Add(line[(i + 1)..]);
                    break;
                }

                tokens.Add(line[(i + 1)..end]);
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i]))
                {
                    i++;
                }

                tokens.Add(line[start..i]);
            }
        }

        return tokens.Count > index ? tokens[index] : null;
    }

    private static bool TryFuzzyMatchSystemCore(string normalizedName, out string core)
    {
        core = string.Empty;
        if (normalizedName.Length < 3)
        {
            return false;
        }

        string? bestCore = null;
        var minDiff = int.MaxValue;
        var ambiguous = false;
        foreach (var kv in SystemNameToCore)
        {
            if (!IsFuzzyMatch(normalizedName, kv.Key))
            {
                continue;
            }

            var diff = Math.Abs(kv.Key.Length - normalizedName.Length);
            if (bestCore == null || diff < minDiff)
            {
                minDiff = diff;
                bestCore = kv.Value;
                ambiguous = false;
            }
            else if (diff == minDiff && !string.Equals(kv.Value, bestCore, StringComparison.Ordinal))
            {
                ambiguous = true;
            }
        }

        if (bestCore == null || ambiguous)
        {
            return false;
        }

        core = bestCore;
        return true;
    }

    private static bool IsFuzzyMatch(string normalizedName, string alias) =>
        alias.Length >= 3 && (normalizedName.Contains(alias, StringComparison.Ordinal) ||
            alias.Contains(normalizedName, StringComparison.Ordinal));

    private static string NormalizeSystemName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }
}
