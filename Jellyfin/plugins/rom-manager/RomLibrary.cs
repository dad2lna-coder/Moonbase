using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Moonfin.Plugins.RomManager;

public sealed class RomEntry
{
    public string Path { get; init; } = string.Empty;

    public string Platform { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string RomFile { get; init; } = string.Empty;

    public long Bytes { get; init; }

    public int? Year { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public int Cover { get; init; }

    public string? Lb { get; init; }

    public string? Genre { get; init; }

    public string? Publisher { get; init; }

    public string? Developer { get; init; }

    public long Mtime { get; init; }

    public long CopyGroup { get; init; }

    public bool KeepCopy { get; init; }

    public long VersionGroup { get; init; }

    public bool BestVersion { get; init; }
}

public sealed class GameRecord
{
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required string Platform { get; init; }

    public int? Year { get; init; }

    public string? Genre { get; init; }

    public string? Publisher { get; init; }

    public string? Developer { get; init; }

    public string CoverPath { get; init; } = string.Empty;

    public string? Lb { get; init; }

    public int Cover { get; init; }

    public long Added { get; init; }

    public long SizeBytes { get; init; }

    public required IReadOnlyList<RomEntry> Members { get; init; }

    public static GameRecord Create(IReadOnlyList<RomEntry> members)
    {
        if (members.Count == 0)
        {
            throw new ArgumentException("A game needs at least one ROM.", nameof(members));
        }

        RomEntry? best = null;
        var smallest = members[0].Path;
        long added = 0;
        long size = 0;
        foreach (var member in members)
        {
            if (best == null && member.BestVersion)
            {
                best = member;
            }

            if (string.CompareOrdinal(member.Path, smallest) < 0)
            {
                smallest = member.Path;
            }

            if (member.Mtime > added)
            {
                added = member.Mtime;
            }

            size += member.Bytes;
        }

        var rep = best ?? members[0];
        var grouped = members[0].VersionGroup != 0;
        return new GameRecord
        {
            Key = (grouped ? "m:" : "p:") + (grouped ? smallest : members[0].Path),
            Title = string.IsNullOrWhiteSpace(rep.Title) ? FallbackTitle(rep) : rep.Title.Trim(),
            Platform = string.IsNullOrWhiteSpace(rep.Platform) ? "Unknown" : rep.Platform.Trim(),
            Year = rep.Year,
            Genre = BlankToNull(rep.Genre),
            Publisher = BlankToNull(rep.Publisher),
            Developer = BlankToNull(rep.Developer),
            CoverPath = rep.Path,
            Lb = BlankToNull(rep.Lb),
            Cover = rep.Cover,
            Added = added,
            SizeBytes = size,
            Members = members,
        };
    }

    public bool Matches(string? query, string? platform)
    {
        if (!string.IsNullOrWhiteSpace(platform))
        {
            var want = platform.Trim();
            var found = false;
            foreach (var member in Members)
            {
                if (string.Equals(member.Platform, want, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Platform, want, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        foreach (var member in Members)
        {
            if (Has(member.Title, query) || Has(member.Genre, query) || Has(member.Platform, query) || Has(member.Publisher, query))
            {
                return true;
            }
        }

        return Has(Title, query) || Has(Genre, query) || Has(Platform, query) || Has(Publisher, query);
    }

    private static bool Has(string? value, string query) =>
        !string.IsNullOrEmpty(value) && value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string FallbackTitle(RomEntry entry)
    {
        var name = string.IsNullOrWhiteSpace(entry.RomFile) ? entry.Path : entry.RomFile;
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class SystemRollup
{
    public required string System { get; init; }

    public int Games { get; set; }

    public int Roms { get; set; }

    public long Bytes { get; set; }
}

public readonly record struct LetterBucket(string Id, string Label);

public readonly record struct LetterBucketCount(string Id, string Label, int Games);

/// <summary>Moonbase folder name beside the index platform name.</summary>
public readonly record struct SystemNameCheck(string? Folder, string? Platform, int Games, bool Match, string Source);

public sealed class DuplicateGroup
{
    public long Id { get; init; }

    public string System { get; init; } = string.Empty;

    public required IReadOnlyList<RomEntry> Members { get; init; }
}

public static class RomPaths
{
    public static bool TryValidate(string? path, out bool trash)
    {
        trash = false;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.Length > 512
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains(':', StringComparison.Ordinal)
            || path.IndexOf('\0') >= 0
            || path.StartsWith('/')
            || path.StartsWith('.')
            || path.Contains("..", StringComparison.Ordinal)
            || path.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = path.Split('/');
        if (parts.Length is not (2 or 3))
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length == 0 || part == "." || part == ".." || part.StartsWith('.'))
            {
                return false;
            }
        }

        trash = parts.Length == 3;
        return true;
    }
}

public static class RomTags
{
    private static readonly HashSet<string> Regions = new(StringComparer.OrdinalIgnoreCase)
    {
        "USA", "Europe", "Japan", "World", "Asia", "Korea", "Australia", "Brazil", "China",
        "Taiwan", "France", "Germany", "Spain", "Italy", "Netherlands", "Sweden", "Canada",
        "United Kingdom", "UK", "Hong Kong", "Scandinavia", "Greece", "Ireland", "Belgium",
        "Austria", "Switzerland", "Mexico", "Argentina", "New Zealand", "Denmark", "Finland",
        "Norway", "Poland", "Portugal", "Russia", "Japan & Korea", "USA & Europe",
    };

    public static string? Region(IReadOnlyList<string>? tags)
    {
        if (tags == null || tags.Count == 0)
        {
            return null;
        }

        var found = new List<string>();
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag) || tag.TrimStart().StartsWith('['))
            {
                continue;
            }

            var inner = Unwrap(tag);
            if (IsRevision(inner))
            {
                continue;
            }

            foreach (var part in inner.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (Regions.Contains(part) && !found.Contains(part, StringComparer.OrdinalIgnoreCase))
                {
                    found.Add(part);
                }
            }
        }

        return found.Count == 0 ? null : string.Join(", ", found);
    }

    public static string? Revision(IReadOnlyList<string>? tags)
    {
        if (tags == null)
        {
            return null;
        }

        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            var inner = Unwrap(tag);
            if (!IsRevision(inner))
            {
                continue;
            }

            if (inner.StartsWith("Revision", StringComparison.OrdinalIgnoreCase))
            {
                var rest = inner["Revision".Length..].Trim().TrimStart('.').Trim();
                return rest.Length == 0 ? "Rev" : "Rev " + rest;
            }

            if (inner.StartsWith("Rev.", StringComparison.OrdinalIgnoreCase))
            {
                var rest = inner[4..].Trim();
                return rest.Length == 0 ? "Rev" : "Rev " + rest;
            }

            if (inner.StartsWith("Rev ", StringComparison.OrdinalIgnoreCase))
            {
                return "Rev " + inner[4..].Trim();
            }

            return "Rev";
        }

        return null;
    }

    public static string FileName(string? romFile, string path)
    {
        var name = string.IsNullOrWhiteSpace(romFile) ? path : romFile;
        name = name.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    public static string ExtensionOf(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1)
        {
            return string.Empty;
        }

        return fileName[(dot + 1)..];
    }

    private static readonly System.Text.RegularExpressions.Regex TrainerTag =
        new(@"^\[t\d*(\+[^\]]*)?\]$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A cheat or trainer ROM: a tag naming "cheat" or "trainer" in any case, or a GoodTools
    /// trainer tag in its lowercase form ([t], [t1], [t+3]). Capital [T+Eng] is a translation.
    /// </summary>
    public static bool IsCheatTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var text = tag.Trim();
        return text.Contains("cheat", StringComparison.OrdinalIgnoreCase)
            || text.Contains("trainer", StringComparison.OrdinalIgnoreCase)
            || TrainerTag.IsMatch(text);
    }

    public static bool IsCheat(IReadOnlyList<string>? tags) => tags != null && tags.Any(IsCheatTag);

    // GoodTools hack tags are lowercase: [h], [h1], [h2C], [h1+2]. Capital [H...] is not matched.
    private static readonly System.Text.RegularExpressions.Regex HackTag =
        new(@"^\[h\d*[^\]]*\]$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A hacked ROM: a tag containing "hack" in any case, or a lowercase GoodTools hack tag
    /// ([h], [h1], [h2C], [h1+2]). [!], [b], [T+Eng] and title words are not hacks.
    /// </summary>
    public static bool IsHackTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var text = tag.Trim();
        return text.Contains("hack", StringComparison.OrdinalIgnoreCase) || HackTag.IsMatch(text);
    }

    public static bool IsHack(IReadOnlyList<string>? tags) => tags != null && tags.Any(IsHackTag);

    private static string Unwrap(string tag)
    {
        var text = tag.Trim();
        if (text.Length >= 2 && text[0] == '(' && text[^1] == ')')
        {
            return text[1..^1].Trim();
        }

        return text;
    }

    private static bool IsRevision(string inner) =>
        inner.StartsWith("Rev ", StringComparison.OrdinalIgnoreCase)
        || inner.StartsWith("Rev.", StringComparison.OrdinalIgnoreCase)
        || inner.Equals("Rev", StringComparison.OrdinalIgnoreCase)
        || inner.StartsWith("Revision", StringComparison.OrdinalIgnoreCase);
}

public static class RomIndex
{
    public static List<RomEntry> Parse(string json, out string? metaJson)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        metaJson = null;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("meta", out var meta)
            && meta.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            metaJson = meta.GetRawText();
        }

        var items = new List<RomEntry>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var path = Str(el, "p");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            items.Add(new RomEntry
            {
                Path = path.Trim(),
                Platform = Str(el, "s") ?? string.Empty,
                Title = Str(el, "t") ?? string.Empty,
                RomFile = Str(el, "f") ?? string.Empty,
                Bytes = Long(el, "z"),
                Year = Year(el, "y"),
                Tags = Tags(el, "tg"),
                Cover = (int)Long(el, "c"),
                Lb = Lb(el),
                Genre = Str(el, "ge"),
                Publisher = Str(el, "pub"),
                Developer = Str(el, "dev"),
                Mtime = Long(el, "m"),
                CopyGroup = Long(el, "sg"),
                KeepCopy = Flag(el, "ks"),
                VersionGroup = Long(el, "mg"),
                BestVersion = Flag(el, "km"),
            });
        }

