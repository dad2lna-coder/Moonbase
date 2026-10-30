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
        var byVersion = new Dictionary<long, List<RomEntry>>();
        foreach (var item in items)
        {
            if (item.VersionGroup == 0)
            {
                continue;
            }

            if (!byVersion.TryGetValue(item.VersionGroup, out var list))
            {
                list = new List<RomEntry>();
                byVersion[item.VersionGroup] = list;
            }

            list.Add(item);
        }

        var games = new List<GameRecord>();
        var emitted = new HashSet<long>();
        foreach (var item in items)
        {
            if (item.VersionGroup == 0)
            {
                games.Add(GameRecord.Create(new List<RomEntry> { item }));
                continue;
            }

            if (!emitted.Add(item.VersionGroup))
            {
                continue;
            }

            games.Add(GameRecord.Create(byVersion[item.VersionGroup]));
        }

        return games;
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
        IEnumerable<GameRecord> ordered = mode.Equals("added", StringComparison.OrdinalIgnoreCase)
            ? games.OrderByDescending(game => game.Added).ThenBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
            : mode.Equals("year", StringComparison.OrdinalIgnoreCase)
                ? games.OrderByDescending(game => game.Year.HasValue).ThenByDescending(game => game.Year ?? 0).ThenBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
                : games.OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase).ThenBy(game => game.Key, StringComparer.Ordinal);
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

    public static int VersionGroupCount(IReadOnlyList<RomEntry> items, string? platform)
    {
        var ids = new HashSet<long>();
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

            ids.Add(item.VersionGroup);
        }

        return ids.Count;
    }

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
