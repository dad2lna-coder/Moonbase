using System.Xml;

namespace Moonfin.Server.Services;

/// <summary>
/// Which shared arcade BIOS zip a romset needs, and which parent romsets a clone or hack
/// archive needs beside it. FBNeo's DAT (romof chain, or the Neo Geo / PGM / Super Kaneko Nova
/// driver when the chain does not name a BIOS) wins when it is installed. Otherwise a small
/// romset map is used, and every Neo Geo set in that map defaults to neogeo.zip. Other zips in
/// the BIOS folder are not returned.
/// </summary>
internal sealed class ArcadeBiosCatalog
{
    private readonly Dictionary<string, Entry> _sets;

    private ArcadeBiosCatalog(Dictionary<string, Entry> sets)
    {
        _sets = sets;
    }

    public bool TryResolve(string? romset, out string? zipName)
    {
        zipName = null;
        var name = Normalize(romset);
        if (name.Length == 0 || !_sets.TryGetValue(name, out var start))
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        var current = name;
        var entry = start;
        while (true)
        {
            if (IsSharedBios(current))
            {
                zipName = current + ".zip";
                return true;
            }

            if (string.IsNullOrEmpty(entry.Romof))
            {
                break;
            }

            current = Normalize(entry.Romof);
            if (current.Length == 0 || !seen.Add(current))
            {
                break;
            }

            if (!_sets.TryGetValue(current, out entry))
            {
                if (IsSharedBios(current))
                {
                    zipName = current + ".zip";
                }

                break;
            }
        }

        zipName = ZipForDriver(start.Source);
        return true;
    }

    /// <summary>
    /// The parent romset zips a clone or hack archive needs in its own directory, nearest
    /// parent first. FBNeo looks for a parent set's files beside the loaded archive, so a hack
    /// such as dkchrmx cannot boot without dkong.zip sitting next to it. The walk follows the
    /// DAT's romof chain and stops at anything this catalog classifies as a BIOS (neogeo, pgm,
    /// skns, or a set the DAT marks isbios), because those are delivered through the BIOS
    /// folder instead and must not be downloaded a second time as a parent. Empty when the set
    /// is unknown, needs no parent, or the chain cannot be followed.
    /// </summary>
    public IReadOnlyList<string> ResolveParents(string? romset)
    {
        var name = Normalize(romset);
        if (name.Length == 0 || !_sets.TryGetValue(name, out var entry))
        {
            return Array.Empty<string>();
        }

        var parents = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        while (!string.IsNullOrEmpty(entry.Romof))
        {
            var parent = Normalize(entry.Romof);
            if (parent.Length == 0 || !seen.Add(parent) || IsBiosSet(parent))
            {
                break;
            }

            parents.Add(parent + ".zip");
            if (!_sets.TryGetValue(parent, out entry))
            {
                break;
            }
        }

        return parents;
    }

    public static ArcadeBiosCatalog? TryLoad(string path)
    {
        try
        {
            return Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }

    public static ArcadeBiosCatalog Load(string path)
    {
        var sets = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var settings = new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            DtdProcessing = DtdProcessing.Ignore,
        };

        using var reader = XmlReader.Create(path, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName is not ("game" or "machine"))
            {
                continue;
            }

            var name = Normalize(reader.GetAttribute("name"));
            if (name.Length == 0 || sets.ContainsKey(name))
            {
                continue;
            }

            sets[name] = new Entry(
                Normalize(reader.GetAttribute("romof")),
                reader.GetAttribute("sourcefile"),
                string.Equals(reader.GetAttribute("isbios"), "yes", StringComparison.OrdinalIgnoreCase));
        }