        return items;
    }

    public static List<GameRecord> Group(IReadOnlyList<RomEntry> items)
    {
        var byVersion = new Dictionary<(string System, long Group), List<RomEntry>>(VersionKeyComparer.Instance);
        foreach (var item in items)
        {
            if (item.VersionGroup == 0)
            {
                continue;
            }

            var key = VersionKey(item);
            if (!byVersion.TryGetValue(key, out var list))
            {
                list = new List<RomEntry>();
                byVersion[key] = list;
            }

            list.Add(item);
        }

        var games = new List<GameRecord>();
        var emitted = new HashSet<(string System, long Group)>(VersionKeyComparer.Instance);
        foreach (var item in items)
        {
            if (item.VersionGroup == 0)
            {
                games.Add(GameRecord.Create(new List<RomEntry> { item }));
                continue;
            }

            var key = VersionKey(item);
            if (!emitted.Add(key))
            {
                continue;
            }

            games.Add(GameRecord.Create(byVersion[key]));
        }

        return games;
    }

    public static int CountVersionGroups(IReadOnlyList<RomEntry> items, string? platform)
    {
        var ids = new HashSet<(string System, long Group)>(VersionKeyComparer.Instance);
        foreach (var item in items)
        {
            if (item.VersionGroup == 0)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(platform)
                && !string.Equals(item.Platform, platform.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ids.Add(VersionKey(item));
        }

        return ids.Count;
    }

    private static (string System, long Group) VersionKey(RomEntry item) => (item.Platform ?? string.Empty, item.VersionGroup);

    private sealed class VersionKeyComparer : IEqualityComparer<(string System, long Group)>
    {
        public static readonly VersionKeyComparer Instance = new();

        public bool Equals((string System, long Group) x, (string System, long Group) y) =>
            x.Group == y.Group && string.Equals(x.System, y.System, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string System, long Group) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.System ?? string.Empty), obj.Group);
    }

    private static string? Str(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static string? Lb(JsonElement el)
    {
        if (!el.TryGetProperty("lb", out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return string.IsNullOrWhiteSpace(text) || text == "0" ? null : text.Trim();
    }

    private static long Long(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value))
        {
            return 0;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var number))
                {
                    return number;
                }

                if (value.TryGetDouble(out var real))
                {
                    return (long)real;
                }

                return 0;
            case JsonValueKind.String:
                var text = value.GetString();
                if (long.TryParse(text, out var parsed))
                {
                    return parsed;
                }

                if (DateTimeOffset.TryParse(text, out var when))
                {
                    return when.ToUnixTimeMilliseconds();
                }

                return 0;
            case JsonValueKind.True:
                return 1;
            default:
                return 0;
        }
    }

    private static bool Flag(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => Long(el, name) != 0,
            JsonValueKind.String => value.GetString() is "1" or "true" or "True",
            _ => false,
        };
    }

    private static int? Year(JsonElement el, string name)
    {
        var value = Long(el, name);
        if (value < 1950 || value > 2100)
        {
            return null;
        }

        return (int)value;
    }

    private static IReadOnlyList<string> Tags(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value))
        {
            return Array.Empty<string>();
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                {
                    list.Add(text);
                }
            }

            return list;
        }

        if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } raw)
        {
            var list = new List<string>();
            var start = -1;
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i] is '(' or '[')
                {
                    start = i;
                }
                else if (start >= 0 && raw[i] is ')' or ']')
                {
                    list.Add(raw[start..(i + 1)]);
                    start = -1;
                }
            }

            return list.Count == 0 ? new List<string> { raw } : list;
        }

        return Array.Empty<string>();
    }
}

