using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Moonfin.Plugins.RomManager;

/// <summary>A developer as the Library groups it: a fold key plus the name shown.</summary>
public sealed record DeveloperName(string Key, string Name);

public sealed class DeveloperRollup
{
    public required string Key { get; init; }

    public required string Name { get; set; }

    public int Games { get; set; }

    public int Roms { get; set; }

    public Dictionary<string, int> Spellings { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The one place developer names are cleaned and merged. The index's <c>dev</c> field mixes
/// spellings ("SEGA", "Sega ???", "Sega Enterprises", "Sega AM2"); this folds case and accents,
/// strips trailing junk and safe company suffixes, then applies an explicit alias table for
/// names that are clearly the same company. Co-developed credits ("Sega/Gremlin",
/// "Nintendo R&amp;D1, Tose") are left as their own entry.
/// </summary>
public static class RomDevelopers
{
    // Fold key -> shown name. Only merges where the names are the same company: renamed
    // (Tehkan -> Tecmo, Alpha Denshi -> ADK), an internal studio (Sega AM2, Nintendo EAD),
    // or a short brand of the full name (Nichibutsu = Nihon Bussan, Sunsoft = Sun Electronics).
    public static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["sega"] = "Sega",
        ["segaenterprises"] = "Sega",
        ["segaam1"] = "Sega",
        ["segaam2"] = "Sega",
        ["segaam3"] = "Sega",
        ["segaam4"] = "Sega",
        ["segaam5"] = "Sega",
        ["segaam7"] = "Sega",
        ["segaam8"] = "Sega",
        ["segatechnicalinstitute"] = "Sega",
        ["segaelectronics"] = "Sega",
        ["segainteractive"] = "Sega",
        ["namco"] = "Namco",
        ["namcot"] = "Namco",
        ["namcoltd"] = "Namco",
        ["snk"] = "SNK",
        ["snkplaymore"] = "SNK",
        ["snkcorporation"] = "SNK",
        ["dataeast"] = "Data East",
        ["dataeastcorporation"] = "Data East",
        ["konami"] = "Konami",
        ["konamiindustry"] = "Konami",
        ["konamiindustries"] = "Konami",
        ["konamidigitalentertainment"] = "Konami",
        ["konamicomputerentertainmentjapan"] = "Konami",
        ["konamicomputerentertainmenttokyo"] = "Konami",
        ["konamicomputerentertainmentosaka"] = "Konami",
        ["konamicomputerentertainmentkobe"] = "Konami",
        ["capcom"] = "Capcom",
        ["capcomproductionstudio1"] = "Capcom",
        ["capcomproductionstudio2"] = "Capcom",
        ["capcomproductionstudio3"] = "Capcom",
        ["capcomproductionstudio4"] = "Capcom",
        ["capcomusa"] = "Capcom",
        ["taito"] = "Taito",
        ["irem"] = "Irem",
        ["jaleco"] = "Jaleco",
        ["toaplan"] = "Toaplan",
        ["tecmo"] = "Tecmo",
        ["tehkan"] = "Tecmo",
        ["technosjapan"] = "Technōs Japan",
        ["technos"] = "Technōs Japan",
        ["hudson"] = "Hudson Soft",
        ["hudsonsoft"] = "Hudson Soft",
        ["midway"] = "Midway",
        ["midwaygames"] = "Midway",
        ["ballymidway"] = "Midway",
        ["williams"] = "Williams",
        ["williamselectronics"] = "Williams",
        ["williamselectronicsgames"] = "Williams",
        ["tose"] = "TOSE",
        ["alphadenshi"] = "ADK",
        ["alphadenshikogyo"] = "ADK",
        ["adk"] = "ADK",
        ["nichibutsu"] = "Nichibutsu",
        ["nihonbussan"] = "Nichibutsu",
        ["sunsoft"] = "Sunsoft",
        ["sunelectronics"] = "Sunsoft",
        ["nintendo"] = "Nintendo",
        ["nintendoead"] = "Nintendo",
        ["nintendord"] = "Nintendo",
        ["nintendord1"] = "Nintendo",
        ["nintendord2"] = "Nintendo",
        ["nintendord3"] = "Nintendo",
        ["nintendord4"] = "Nintendo",
        ["hallaboratory"] = "HAL Laboratory",
        ["hallabs"] = "HAL Laboratory",
        ["codemasters"] = "Codemasters",
        ["codemasterssoftware"] = "Codemasters",
        ["gremlingraphics"] = "Gremlin Graphics",
        ["gremlingraphicssoftware"] = "Gremlin Graphics",
        ["gremlininteractive"] = "Gremlin Graphics",
        ["visco"] = "Visco",
        ["viscogames"] = "Visco",
        ["sims"] = "SIMS",
        ["internationalgamessystem"] = "IGS",
        ["igs"] = "IGS",
        ["seibukaihatsu"] = "Seibu Kaihatsu",
        ["kaneko"] = "Kaneko",
        ["cave"] = "Cave",
        ["compile"] = "Compile",
        ["comad"] = "Comad",
        ["comadindustry"] = "Comad",
    };

    // Not a developer: hidden from Browse by Developer.
    private static readonly HashSet<string> Junk = new(StringComparer.Ordinal)
    {
        string.Empty, "unknown", "unk", "na", "none", "various", "notavailable", "unlicensed",
    };

    // Safe trailing company suffixes. Applied repeatedly ("Foo Co., Ltd." -> "Foo").
    private static readonly Regex Suffix = new(
        @"[\s,]+(co\.?\s*,?\s*ltd\.?|ltd\.?|limited|inc\.?|incorporated|corporation|corp\.?|enterprises|entertainment|manufacturing|mfg\.?|llc)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Trailing question marks the index uses for "unsure" ("Sega ???", "Nintendo R&D?").
    private static readonly Regex Unsure = new(@"\s*\?+\s*$", RegexOptions.CultureInvariant);

    private static readonly ConcurrentDictionary<string, DeveloperName?> Cache = new(StringComparer.Ordinal);

    /// <summary>The cleaned developer, or null when the value is blank or not a developer.</summary>
    public static DeveloperName? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (Cache.Count > 20000)
        {
            Cache.Clear();
        }

        return Cache.GetOrAdd(raw, Compute);
    }

    /// <summary>The fold key a client sends back to pick a developer ("Data East" -> "dataeast").</summary>
    public static string KeyOf(string? nameOrKey) => Normalize(nameOrKey)?.Key ?? Fold(nameOrKey);

    public static DeveloperName? ForGame(GameRecord game)
    {
        var found = Normalize(game.Developer);
        if (found != null)
        {
            return found;
        }

        foreach (var member in game.Members)
        {
            found = Normalize(member.Developer);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Developers by game count (the same game grouping as the console counts).</summary>
    public static List<DeveloperRollup> Rollup(IReadOnlyList<GameRecord> games)
    {
        var map = new Dictionary<string, DeveloperRollup>(StringComparer.Ordinal);
        foreach (var game in games)
        {
            var dev = ForGame(game);
            if (dev == null)
            {
                continue;
            }

            if (!map.TryGetValue(dev.Key, out var row))
            {
                row = new DeveloperRollup { Key = dev.Key, Name = dev.Name };
                map[dev.Key] = row;
            }

            row.Games += 1;
            row.Roms += game.Members.Count;
            row.Spellings[dev.Name] = row.Spellings.GetValueOrDefault(dev.Name) + 1;
        }

        foreach (var row in map.Values)
        {
            if (Aliases.TryGetValue(row.Key, out var canonical))
            {
                row.Name = canonical;
                continue;
            }

            row.Name = row.Spellings
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .First().Key;
        }

        return map.Values
            .OrderByDescending(row => row.Games)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool Matches(GameRecord game, string key) =>
        key.Length > 0 && string.Equals(ForGame(game)?.Key, key, StringComparison.Ordinal);

    private static DeveloperName? Compute(string raw)
    {
        var text = raw.Normalize(NormalizationForm.FormC).Trim();
        for (var i = 0; i < 4; i++)
        {
            var before = text;
            text = Unsure.Replace(text, string.Empty).Trim();
            text = Suffix.Replace(text, string.Empty).Trim().TrimEnd(',', '.').Trim();
            if (text == before)
            {
                break;
            }
        }

        var key = Fold(text);
        if (Junk.Contains(key))
        {
            return null;
        }

        if (Aliases.TryGetValue(key, out var canonical))
        {
            // Group under the canonical name's key so "Sega AM2" and "SEGA" share one row.
            return new DeveloperName(Fold(canonical), canonical);
        }

        return new DeveloperName(key, text);
    }

    /// <summary>Lower-case letters and digits only, accents removed ("Technōs" -> "technos").</summary>
    public static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }
}