        return new ArcadeBiosCatalog(sets);
    }

    /// <summary>
    /// Offline default used when no FBNeo DAT is installed or the set is not in it.
    /// Neo Geo names all map to neogeo.zip.
    /// </summary>
    public static string? FallbackZip(string? romset)
    {
        var name = Normalize(romset);
        return name.Length > 0 && Fallback.TryGetValue(name, out var zip) ? zip : null;
    }

    public static string CanonicalZip(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        if (name.Equals("neogeo.zip", StringComparison.OrdinalIgnoreCase))
        {
            return "neogeo.zip";
        }

        if (name.Equals("pgm.zip", StringComparison.OrdinalIgnoreCase))
        {
            return "pgm.zip";
        }

        if (name.Equals("skns.zip", StringComparison.OrdinalIgnoreCase))
        {
            return "skns.zip";
        }

        return name;
    }

    public static bool IsSharedZip(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        return name.Equals("neogeo.zip", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pgm.zip", StringComparison.OrdinalIgnoreCase)
            || name.Equals("skns.zip", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ZipForDriver(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        if (source.Contains("d_neogeo", StringComparison.OrdinalIgnoreCase)
            || source.Contains("neogeo/", StringComparison.OrdinalIgnoreCase))
        {
            return "neogeo.zip";
        }

        if (source.Contains("d_pgm", StringComparison.OrdinalIgnoreCase)
            || source.Contains("/pgm/", StringComparison.OrdinalIgnoreCase))
        {
            return "pgm.zip";
        }

        if (source.Contains("d_suprnova", StringComparison.OrdinalIgnoreCase)
            || source.Contains("skns", StringComparison.OrdinalIgnoreCase))
        {
            return "skns.zip";
        }

        return null;
    }

    private static bool IsSharedBios(string name) =>
        name.Equals("neogeo", StringComparison.OrdinalIgnoreCase)
        || name.Equals("pgm", StringComparison.OrdinalIgnoreCase)
        || name.Equals("skns", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        return Path.GetFileNameWithoutExtension(name.Trim()).ToLowerInvariant();
    }

    /// <summary>True for the shared BIOS zips and for any set the DAT marks as a BIOS.</summary>
    private bool IsBiosSet(string name) =>
        IsSharedBios(name) || (_sets.TryGetValue(name, out var entry) && entry.IsBios);

    private readonly record struct Entry(string Romof, string? Source, bool IsBios);

    private static readonly Dictionary<string, string> Fallback = new(StringComparer.OrdinalIgnoreCase)
    {
        ["svc"] = "neogeo.zip",
        ["svcboot"] = "neogeo.zip",
        ["svcsplus"] = "neogeo.zip",
        ["svcplus"] = "neogeo.zip",
        ["kof94"] = "neogeo.zip",
        ["kof95"] = "neogeo.zip",
        ["kof96"] = "neogeo.zip",
        ["kof97"] = "neogeo.zip",
        ["kof98"] = "neogeo.zip",
        ["kof99"] = "neogeo.zip",
        ["kof2000"] = "neogeo.zip",
        ["kof2001"] = "neogeo.zip",
        ["kof2002"] = "neogeo.zip",
        ["kof2003"] = "neogeo.zip",
        ["garou"] = "neogeo.zip",
        ["mslug"] = "neogeo.zip",
        ["mslug2"] = "neogeo.zip",
        ["mslug3"] = "neogeo.zip",
        ["mslug4"] = "neogeo.zip",
        ["mslug5"] = "neogeo.zip",
        ["mslugx"] = "neogeo.zip",
        ["samsho"] = "neogeo.zip",
        ["samsho2"] = "neogeo.zip",
        ["samsho3"] = "neogeo.zip",
        ["samsho4"] = "neogeo.zip",
        ["samsho5"] = "neogeo.zip",
        ["fatfury1"] = "neogeo.zip",
        ["fatfury2"] = "neogeo.zip",
        ["fatfury3"] = "neogeo.zip",
        ["rbff1"] = "neogeo.zip",
        ["rbff2"] = "neogeo.zip",
        ["lastblad"] = "neogeo.zip",
        ["lastbld2"] = "neogeo.zip",
        ["wakuwak7"] = "neogeo.zip",
        ["blazstar"] = "neogeo.zip",
        ["pulstar"] = "neogeo.zip",
        ["shocktro"] = "neogeo.zip",
        ["s1945p"] = "neogeo.zip",
        ["aof"] = "neogeo.zip",
        ["aof2"] = "neogeo.zip",
        ["aof3"] = "neogeo.zip",
        ["sengoku"] = "neogeo.zip",
        ["sengoku2"] = "neogeo.zip",
        ["sengoku3"] = "neogeo.zip",
        ["bstars"] = "neogeo.zip",
        ["bstars2"] = "neogeo.zip",
        ["breakers"] = "neogeo.zip",
        ["kotm"] = "neogeo.zip",
        ["kotm2"] = "neogeo.zip",
        ["matrim"] = "neogeo.zip",
        ["rotd"] = "neogeo.zip",
        ["nitd"] = "neogeo.zip",
        ["karnovr"] = "neogeo.zip",
        ["preisle2"] = "neogeo.zip",
        ["viewpoin"] = "neogeo.zip",
        ["pbobblen"] = "neogeo.zip",
        ["magdrop3"] = "neogeo.zip",
        ["tws96"] = "neogeo.zip",
        ["ninjamas"] = "neogeo.zip",
        ["alpham2"] = "neogeo.zip",
        ["kov"] = "pgm.zip",
        ["kov2"] = "pgm.zip",
        ["kovsh"] = "pgm.zip",
        ["kovshp"] = "pgm.zip",
        ["orlegend"] = "pgm.zip",
        ["ddp2"] = "pgm.zip",
        ["ddp3"] = "pgm.zip",
        ["martmast"] = "pgm.zip",
        ["espgal"] = "pgm.zip",
        ["olds"] = "pgm.zip",
        ["oldsplus"] = "pgm.zip",
        ["killbld"] = "pgm.zip",
        ["theglad"] = "pgm.zip",
        ["photoy2k"] = "pgm.zip",
        ["ket"] = "pgm.zip",
        ["svg"] = "pgm.zip",
        ["dmnfrnt"] = "pgm.zip",
        ["puzzli2"] = "pgm.zip",
        ["cyvern"] = "skns.zip",
        ["galpans2"] = "skns.zip",
        ["sengekis"] = "skns.zip",
        ["panicstr"] = "skns.zip",
        ["vblokbrk"] = "skns.zip",
        ["sarukani"] = "skns.zip",
        ["jjparad2"] = "skns.zip",
    };
}