public static class RomQuery
{
    public const int MaxPage = 60;

    public static List<SystemRollup> Systems(IReadOnlyList<GameRecord> games)
    {
        var map = new Dictionary<string, SystemRollup>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            if (!map.TryGetValue(game.Platform, out var row))
            {
                row = new SystemRollup { System = game.Platform };
                map[game.Platform] = row;
            }

            row.Games += 1;
            row.Roms += game.Members.Count;
            row.Bytes += game.SizeBytes;
        }

        return map.Values.OrderBy(row => row.System, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<GameRecord> Filter(IReadOnlyList<GameRecord> games, string? query, string? platform, IReadOnlyList<string>? keys, string? sort)
    {
        if (keys != null)
        {
            var byKey = new Dictionary<string, GameRecord>(games.Count, StringComparer.Ordinal);
            foreach (var game in games)
            {
                byKey.TryAdd(game.Key, game);
            }

            var picked = new List<GameRecord>();
            foreach (var key in keys)
            {
                if (!string.IsNullOrWhiteSpace(key) && byKey.TryGetValue(key, out var game) && game.Matches(query, platform))
                {
                    picked.Add(game);
                }
            }

            return picked;
        }

        var filtered = new List<GameRecord>();
        foreach (var game in games)
        {
            if (game.Matches(query, platform))
            {
                filtered.Add(game);
            }
        }

        return Sort(filtered, sort);
    }

    public static List<GameRecord> Sort(List<GameRecord> games, string? sort)
    {
        var mode = (sort ?? "title").Trim();
        IEnumerable<GameRecord> ordered;
        if (mode.Equals("added", StringComparison.OrdinalIgnoreCase))
        {
            ordered = games.OrderByDescending(game => game.Added).ThenBy(game => game.Title, StringComparer.OrdinalIgnoreCase);
        }
        else if (mode.Equals("year", StringComparison.OrdinalIgnoreCase))
        {
            ordered = games.OrderByDescending(game => game.Year.HasValue).ThenByDescending(game => game.Year ?? 0).ThenBy(game => game.Title, StringComparer.OrdinalIgnoreCase);
        }
        else if (mode.Equals("title-desc", StringComparison.OrdinalIgnoreCase) || mode.Equals("za", StringComparison.OrdinalIgnoreCase))
        {
            ordered = games.OrderByDescending(game => game.Title, StringComparer.OrdinalIgnoreCase).ThenBy(game => game.Key, StringComparer.Ordinal);
        }
        else if (mode.Equals("size", StringComparison.OrdinalIgnoreCase))
        {
            ordered = games.OrderByDescending(game => game.SizeBytes).ThenBy(game => game.Title, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            ordered = games.OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase).ThenBy(game => game.Key, StringComparer.Ordinal);
        }

        return ordered.ToList();
    }

    public static (int Total, List<T> Page) Page<T>(IReadOnlyList<T> items, int offset, int limit)
    {
        if (offset < 0)
        {
            offset = 0;
        }

        if (limit < 1)
        {
            limit = 1;
        }

        if (limit > MaxPage)
        {
            limit = MaxPage;
        }

        if (offset >= items.Count)
        {
            return (items.Count, new List<T>());
        }

        var count = Math.Min(limit, items.Count - offset);
        var page = new List<T>(count);
        for (var i = 0; i < count; i++)
        {
            page.Add(items[offset + i]);
        }

        return (items.Count, page);
    }

    public static int ClampLimit(int limit, int fallback)
    {
        if (limit <= 0)
        {
            limit = fallback;
        }

        return Math.Min(limit, MaxPage);
    }

    /// <summary>
    /// Six title ranges. <c>#-D</c> is digits, symbols, and A–D. The id is what
    /// the client sends back; the label is only for display.
    /// </summary>
    public static readonly IReadOnlyList<LetterBucket> LetterBuckets = new LetterBucket[]
    {
        new("#-D", "# – D"),
        new("E-H", "E – H"),
        new("I-L", "I – L"),
        new("M-P", "M – P"),
        new("Q-S", "Q – S"),
        new("T-Z", "T – Z"),
    };

    /// <summary>
    /// Folded A–Z initial, or <c>#</c> when the title does not start with a letter.
    /// </summary>
    public static char TitleInitial(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return '#';
        }

        var folded = title.TrimStart().Normalize(NormalizationForm.FormD);
        foreach (var rune in folded.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var value = rune.Value;
            if (value is >= 'a' and <= 'z')
            {
                return (char)(value - 32);
            }

            if (value is >= 'A' and <= 'Z')
            {
                return (char)value;
            }

            return '#';
        }

        return '#';
    }

    /// <summary>
    /// Keeps the current order. An unknown bucket or letter matches nothing.
    /// A letter and a bucket both set must both match.
    /// </summary>
    public static List<GameRecord> NarrowLetters(IReadOnlyList<GameRecord> games, string? bucket, string? letter)
    {
        if (string.IsNullOrWhiteSpace(bucket) && string.IsNullOrWhiteSpace(letter))
        {
            return games as List<GameRecord> ?? games.ToList();
        }

        var narrowed = new List<GameRecord>();
        foreach (var game in games)
        {
            if (InLetterRange(TitleInitial(game.Title), bucket, letter))
            {
                narrowed.Add(game);
            }
        }

        return narrowed;
    }

    public static bool InLetterRange(char initial, string? bucket, string? letter)
    {
        if (!string.IsNullOrWhiteSpace(letter))
        {
            var want = letter.Trim();
            var letterOk = false;
            if (want == "#")
            {
                letterOk = initial == '#';
            }
            else if (want.Length == 1)
            {
                var unit = char.ToUpperInvariant(want[0]);
                letterOk = unit is >= 'A' and <= 'Z' && initial == unit;
            }

            if (!letterOk)
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(bucket))
        {
            return true;
        }

        var id = bucket.Trim();
        if (id.Equals("#-D", StringComparison.OrdinalIgnoreCase))
        {
            return initial == '#' || initial is >= 'A' and <= 'D';
        }

        if (id.Equals("E-H", StringComparison.OrdinalIgnoreCase))
        {
            return initial is >= 'E' and <= 'H';
        }

        if (id.Equals("I-L", StringComparison.OrdinalIgnoreCase))
        {
            return initial is >= 'I' and <= 'L';
        }

        if (id.Equals("M-P", StringComparison.OrdinalIgnoreCase))
        {
            return initial is >= 'M' and <= 'P';
        }

        if (id.Equals("Q-S", StringComparison.OrdinalIgnoreCase))
        {
            return initial is >= 'Q' and <= 'S';
        }

        if (id.Equals("T-Z", StringComparison.OrdinalIgnoreCase))
        {
            return initial is >= 'T' and <= 'Z';
        }

        return false;
    }

    /// <summary>Counts the six buckets over <paramref name="games"/> as they stand. Zeros are kept.</summary>
    public static List<LetterBucketCount> CountBuckets(IReadOnlyList<GameRecord> games)
    {
        var counts = new int[LetterBuckets.Count];
        foreach (var game in games)
        {
            counts[BucketIndex(TitleInitial(game.Title))]++;
        }

        var rows = new List<LetterBucketCount>(LetterBuckets.Count);
        for (var i = 0; i < LetterBuckets.Count; i++)
        {
            rows.Add(new LetterBucketCount(LetterBuckets[i].Id, LetterBuckets[i].Label, counts[i]));
        }

        return rows;
    }

    /// <summary>
    /// One row per index path-folder and platform pair, then any disk folder the
    /// index never used as a path prefix. <see cref="SystemNameCheck.Match"/> is
    /// true only when the folder string and the platform string are the same.
    /// </summary>
    public static List<SystemNameCheck> FolderPlatforms(IReadOnlyList<GameRecord> games, IReadOnlyList<string>? diskFolders)
    {
        var pairs = new Dictionary<(string Folder, string Platform), (string Folder, string Platform, int Games)>(PairComparer.Instance);
        foreach (var game in games)
        {
            var folder = FirstSegment(game);
            var platform = game.Platform?.Trim() ?? string.Empty;
            var key = (folder, platform);
            if (!pairs.TryGetValue(key, out var row))
            {
                pairs[key] = (folder, platform, 1);
            }
            else
            {
                pairs[key] = (row.Folder, row.Platform, row.Games + 1);
            }
        }

        var rows = new List<SystemNameCheck>();
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in pairs.Values
            .OrderBy(row => row.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Platform, StringComparer.OrdinalIgnoreCase))
        {
            seenFolders.Add(row.Folder);
            rows.Add(new SystemNameCheck(
                row.Folder,
                string.IsNullOrEmpty(row.Platform) ? null : row.Platform,
                row.Games,
                !string.IsNullOrEmpty(row.Folder) && string.Equals(row.Folder, row.Platform, StringComparison.OrdinalIgnoreCase),
                "index"));
        }

        if (diskFolders == null)
        {
            return rows;
        }

        foreach (var folder in diskFolders
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (seenFolders.Contains(folder))
            {
                continue;
            }

            var platform = pairs.Values.FirstOrDefault(row => string.Equals(row.Platform, folder, StringComparison.OrdinalIgnoreCase)).Platform;
            rows.Add(new SystemNameCheck(
                folder,
                string.IsNullOrEmpty(platform) ? null : platform,
                0,
                false,
                "disk"));
        }

        return rows;
    }

    private static int BucketIndex(char initial)
    {
        if (initial == '#' || initial is >= 'A' and <= 'D')
        {
            return 0;
        }

        if (initial <= 'H')
        {
            return 1;
        }

        if (initial <= 'L')
        {
            return 2;
        }

        if (initial <= 'P')
        {
            return 3;
        }

        if (initial <= 'S')
        {
            return 4;
        }

        return 5;
    }

    private static string FirstSegment(GameRecord game)
    {
        var path = string.IsNullOrWhiteSpace(game.CoverPath) ? game.Members[0].Path : game.CoverPath;
        var slash = path.IndexOf('/');
        var folder = slash > 0 ? path[..slash] : path;
        return folder.Trim();
    }

    private sealed class PairComparer : IEqualityComparer<(string Folder, string Platform)>
    {
        public static readonly PairComparer Instance = new();

        public bool Equals((string Folder, string Platform) x, (string Folder, string Platform) y) =>
            string.Equals(x.Folder, y.Folder, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Platform, y.Platform, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Folder, string Platform) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Folder ?? string.Empty),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Platform ?? string.Empty));
    }

    public static List<DuplicateGroup> IdenticalCopies(IReadOnlyList<RomEntry> items, string? platform)
    {
        var map = new Dictionary<long, List<RomEntry>>();
        var order = new List<long>();
        foreach (var item in items)
        {
            if (item.CopyGroup == 0)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(platform)
                && !string.Equals(item.Platform, platform.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!map.TryGetValue(item.CopyGroup, out var list))
            {
                list = new List<RomEntry>();
                map[item.CopyGroup] = list;
                order.Add(item.CopyGroup);
            }

            list.Add(item);
        }

        var groups = new List<DuplicateGroup>();
        foreach (var id in order)
        {
            var members = map[id];
            if (members.Count < 2)
            {
                continue;
            }

            groups.Add(new DuplicateGroup
            {
                Id = id,
                System = CommonPlatform(members),
                Members = members,
            });
        }

        return groups;
    }

    public static int VersionGroupCount(IReadOnlyList<RomEntry> items, string? platform) =>
        RomIndex.CountVersionGroups(items, platform);

    public static List<GameRecord> Related(GameRecord game, IReadOnlyList<GameRecord> games, int take)
    {
        var ranked = new List<(int Score, GameRecord Game)>();
        foreach (var other in games)
        {
            if (ReferenceEquals(other, game) || string.Equals(other.Key, game.Key, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(other.Platform, game.Platform, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var genre = !string.IsNullOrEmpty(game.Genre)
                && string.Equals(other.Genre, game.Genre, StringComparison.OrdinalIgnoreCase);
            var developer = !string.IsNullOrEmpty(game.Developer)
                && string.Equals(other.Developer, game.Developer, StringComparison.OrdinalIgnoreCase);
            if (!genre && !developer)
            {
                continue;
            }

            ranked.Add((genre && developer ? 2 : 1, other));
        }

        return ranked
            .OrderByDescending(row => row.Score)
            .ThenBy(row => row.Game.Title, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(row => row.Game)
            .ToList();
    }

    private static string CommonPlatform(IReadOnlyList<RomEntry> members)
    {
        string? platform = null;
        foreach (var member in members)
        {
            if (platform == null)
            {
                platform = member.Platform;
            }
            else if (!string.Equals(platform, member.Platform, StringComparison.OrdinalIgnoreCase))
            {
                return "Mixed";
            }
        }

        return string.IsNullOrWhiteSpace(platform) ? "Unknown" : platform;
    }
}
