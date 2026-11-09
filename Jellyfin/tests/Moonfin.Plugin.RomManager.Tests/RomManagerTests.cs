using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moonfin.Plugins.RomManager;
using Moonfin.Server.Plugins;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Plugin.RomManager.Tests;

public class RomPathTests
{
    [Theory]
    [InlineData("NES/Mario", false)]
    [InlineData("batch/NES/Mario", true)]
    public void AcceptsLibraryAndTrashPaths(string path, bool trash)
    {
        Assert.True(RomPaths.TryValidate(path, out var isTrash));
        Assert.Equal(trash, isTrash);
    }

    [Theory]
    [InlineData("../NES/Mario")]
    [InlineData("NES/../Mario")]
    [InlineData("/NES/Mario")]
    [InlineData(".NES/Mario")]
    [InlineData("NES/.hidden")]
    [InlineData("NES")]
    [InlineData("a/b/c/d")]
    [InlineData("NES//Mario")]
    [InlineData("C:/NES/Mario")]
    [InlineData("")]
    public void RejectsUnsafePaths(string path)
    {
        Assert.False(RomPaths.TryValidate(path, out _));
    }
}

public class RomLibraryTests
{
    [Fact]
    public void VersionsShareOneStableKeyAndTheBestMemberSuppliesTheTitle()
    {
        var games = RomIndex.Group(RomIndex.Parse(SampleIndex, out _));
        var chrono = Assert.Single(games, game => game.Key == "m:SNES/Chrono");
        Assert.Equal("Chrono Trigger", chrono.Title);
        Assert.Equal("SNES/Chrono", chrono.CoverPath);
        Assert.Equal(2, chrono.Members.Count);
        Assert.Equal(1050, chrono.SizeBytes);
        Assert.Equal("p:NES/Mario", Assert.Single(games, game => game.Title == "Super Mario Bros." && game.Cover == 1).Key);
        Assert.Contains(games, game => game.Key == "p:NES/Mario-copy");
    }

    [Fact]
    public void SearchFiltersAndPagesWithoutMatchingDeveloperAlone()
    {
        var games = RomIndex.Group(RomIndex.Parse(SampleIndex, out _));
        Assert.Equal(2, RomQuery.Filter(games, "SQUARE", null, null, "title").Count);
        Assert.Equal(2, RomQuery.Filter(games, "snes", null, null, "title").Count);
        Assert.Equal(2, RomQuery.Filter(games, "rpg", null, null, "title").Count);
        Assert.Empty(RomQuery.Filter(games, "sonic team", null, null, "title"));
        Assert.Equal(2, RomQuery.Filter(games, null, "NES", null, "title").Count);

        var added = RomQuery.Filter(games, null, null, null, "added");
        Assert.Equal("p:NES/Mario", added[0].Key);
        var byYear = RomQuery.Filter(games, null, null, null, "year");
        Assert.Equal("Chrono Trigger", byYear[0].Title);

        var bySize = RomQuery.Filter(games, null, null, null, "size");
        Assert.Equal("Chrono Trigger", bySize[0].Title);
        var za = RomQuery.Filter(games, null, null, null, "title-desc");
        Assert.Equal("p:NES/Mario", za[0].Key);

        var page = RomQuery.Page(RomQuery.Filter(games, null, null, null, "title"), 1, 1);
        Assert.Equal(games.Count, page.Total);
        Assert.Equal("Secret of Mana", Assert.Single(page.Page).Title);

        var keyed = RomQuery.Filter(games, null, null, new[] { "p:Genesis/Sonic", "missing", "m:SNES/Chrono" }, "added");
        Assert.Equal(new[] { "p:Genesis/Sonic", "m:SNES/Chrono" }, keyed.Select(game => game.Key).ToArray());
    }

    [Fact]
    public void IdenticalCopiesKeepTheFlagAndVersionGroupsAreCounted()
    {
        var items = RomIndex.Parse(SampleIndex, out _);
        var groups = RomQuery.IdenticalCopies(items, null);
        var group = Assert.Single(groups);
        Assert.Equal(3, group.Id);
        Assert.Equal(2, group.Members.Count);
        Assert.Contains(group.Members, member => member.KeepCopy && member.Path == "NES/Mario");
        Assert.Contains(group.Members, member => !member.KeepCopy);
        Assert.Equal(1, RomQuery.VersionGroupCount(items, null));
        Assert.Empty(RomQuery.IdenticalCopies(items, "SNES"));
    }

    [Fact]
    public void VersionGroupsStayInsideOneSystem()
    {
        var items = RomIndex.Parse("""
        {"items":[
          {"p":"NES/A","s":"NES","t":"Alpha","f":"A.nes","mg":4,"km":1},
          {"p":"SNES/A","s":"SNES","t":"Alpha","f":"A.sfc","mg":4,"km":1}
        ]}
        """, out _);
        var games = RomIndex.Group(items);
        Assert.Equal(2, games.Count);
        Assert.Contains(games, game => game.Key == "m:NES/A" && game.Platform == "NES");
        Assert.Contains(games, game => game.Key == "m:SNES/A" && game.Platform == "SNES");
        Assert.Equal(2, RomQuery.VersionGroupCount(items, null));
        Assert.Equal(1, RomQuery.VersionGroupCount(items, "NES"));
    }

    [Fact]
    public void LetterRangesAndFolderNames()
    {
        Assert.Equal('#', RomQuery.TitleInitial("1942"));
        Assert.Equal('#', RomQuery.TitleInitial("  *star"));
        Assert.Equal('E', RomQuery.TitleInitial("Éclair"));
        Assert.Equal('Q', RomQuery.TitleInitial("Q*bert"));

        var games = RomIndex.Group(RomIndex.Parse(NameIndex, out _));
        var arcade = RomQuery.Filter(games, null, "Arcade", null, "title");
        var counts = RomQuery.CountBuckets(arcade);
        Assert.Equal(new[] { "#-D", "E-H", "I-L", "M-P", "Q-S", "T-Z" }, counts.Select(row => row.Id).ToArray());
        Assert.Equal(2, counts[0].Games);
        Assert.Equal(1, counts[1].Games);
        Assert.Equal(0, counts[2].Games);
        Assert.Equal(1, counts[4].Games);
        Assert.Equal("1942", Assert.Single(RomQuery.NarrowLetters(arcade, null, "#")).Title);
        Assert.Equal("Q*bert", Assert.Single(RomQuery.NarrowLetters(arcade, "q-s", null)).Title);
        Assert.Empty(RomQuery.NarrowLetters(arcade, "nope", null));

        var names = RomQuery.FolderPlatforms(games, new[] { "mame", "snes" });
        var mame = Assert.Single(names, row => row.Folder == "mame");
        Assert.Equal("Arcade", mame.Platform);
        Assert.False(mame.Match);
        Assert.Equal("index", mame.Source);
        Assert.True(Assert.Single(names, row => row.Folder == "NES" && row.Platform == "NES").Match);
        var diskOnly = Assert.Single(names, row => row.Source == "disk");
        Assert.Equal("snes", diskOnly.Folder);
        Assert.False(diskOnly.Match);
    }

    internal const string NameIndex = """
    {"items":[
      {"p":"mame/1942","s":"Arcade","t":"1942","f":"1942.zip","mg":1,"km":1},
      {"p":"mame/1942a","s":"Arcade","t":"1942","f":"1942a.zip","mg":1,"km":0},
      {"p":"Arcade/ddragon","s":"Arcade","t":"Double Dragon","f":"ddragon.zip","mg":0},
      {"p":"Arcade/elev","s":"Arcade","t":"Elevator Action","f":"elev.zip","mg":0},
      {"p":"Arcade/qbert","s":"Arcade","t":"Q*bert","f":"qbert.zip","mg":0},
      {"p":"NES/Mario","s":"NES","t":"Super Mario Bros.","f":"mario.nes","mg":0}
    ]}
    """;

    internal const string SampleIndex = """
    {
      "meta": {"built": true},
      "items": [
        {"p":"SNES/Chrono-eu","s":"SNES","t":"Chrono Trigger EU","f":"Chrono Trigger (Europe) (Rev 1) [t1].sfc","z":50,"y":1995,"tg":["(Europe)","(Rev 1)","[t1]"],"c":1,"ge":"RPG","pub":"Square","dev":"Square","m":40,"sg":0,"ks":0,"mg":7,"km":0},
        {"p":"SNES/Chrono","s":"SNES","t":"Chrono Trigger","f":"Chrono Trigger (USA).sfc","z":1000,"y":1995,"tg":["(USA)","[!]"],"c":2,"lb":"123","ge":"RPG","pub":"Square","dev":"Square","m":100,"sg":0,"ks":0,"mg":7,"km":1},
        {"p":"NES/Mario","s":"NES","t":"Super Mario Bros.","f":"Super Mario Bros. (World).nes","z":400,"y":1985,"tg":["(World)"],"c":1,"ge":"Platform","pub":"Nintendo","dev":"Nintendo","m":900,"sg":3,"ks":1,"mg":0,"km":0},
        {"p":"NES/Mario-copy","s":"NES","t":"Super Mario Bros.","f":"Super Mario Bros. (World) (copy).nes","z":400,"y":1985,"tg":["(World)"],"c":0,"ge":"Platform","pub":"Nintendo","dev":"Nintendo","m":800,"sg":3,"ks":0,"mg":0,"km":0},
        {"p":"Genesis/Sonic","s":"Genesis","t":"Sonic","f":"Sonic (USA).md","z":20,"y":1991,"tg":["(USA)"],"c":0,"ge":"Platform","pub":"Sega","dev":"Sonic Team","m":10,"sg":0,"ks":0,"mg":0,"km":0},
        {"p":"SNES/Mana","s":"SNES","t":"Secret of Mana","f":"Secret of Mana (USA) [h1].sfc","z":10,"y":1993,"tg":["(USA)","[h1]"],"c":0,"ge":"RPG","pub":"Square","dev":"Square","m":5,"sg":0,"ks":0,"mg":0,"km":0}
      ]
    }
    """;
}

public class RomCheatTagTests
{
    [Theory]
    [InlineData("[t]", true)]
    [InlineData("[t1]", true)]
    [InlineData("[t+3]", true)]
    [InlineData("[t1+2C]", true)]
    [InlineData("(Cheat)", true)]
    [InlineData("(Trainer)", true)]
    [InlineData("[CHEAT CODES]", true)]
    [InlineData("[T+Eng]", false)]
    [InlineData("[T-Fre]", false)]
    [InlineData("(Translated)", false)]
    [InlineData("(USA)", false)]
    [InlineData("[!]", false)]
    [InlineData("[h1]", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DetectsCheatAndTrainerTags(string? tag, bool cheat)
    {
        Assert.Equal(cheat, RomTags.IsCheatTag(tag));
    }

    [Fact]
    public void IsCheatChecksEveryTag()
    {
        Assert.True(RomTags.IsCheat(new[] { "(USA)", "[t1]" }));
        Assert.False(RomTags.IsCheat(new[] { "(USA)", "[T+Eng]" }));
        Assert.False(RomTags.IsCheat(null));
    }
}

public class RomDeveloperTests
{
    [Theory]
    [InlineData("SEGA", "Sega")]
    [InlineData("Sega ???", "Sega")]
    [InlineData("Sega Enterprises", "Sega")]
    [InlineData("Sega Enterprises, Ltd.", "Sega")]
    [InlineData("Sega AM2", "Sega")]
    [InlineData("Sega Technical Institute", "Sega")]
    [InlineData("capcom co., ltd.", "Capcom")]
    [InlineData("Konami Industry", "Konami")]
    [InlineData("Namcot", "Namco")]
    [InlineData("SNK Playmore", "SNK")]
    [InlineData("Data East Corporation", "Data East")]
    [InlineData("Technos Japan Corp.", "Technōs Japan")]
    [InlineData("Technōs Japan", "Technōs Japan")]
    [InlineData("Tehkan", "Tecmo")]
    [InlineData("ToaPlan", "Toaplan")]
    [InlineData("Alpha Denshi Kōgyō", "ADK")]
    [InlineData("Nihon Bussan", "Nichibutsu")]
    [InlineData("Sun Electronics", "Sunsoft")]
    [InlineData("Midway Manufacturing", "Midway")]
    [InlineData("Bally Midway Manufacturing", "Midway")]
    [InlineData("Nintendo R&D1", "Nintendo")]
    [InlineData("Nintendo R&D?", "Nintendo")]
    [InlineData("Hudson", "Hudson Soft")]
    [InlineData("International Games System", "IGS")]
    public void MergesClearlyIdenticalCompanies(string raw, string expected)
    {
        var dev = RomDevelopers.Normalize(raw);
        Assert.NotNull(dev);
        Assert.Equal(expected, dev!.Name);
        Assert.Equal(RomDevelopers.Fold(expected), dev.Key);
    }

    [Theory]
    [InlineData("Technosoft", "Technōs Japan")]
    [InlineData("Sega/Gremlin", "Sega")]
    [InlineData("Segaman Production", "Sega")]
    [InlineData("Bandai Namco", "Namco")]
    [InlineData("Atari Games", "Atari")]
    [InlineData("Gremlin Industries", "Gremlin Graphics")]
    [InlineData("Taito America", "Taito")]
    public void KeepsDifferentCompaniesApart(string raw, string other)
    {
        Assert.NotEqual(RomDevelopers.Normalize(other)!.Key, RomDevelopers.Normalize(raw)!.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Unknown")]
    [InlineData("???")]
    public void BlankAndUnknownAreNotDevelopers(string? raw)
    {
        Assert.Null(RomDevelopers.Normalize(raw));
    }

    [Fact]
    public void EveryAliasPointsAtItsOwnCanonicalGroup()
    {
        foreach (var pair in RomDevelopers.Aliases)
        {
            Assert.Equal(pair.Key, RomDevelopers.Fold(pair.Key));
            var canonical = RomDevelopers.Fold(pair.Value);
            Assert.True(RomDevelopers.Aliases.TryGetValue(canonical, out var self) && self == pair.Value, pair.Key + " -> " + pair.Value);
        }
    }

    [Fact]
    public void RollupCountsGamesNotRomsAndUsesTheMostCommonSpelling()
    {
        var items = RomIndex.Parse("""
        {"items":[
          {"p":"Arcade/sf2","s":"Arcade","t":"Street Fighter II","f":"sf2.zip","dev":"Capcom","mg":1,"km":1},
          {"p":"Arcade/sf2u","s":"Arcade","t":"Street Fighter II","f":"sf2u.zip","dev":"Capcom","mg":1,"km":0},
          {"p":"SNES/sf2","s":"SNES","t":"Street Fighter II","f":"sf2.sfc","dev":"CAPCOM Co., Ltd.","mg":0},
          {"p":"Arcade/ofox","s":"Arcade","t":"Outfoxies","f":"outfxies.zip","dev":"Namco ???","mg":0},
          {"p":"Genesis/x","s":"Genesis","t":"X","f":"x.md","dev":"Some Studio","mg":0},
          {"p":"Genesis/y","s":"Genesis","t":"Y","f":"y.md","dev":"Some studio","mg":0},
          {"p":"Genesis/z","s":"Genesis","t":"Z","f":"z.md","dev":"Some Studio","mg":0},
          {"p":"Genesis/u","s":"Genesis","t":"U","f":"u.md","dev":"","mg":0}
        ]}
        """, out _);
        var rows = RomDevelopers.Rollup(RomIndex.Group(items));
        var capcom = Assert.Single(rows, row => row.Name == "Capcom");
        Assert.Equal(2, capcom.Games);
        Assert.Equal(3, capcom.Roms);
        var studio = Assert.Single(rows, row => row.Key == "somestudio");
        Assert.Equal("Some Studio", studio.Name);
        Assert.Equal(3, studio.Games);
        Assert.Equal("Namco", Assert.Single(rows, row => row.Key == "namco").Name);
        Assert.Equal(3, rows.Count);
        Assert.Equal("somestudio", rows[0].Key);
    }
}

public class RomLogoTests
{
    [Theory]
    [InlineData("../consoles/SNES")]
    [InlineData("..")]
    [InlineData("SNES/../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("..\\x")]
    [InlineData("C:x")]
    [InlineData(".hidden")]
    [InlineData("")]
    [InlineData("a\u0000b")]
    public void RejectsUnsafeNames(string name)
    {
        Assert.False(RomLogos.IsSafeName(name));
    }

    [Theory]
    [InlineData("SNES")]
    [InlineData("Master System")]
    [InlineData("Data East")]
    [InlineData("Technōs Japan")]
    [InlineData("Sega/Gremlin")]
    public void AcceptsOrdinaryNames(string name)
    {
        Assert.Equal(!name.Contains('/'), RomLogos.IsSafeName(name));
    }

    [Fact]
    public void LoadsOnlyRealImagesFromTheKindFolder()
    {
        var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-logos-" + Guid.NewGuid().ToString("N")));
        try
        {
            var consoles = Directory.CreateDirectory(Path.Combine(temp.FullName, ".logos", "consoles")).FullName;
            var developers = Directory.CreateDirectory(Path.Combine(temp.FullName, ".logos", "developers")).FullName;
            File.WriteAllText(Path.Combine(consoles, "Master System.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
            File.WriteAllText(Path.Combine(consoles, "SNES.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
            File.WriteAllBytes(Path.Combine(consoles, "SNES.png"), [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0]);
            File.WriteAllText(Path.Combine(consoles, "Fake.svg"), "not an image");
            File.WriteAllText(Path.Combine(temp.FullName, "secret.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
            File.WriteAllText(Path.Combine(developers, "Data East.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");

            var found = RomLogos.Load(temp.FullName, "consoles", new[] { "Master System", "mastersystem", "SNES", "Fake", "../secret", "../../secret", "secret", "Missing" });
            Assert.StartsWith("data:image/svg+xml;base64,", found["Master System"]);
            Assert.StartsWith("data:image/svg+xml;base64,", found["mastersystem"]);
            Assert.StartsWith("data:image/png;base64,", found["SNES"]);
            Assert.False(found.ContainsKey("Fake"));
            Assert.False(found.ContainsKey("../secret"));
            Assert.False(found.ContainsKey("../../secret"));
            Assert.False(found.ContainsKey("secret"));
            Assert.False(found.ContainsKey("Missing"));
            Assert.Empty(RomLogos.Load(temp.FullName, "../consoles", new[] { "SNES" }));
            Assert.Empty(RomLogos.Load(temp.FullName, "developers", new[] { "SNES" }));
            Assert.Single(RomLogos.Load(temp.FullName, "developers", new[] { "Data East" }));
        }
        finally
        {
            Directory.Delete(temp.FullName, recursive: true);
        }
    }
}

public class RomHackTagTests
{
    [Theory]
    [InlineData("[h]", true)]
    [InlineData("[h1]", true)]
    [InlineData("[h2C]", true)]
    [InlineData("[h1+2]", true)]
    [InlineData("(Hack)", true)]
    [InlineData("[HACK by Foo]", true)]
    [InlineData("[T+Eng]", false)]
    [InlineData("[!]", false)]
    [InlineData("[b]", false)]
    [InlineData("[b1]", false)]
    [InlineData("[H1]", false)]
    [InlineData("[t1]", false)]
    [InlineData("(USA)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DetectsHackTags(string? tag, bool hack)
    {
        Assert.Equal(hack, RomTags.IsHackTag(tag));
    }

    [Fact]
    public void IsHackChecksEveryTagAndLeavesCheatAlone()
    {
        Assert.True(RomTags.IsHack(new[] { "(USA)", "[h2C]" }));
        Assert.False(RomTags.IsHack(new[] { "(USA)", "[T+Eng]", "[!]" }));
        Assert.False(RomTags.IsHack(null));
        Assert.False(RomTags.IsCheat(new[] { "[h1]" }));
    }

}

public class RomManagerRouteTests
{
    [Fact]
    public async Task CardsCarryHackFlagAndFiltersListHacksOrCheats()
    {
        var module = Module(Library());
        var all = await Json(module.Games(Req("{}")));
        var items = all.GetProperty("items").EnumerateArray().ToList();
        var mana = Assert.Single(items, item => item.GetProperty("key").GetString() == "p:SNES/Mana" || item.GetProperty("title").GetString() == "Secret of Mana");
        Assert.Equal("best", mana.GetProperty("hack").GetString());
        Assert.False(mana.TryGetProperty("cheat", out _));
        var chrono = Assert.Single(items, item => item.GetProperty("key").GetString() == "m:SNES/Chrono");
        Assert.Equal("some", chrono.GetProperty("cheat").GetString());
        Assert.False(chrono.TryGetProperty("hack", out _));

        var hacks = await Json(module.Games(Req("""{"flag":"hack"}""")));
        Assert.Equal(1, hacks.GetProperty("total").GetInt32());
        Assert.Equal("Secret of Mana", hacks.GetProperty("items")[0].GetProperty("title").GetString());
        var cheats = await Json(module.Games(Req("""{"flag":"cheat"}""")));
        Assert.Equal(1, cheats.GetProperty("total").GetInt32());
        Assert.Equal("m:SNES/Chrono", cheats.GetProperty("items")[0].GetProperty("key").GetString());
        var none = await Json(module.Games(Req("""{"flag":"hack","system":"NES"}""")));
        Assert.Equal(0, none.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task GamesRouteGroupsSearchesAndCapsThePage()
    {
        var module = Module(Library());
        var all = await Json(module.Games(Req("{}")));
        Assert.True(all.GetProperty("ok").GetBoolean());
        Assert.Equal(5, all.GetProperty("total").GetInt32());
        Assert.Contains(all.GetProperty("items").EnumerateArray(), item => item.GetProperty("key").GetString() == "m:SNES/Chrono" && item.GetProperty("title").GetString() == "Chrono Trigger" && item.GetProperty("versions").GetInt32() == 2);

        var chrono = Assert.Single(all.GetProperty("items").EnumerateArray(), item => item.GetProperty("key").GetString() == "m:SNES/Chrono");
        Assert.Equal("some", chrono.GetProperty("cheat").GetString());
        Assert.All(all.GetProperty("items").EnumerateArray().Where(item => item.GetProperty("key").GetString() != "m:SNES/Chrono"), item => Assert.False(item.TryGetProperty("cheat", out _)));

        var search = await Json(module.Games(Req("""{"q":"sonic team"}""")));
        Assert.Equal(0, search.GetProperty("total").GetInt32());
        var sega = await Json(module.Games(Req("""{"q":"SeGa"}""")));
        Assert.Equal("Sonic", Assert.Single(sega.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());

        var page = await Json(module.Games(Req("""{"sort":"title","offset":1,"limit":100}""")));
        Assert.Equal(5, page.GetProperty("total").GetInt32());
        Assert.Equal(4, page.GetProperty("items").GetArrayLength());

        var many = Module(BigLibrary(70));
        var capped = await Json(many.Games(Req("""{"limit":100}""")));
        Assert.Equal(70, capped.GetProperty("total").GetInt32());
        Assert.Equal(60, capped.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task BucketPageKeepsTheDefaultShapeAndStillCapsAtSixty()
    {
        var module = Module(call => call.Uri.AbsolutePath.EndsWith("/index")
            ? Text(HttpStatusCode.OK, RomLibraryTests.NameIndex)
            : Text(HttpStatusCode.OK, "{}"));

        var plain = await Json(module.Games(Req("""{"system":"Arcade"}""")));
        Assert.False(plain.TryGetProperty("buckets", out _));
        Assert.Equal(4, plain.GetProperty("total").GetInt32());
        Assert.Contains(plain.GetProperty("items").EnumerateArray(), item => item.GetProperty("title").GetString() == "1942");
        Assert.DoesNotContain(plain.GetProperty("items").EnumerateArray(), item => item.GetProperty("title").GetString() == "1942a");

        var page = await Json(module.Games(Req("""{"system":"Arcade","bucket":"Q-S","includeBuckets":1,"limit":100}""")));
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal("Q*bert", Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
        var buckets = page.GetProperty("buckets").EnumerateArray().ToList();
        Assert.Equal(6, buckets.Count);
        Assert.Equal("#-D", buckets[0].GetProperty("id").GetString());
        Assert.Equal("# – D", buckets[0].GetProperty("label").GetString());
        Assert.Equal(2, buckets[0].GetProperty("games").GetInt32());
        Assert.Equal(1, buckets.Single(row => row.GetProperty("id").GetString() == "Q-S").GetProperty("games").GetInt32());
        Assert.Equal(0, buckets.Single(row => row.GetProperty("id").GetString() == "T-Z").GetProperty("games").GetInt32());

        var letter = await Json(module.Games(Req("""{"system":"Arcade","letter":"E"}""")));
        Assert.False(letter.TryGetProperty("buckets", out _));
        Assert.Equal("Elevator Action", Assert.Single(letter.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());

        var systems = await Json(module.Systems(Req("{}")));
        var names = systems.GetProperty("nameCheck").EnumerateArray().ToList();
        Assert.Contains(names, row => row.GetProperty("folder").GetString() == "mame" && row.GetProperty("platform").GetString() == "Arcade" && row.GetProperty("match").GetBoolean() == false);
        Assert.Contains(names, row => row.GetProperty("folder").GetString() == "NES" && row.GetProperty("platform").GetString() == "NES" && row.GetProperty("match").GetBoolean());
    }

    [Fact]
    public async Task DevelopersRouteCountsGamesAndGamesRouteFiltersByDeveloper()
    {
        var module = Module(Library());
        var devs = await Json(module.Developers(Req("{}")));
        Assert.True(devs.GetProperty("ok").GetBoolean());
        var list = devs.GetProperty("developers").EnumerateArray().ToList();
        Assert.Equal(new[] { "Nintendo", "Square", "Sonic Team" }, list.Select(row => row.GetProperty("name").GetString()).ToArray());
        var squareRow = list[1];
        Assert.Equal(2, squareRow.GetProperty("games").GetInt32());
        Assert.Equal(3, squareRow.GetProperty("roms").GetInt32());
        Assert.Contains(list, row => row.GetProperty("name").GetString() == "Nintendo" && row.GetProperty("games").GetInt32() == 2);
        Assert.Equal(3, devs.GetProperty("total").GetInt32());

        var top = await Json(module.Developers(Req("""{"min":2}""")));
        Assert.Equal(2, top.GetProperty("developers").GetArrayLength());

        var square = await Json(module.Games(Req("""{"developer":"square","sort":"title-desc"}""")));
        Assert.Equal(2, square.GetProperty("total").GetInt32());
        Assert.Equal(new[] { "Secret of Mana", "Chrono Trigger" }, square.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("title").GetString()).ToArray());
        Assert.All(square.GetProperty("items").EnumerateArray(), item => Assert.Equal("Square", item.GetProperty("developer").GetString()));
        var squareSnes = await Json(module.Games(Req("""{"developer":"Square","q":"mana"}""")));
        Assert.Equal(1, squareSnes.GetProperty("total").GetInt32());
        var none = await Json(module.Games(Req("""{"developer":"Nobody"}""")));
        Assert.Equal(0, none.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task LogosRouteServesDataUrisAndRejectsTraversal()
    {
        var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-logo-route-" + Guid.NewGuid().ToString("N")));
        try
        {
            var romRoot = Path.Combine(temp.FullName, "roms");
            var consoles = Directory.CreateDirectory(Path.Combine(romRoot, ".logos", "consoles")).FullName;
            File.WriteAllText(Path.Combine(consoles, "SNES.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
            File.WriteAllText(Path.Combine(romRoot, "passwd.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
            var plugins = Directory.CreateDirectory(Path.Combine(temp.FullName, "plugins"));
            Directory.CreateDirectory(Path.Combine(plugins.FullName, "rom-manager"));
            var registry = new MoonfinPluginRegistry(new MoonbaseModuleCatalog(), plugins.FullName);
            registry.WriteConfig("rom-manager", JsonSerializer.Serialize(new Dictionary<string, string> { ["romRoot"] = romRoot }));
            var services = new ServiceCollection();
            services.AddSingleton(registry);
            var provider = services.BuildServiceProvider();
            var module = Module(Library());

            var ok = await Json(module.Logos(Req("""{"kind":"consoles","names":["SNES","../passwd","../../roms/passwd","..","NES"]}""", services: provider)));
            Assert.True(ok.GetProperty("ok").GetBoolean());
            var logos = ok.GetProperty("logos").EnumerateObject().ToList();
            var snes = Assert.Single(logos);
            Assert.Equal("SNES", snes.Name);
            var uri = snes.Value.GetString()!;
            Assert.StartsWith("data:image/svg+xml;base64,", uri);
            Assert.Contains("<svg", Encoding.UTF8.GetString(Convert.FromBase64String(uri["data:image/svg+xml;base64,".Length..])));

            var badKind = await Json(module.Logos(Req("""{"kind":"..","names":["passwd"]}""", services: provider)));
            Assert.False(badKind.GetProperty("ok").GetBoolean());
            var upKind = await Json(module.Logos(Req("""{"kind":"../roms","names":["passwd"]}""", services: provider)));
            Assert.False(upKind.GetProperty("ok").GetBoolean());
        }
        finally
        {
            Directory.Delete(temp.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task GameRouteReturnsVersionsDetailsAndRelated()
    {
        var module = Module(Library(game: """{"description":"A classic.","year":1995,"genre":"RPG","developer":"Square","publisher":"Square","rating":4.3,"rating_count":120,"max_players":2,"esrb":"E","lb_url":"https://example.test/chrono"}"""));
        var body = await Json(module.Game(Req("""{"key":"m:SNES/Chrono"}""")));
        var game = body.GetProperty("game");
        Assert.Equal("A classic.", game.GetProperty("description").GetString());
        Assert.Equal(4.3, game.GetProperty("rating").GetDouble());
        Assert.Equal(120, game.GetProperty("ratingCount").GetInt32());
        Assert.Equal("Secret of Mana", Assert.Single(game.GetProperty("related").EnumerateArray()).GetProperty("title").GetString());
        var versions = game.GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal(2, versions.Count);
        var best = Assert.Single(versions, version => version.GetProperty("best").GetBoolean());
        Assert.Equal("USA", best.GetProperty("region").GetString());
        Assert.False(best.TryGetProperty("revision", out _));
        var europe = Assert.Single(versions, version => version.GetProperty("region").GetString() == "Europe");
        Assert.Equal("Rev 1", europe.GetProperty("revision").GetString());
        Assert.Equal("sfc", europe.GetProperty("ext").GetString());
    }

    [Fact]
    public async Task LaunchRejectsUnknownKeysMembersTraversalAndTrash()
    {
        var module = Module(Library());
        var unknown = await Json(module.Launch(Req("""{"key":"missing","p":"NES/Mario"}""")));
        Assert.False(unknown.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown key", unknown.GetProperty("error").GetString());

        var member = await Json(module.Launch(Req("""{"key":"m:SNES/Chrono","p":"NES/Mario"}""")));
        Assert.False(member.GetProperty("ok").GetBoolean());
        Assert.Equal("not a member", member.GetProperty("error").GetString());

        var traversal = await Json(module.Launch(Req("""{"key":"m:SNES/Chrono","p":"SNES/../Chrono"}""")));
        Assert.False(traversal.GetProperty("ok").GetBoolean());
        Assert.Equal("invalid path", traversal.GetProperty("error").GetString());

        var trash = await Json(module.Launch(Req("""{"key":"m:SNES/Chrono","p":"batch/NES/Mario"}""")));
        Assert.False(trash.GetProperty("ok").GetBoolean());
        Assert.Equal("trash path", trash.GetProperty("error").GetString());
    }

    [Fact]
    public async Task LaunchHandsALibraryRomToTheExistingPlayer()
    {
        var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-launch-" + Guid.NewGuid().ToString("N")));
        try
        {
            var romRoot = Path.Combine(temp.FullName, "roms");
            var rom = Path.Combine(romRoot, "SNES", "Chrono", "Chrono Trigger (USA).sfc");
            Directory.CreateDirectory(Path.GetDirectoryName(rom)!);
            File.WriteAllBytes(rom, [1, 2, 3, 4]);

            var library = DispatchProxy.Create<ILibraryManager, FolderLibrary>();
            ((FolderLibrary)(object)library).Folders.Add(new VirtualFolderInfo
            {
                Name = "Retro Games",
                ItemId = "retro-games",
                Locations = new[] { romRoot },
            });
            var games = new GamesService(library);
            var plugins = Directory.CreateDirectory(Path.Combine(temp.FullName, "plugins"));
            Directory.CreateDirectory(Path.Combine(plugins.FullName, "rom-manager"));
            var registry = new MoonfinPluginRegistry(new MoonbaseModuleCatalog(), plugins.FullName);
            registry.WriteConfig("rom-manager", JsonSerializer.Serialize(new Dictionary<string, string> { ["romRoot"] = romRoot }));
            var services = new ServiceCollection();
            services.AddSingleton(registry);
            services.AddSingleton(games);

            var body = await Json(Module(Library()).Launch(Req(
                """{"key":"m:SNES/Chrono","p":"SNES/Chrono"}""",
                admin: false,
                services: services.BuildServiceProvider())));
            Assert.True(body.GetProperty("ok").GetBoolean());
            Assert.True(body.GetProperty("playable").GetBoolean());
            Assert.Equal("retro-games", body.GetProperty("libraryId").GetString());
            Assert.Equal("snes", body.GetProperty("core").GetString());
            Assert.Equal("Chrono Trigger (USA).sfc", body.GetProperty("fileName").GetString());
            Assert.Equal("Chrono Trigger", body.GetProperty("title").GetString());
            Assert.Equal("SNES", body.GetProperty("system").GetString());
            Assert.Equal(Path.GetFullPath(rom), DecodeToken(body.GetProperty("gameId").GetString()!));
            Assert.True(!body.TryGetProperty("reason", out var reason)
                || reason.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || string.IsNullOrEmpty(reason.GetString()));
        }
        finally
        {
            Directory.Delete(temp.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task LaunchDreamcast_ListsTheSystemAndPassesCoreWithoutEmulatorJs()
    {
        var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-launch-dc-" + Guid.NewGuid().ToString("N")));
        try
        {
            var romRoot = Path.Combine(temp.FullName, "roms");
            var rom = Path.Combine(romRoot, "Dreamcast", "Sonic", "Sonic Adventure.gdi");
            Directory.CreateDirectory(Path.GetDirectoryName(rom)!);
            File.WriteAllText(rom, "1\n1 0 4 2352 track01.bin 0\n");
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(rom)!, "track01.bin"), [1, 2, 3]);
            var bios = Path.Combine(romRoot, ".bios", "Dreamcast");
            Directory.CreateDirectory(bios);
            File.WriteAllBytes(Path.Combine(bios, "dc_boot.bin"), [4]);
            File.WriteAllBytes(Path.Combine(bios, "dc_flash.bin"), [5]);

            var library = DispatchProxy.Create<ILibraryManager, FolderLibrary>();
            ((FolderLibrary)(object)library).Folders.Add(new VirtualFolderInfo
            {
                Name = "Retro Games",
                ItemId = "retro-games",
                Locations = new[] { romRoot },
            });
            var games = new GamesService(library);
            var plugins = Directory.CreateDirectory(Path.Combine(temp.FullName, "plugins"));
            Directory.CreateDirectory(Path.Combine(plugins.FullName, "rom-manager"));
            var registry = new MoonfinPluginRegistry(new MoonbaseModuleCatalog(), plugins.FullName);
            registry.WriteConfig("rom-manager", JsonSerializer.Serialize(new Dictionary<string, string> { ["romRoot"] = romRoot }));
            var services = new ServiceCollection();
            services.AddSingleton(registry);
            services.AddSingleton(games);
            var provider = services.BuildServiceProvider();
            const string index = """{"items":[{"p":"Dreamcast/Sonic","s":"Dreamcast","t":"Sonic Adventure","f":"Sonic Adventure.gdi","z":10,"m":1,"mg":0}]}""";
            var module = Module(_ => Text(HttpStatusCode.OK, index));

            var systems = await Json(module.Systems(Req("{}", services: provider)));
            Assert.Equal("Dreamcast", Assert.Single(systems.GetProperty("systems").EnumerateArray()).GetProperty("system").GetString());

            var body = await Json(module.Launch(Req("""{"key":"p:Dreamcast/Sonic","p":"Dreamcast/Sonic"}""", services: provider)));
            Assert.True(body.GetProperty("ok").GetBoolean());
            Assert.False(body.GetProperty("playable").GetBoolean());
            Assert.Equal("dreamcast", body.GetProperty("core").GetString());
            Assert.Equal("Dreamcast", body.GetProperty("system").GetString());
            Assert.Equal("Sonic Adventure.gdi", body.GetProperty("fileName").GetString());
            Assert.Equal("No EmulatorJS core for this system.", body.GetProperty("reason").GetString());
            Assert.False(body.TryGetProperty("biosUrl", out _));
        }
        finally
        {
            Directory.Delete(temp.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task LaunchLooseRom_UsesThePathAsTheFileAndKeepsTheJoinForAFolderNamedLikeAFile()
    {
        var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-launch-loose-" + Guid.NewGuid().ToString("N")));
        try
        {
            var romRoot = Path.Combine(temp.FullName, "roms");
            var loose = Path.Combine(romRoot, "WonderSwan", "Golden Axe (Japan).7z");
            Directory.CreateDirectory(Path.GetDirectoryName(loose)!);
            File.WriteAllBytes(loose, [1, 2, 3, 4]);
            var folderRom = Path.Combine(romRoot, "WonderSwan", "Klonoa (Japan).7z", "Klonoa (Japan).7z");
            Directory.CreateDirectory(Path.GetDirectoryName(folderRom)!);
            File.WriteAllBytes(folderRom, [5, 6, 7, 8]);

            var library = DispatchProxy.Create<ILibraryManager, FolderLibrary>();
            ((FolderLibrary)(object)library).Folders.Add(new VirtualFolderInfo
            {
                Name = "Retro Games",
                ItemId = "retro-games",
                Locations = new[] { romRoot },
            });
            var games = new GamesService(library);
            var plugins = Directory.CreateDirectory(Path.Combine(temp.FullName, "plugins"));
            Directory.CreateDirectory(Path.Combine(plugins.FullName, "rom-manager"));
            var registry = new MoonfinPluginRegistry(new MoonbaseModuleCatalog(), plugins.FullName);
            registry.WriteConfig("rom-manager", JsonSerializer.Serialize(new Dictionary<string, string> { ["romRoot"] = romRoot }));
            var services = new ServiceCollection();
            services.AddSingleton(registry);
            services.AddSingleton(games);
            var provider = services.BuildServiceProvider();
            const string index = """{"items":[{"p":"WonderSwan/Golden Axe (Japan).7z","s":"WonderSwan","t":"Golden Axe","f":"Golden Axe (Japan).7z","z":4,"m":1,"mg":0,"loose":1},{"p":"WonderSwan/Klonoa (Japan).7z","s":"WonderSwan","t":"Klonoa","f":"Klonoa (Japan).7z","z":4,"m":1,"mg":0}]}""";
            var module = Module(_ => Text(HttpStatusCode.OK, index));

            var body = await Json(module.Launch(Req("""{"key":"p:WonderSwan/Golden Axe (Japan).7z","p":"WonderSwan/Golden Axe (Japan).7z"}""", admin: false, services: provider)));
            Assert.True(body.GetProperty("ok").GetBoolean());
            Assert.Equal("retro-games", body.GetProperty("libraryId").GetString());
            Assert.Equal("Golden Axe (Japan).7z", body.GetProperty("fileName").GetString());
            Assert.Equal(Path.GetFullPath(loose), DecodeToken(body.GetProperty("gameId").GetString()!));
            Assert.NotEqual("No game library covers this ROM.", body.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null);

            var folder = await Json(module.Launch(Req("""{"key":"p:WonderSwan/Klonoa (Japan).7z","p":"WonderSwan/Klonoa (Japan).7z"}""", admin: false, services: provider)));
            Assert.True(folder.GetProperty("ok").GetBoolean());
            Assert.Equal(Path.GetFullPath(folderRom), DecodeToken(folder.GetProperty("gameId").GetString()!));
        }
        finally
        {
            Directory.Delete(temp.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task LaunchArcadeBios_PassesNeogeoZipAndRejectsAMissingBios()
    {
        var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-launch-bios-" + Guid.NewGuid().ToString("N")));
        try
        {
            var romRoot = Path.Combine(temp.FullName, "roms");
            var rom = Path.Combine(romRoot, "Arcade", "svc", "svc.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(rom)!);
            using (var zip = System.IO.Compression.ZipFile.Open(rom, System.IO.Compression.ZipArchiveMode.Create))
            using (var stream = zip.CreateEntry("chip.bin").Open())
            {
                stream.Write(new byte[] { 1, 2, 3, 4 });
            }

            var biosDir = Path.Combine(romRoot, ".bios", "Arcade");
            Directory.CreateDirectory(biosDir);
            File.WriteAllBytes(Path.Combine(biosDir, "pgm.zip"), [8]);
            File.WriteAllBytes(Path.Combine(biosDir, "skns.zip"), [7]);

            var library = DispatchProxy.Create<ILibraryManager, FolderLibrary>();
            ((FolderLibrary)(object)library).Folders.Add(new VirtualFolderInfo
            {
                Name = "Retro Games",
                ItemId = "retro-games",
                Locations = new[] { romRoot },
            });
            var games = new GamesService(library);
            var plugins = Directory.CreateDirectory(Path.Combine(temp.FullName, "plugins"));
            Directory.CreateDirectory(Path.Combine(plugins.FullName, "rom-manager"));
            var registry = new MoonfinPluginRegistry(new MoonbaseModuleCatalog(), plugins.FullName);
            registry.WriteConfig("rom-manager", JsonSerializer.Serialize(new Dictionary<string, string> { ["romRoot"] = romRoot }));
            var services = new ServiceCollection();
            services.AddSingleton(registry);
            services.AddSingleton(games);
            var provider = services.BuildServiceProvider();
            const string index = """{"items":[{"p":"Arcade/svc","s":"Arcade","t":"SVC Chaos","f":"svc.zip","z":10,"m":1,"mg":0}]}""";
            var module = Module(_ => Text(HttpStatusCode.OK, index));

            var missing = await Json(module.Launch(Req("""{"key":"p:Arcade/svc","p":"Arcade/svc"}""", services: provider)));
            Assert.True(missing.GetProperty("ok").GetBoolean());
            Assert.False(missing.GetProperty("playable").GetBoolean());
            Assert.Equal("BIOS is missing for this system.", missing.GetProperty("reason").GetString());
            Assert.False(missing.TryGetProperty("biosUrl", out _));

            var neogeo = Path.Combine(biosDir, "neogeo.zip");
            File.WriteAllBytes(neogeo, [9]);
            var body = await Json(module.Launch(Req("""{"key":"p:Arcade/svc","p":"Arcade/svc"}""", services: provider)));
            Assert.True(body.GetProperty("playable").GetBoolean());
            var biosUrl = body.GetProperty("biosUrl").GetString();
            Assert.NotNull(biosUrl);
            Assert.StartsWith("/Moonfin/Games/retro-games/Bios/", biosUrl, StringComparison.Ordinal);
            Assert.EndsWith("/neogeo.zip", biosUrl, StringComparison.Ordinal);
            Assert.DoesNotContain("pgm.zip", biosUrl, StringComparison.Ordinal);
            var token = biosUrl.Split('/')[^2];
            Assert.Equal(Path.GetFullPath(neogeo), DecodeToken(token));
        }
        finally
        {
            Directory.Delete(temp.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task DuplicatesExposeCopyGroupsAndAVersionCount()
    {
        var body = await Json(Module(Library()).Duplicates(Req("{}", admin: true)));
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        Assert.Equal(1, body.GetProperty("versionGroups").GetInt32());
        var members = Assert.Single(body.GetProperty("groups").EnumerateArray()).GetProperty("members").EnumerateArray().ToList();
        Assert.Contains(members, member => member.GetProperty("ks").GetBoolean());
        Assert.Contains(members, member => !member.GetProperty("ks").GetBoolean());
    }

    [Fact]
    public async Task DuplicatesAreAdminOnly()
    {
        var body = await Json(Module(Library()).Duplicates(Req("{}")));
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal("admin only", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DisableThenEnableStillServesRoutes()
    {
        var handler = new RecordingHandler(_ => Text(HttpStatusCode.OK, RomLibraryTests.SampleIndex));
        var module = new RomManagerModule(handler);
        var first = await Json(module.Systems(Req("{}")));
        Assert.True(first.GetProperty("ok").GetBoolean());
        var indexes = handler.Calls.Count(call => call.Uri.AbsolutePath.EndsWith("/index"));
        module.OnStop();
        ((IMoonbaseModule)module).OnStart(new ServiceCollection().BuildServiceProvider());
        var second = await Json(module.Systems(Req("{}")));
        Assert.True(second.GetProperty("ok").GetBoolean());
        Assert.True(handler.Calls.Count(call => call.Uri.AbsolutePath.EndsWith("/index")) > indexes);
    }

    [Fact]
    public async Task IndexIsCachedForSixtySecondsAndSendsTheEtag()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var handler = new RecordingHandler(call =>
        {
            if (call.Uri.AbsolutePath.EndsWith("/index"))
            {
                Assert.Contains("gzip", call.AcceptEncoding, StringComparison.OrdinalIgnoreCase);
                if (call.IfNoneMatch != null)
                {
                    Assert.Contains("v1", call.IfNoneMatch, StringComparison.Ordinal);
                    return Text(HttpStatusCode.NotModified, "");
                }

                var response = Text(HttpStatusCode.OK, RomLibraryTests.SampleIndex);
                response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return response;
            }

            return Text(HttpStatusCode.OK, "{}");
        });
        var module = new RomManagerModule(handler) { Clock = () => now };
        await module.Games(Req("{}"));
        await module.Games(Req("{}"));
        Assert.Single(handler.Calls, call => call.Uri.AbsolutePath.EndsWith("/index"));

        now = now.AddSeconds(61);
        await module.Games(Req("{}"));
        Assert.Equal(2, handler.Calls.Count(call => call.Uri.AbsolutePath.EndsWith("/index")));
        var second = handler.Calls.Last(call => call.Uri.AbsolutePath.EndsWith("/index"));
        Assert.Contains("v1", second.IfNoneMatch);
        var still = await Json(module.Games(Req("{}")));
        Assert.True(still.GetProperty("total").GetInt32() > 0);
    }

    [Fact]
    public async Task BuildingIndexSurfacesTheIndexingMessage()
    {
        var module = Module(_ => Text(HttpStatusCode.Accepted, ""));
        var body = await Json(module.Systems(Req("{}")));
        Assert.True(body.GetProperty("indexing").GetBoolean());
        Assert.Equal(RomChannel.IndexingMessage, body.GetProperty("message").GetString());
        Assert.Equal(0, body.GetProperty("totals").GetProperty("games").GetInt32());
    }

    [Fact]
    public async Task FallbackHostIsUsedOnlyWhenTheDefaultDoesNotAnswer()
    {
        var handler = new RecordingHandler(call =>
        {
            if (call.Uri.Host == "192.168.1.119")
            {
                throw new HttpRequestException("down");
            }

            return Text(HttpStatusCode.OK, """{"meta":{},"items":[]}""");
        });
        var module = new RomManagerModule(handler);
        var body = await Json(module.Games(Req("{}")));
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Contains(handler.Calls, call => call.Uri.Host == "192.168.1.119");
        Assert.Contains(handler.Calls, call => call.Uri.Host == "172.17.0.1");
    }

    [Fact]
    public async Task PluginConfigOverridesTheChannelUrl()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rom-config-" + Guid.NewGuid().ToString("N")));
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "rom-manager"));
            await File.WriteAllTextAsync(Path.Combine(folder.FullName, "config.json"), """{"settings":{"romManagerUrl":"http://10.1.2.3:3002"}}""");
            var catalog = new MoonbaseModuleCatalog();
            catalog.EnsureExternalLoaded(root.FullName);
            var registry = new MoonfinPluginRegistry(catalog);
            Assert.Contains("10.1.2.3", registry.ReadConfig("rom-manager"), StringComparison.Ordinal);
            var services = new ServiceCollection();
            services.AddSingleton(registry);
            var provider = services.BuildServiceProvider();
            var handler = new RecordingHandler(call =>
            {
                Assert.Equal("10.1.2.3", call.Uri.Host);
                return Text(HttpStatusCode.OK, """{"meta":{},"items":[]}""");
            });
            var module = new RomManagerModule(handler);
            var body = await Json(module.Games(Req("{}", services: provider)));
            Assert.True(body.GetProperty("ok").GetBoolean());
            Assert.Equal("10.1.2.3", Assert.Single(handler.Calls).Uri.Host);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task CoversRejectBadPathsAndSkipOversizedImages()
    {
        var handler = new RecordingHandler(call =>
        {
            Assert.DoesNotContain("..", call.Uri.PathAndQuery, StringComparison.Ordinal);
            var width = call.Uri.Query.Contains("w=400", StringComparison.Ordinal) ? 400 : 200;
            if (call.Uri.Query.Contains("big=", StringComparison.Ordinal) || call.Uri.Query.Contains("NES%2FBig", StringComparison.Ordinal) || call.Uri.Query.Contains("NES/Big", StringComparison.Ordinal))
            {
                return Jpeg(new byte[301 * 1024]);
            }

            if (call.Uri.Query.Contains("NES%2FWide", StringComparison.Ordinal) || call.Uri.Query.Contains("NES/Wide", StringComparison.Ordinal))
            {
                return Jpeg(new byte[200 * 1024]);
            }

            var tiny = new byte[32];
            tiny[0] = 0xFF;
            tiny[1] = 0xD8;
            tiny[2] = 0xFF;
            _ = width;
            return Jpeg(tiny);
        });
        var module = new RomManagerModule(handler);
        var body = await Json(module.Covers(Req("""
        {"items":[
          {"p":"../etc/passwd","w":200},
          {"p":"/NES/Mario","w":200},
          {"p":"NES","w":200},
          {"p":"NES/Mario","lb":"9","w":200},
          {"p":"NES/Big","w":200},
          {"p":"NES/Wide","w":400},
          {"p":"batch/NES/Mario","w":200}
        ]}
        """)));
        var covers = body.GetProperty("covers");
        Assert.True(covers.TryGetProperty("NES/Mario", out var mario));
        Assert.StartsWith("data:image/jpeg;base64,", mario.GetString());
        Assert.True(covers.TryGetProperty("batch/NES/Mario", out _));
        Assert.False(covers.TryGetProperty("NES/Big", out _));
        Assert.False(covers.TryGetProperty("NES/Wide", out _));
        Assert.DoesNotContain(handler.Calls, call => call.Uri.PathAndQuery.Contains("..", StringComparison.Ordinal));
        Assert.Contains(handler.Calls, call => call.Uri.Query.Contains("trash=1", StringComparison.Ordinal));
        Assert.Equal(4, handler.Calls.Count);
    }

    [Fact]
    public async Task AdminRoutesRejectEveryoneElse()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("should not be called"));
        var module = new RomManagerModule(handler);
        foreach (var call in new Func<PluginRouteRequest, Task<PluginRouteResult>>[]
        {
            module.Rescan, module.TrashList, module.Deps, module.Trash, module.Restore,
        })
        {
            var body = await Json(call(Req("""{"paths":["NES/Mario"]}""", admin: false)));
            Assert.False(body.GetProperty("ok").GetBoolean());
            Assert.Equal("admin only", body.GetProperty("error").GetString());
        }

        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task TrashRequiresAKnownPathAndPassesAcksThrough()
    {
        var handler = new RecordingHandler(call =>
        {
            if (call.Uri.AbsolutePath.EndsWith("/delete"))
            {
                return Text(HttpStatusCode.Conflict, """{"needs_ack":true,"warnings":["needed by Pac-Man"]}""");
            }

            return Text(HttpStatusCode.OK, RomLibraryTests.SampleIndex);
        });
        var module = new RomManagerModule(handler);
        var unknown = await Json(module.Trash(Req("""{"paths":["NES/Missing"]}""", admin: true)));
        Assert.Equal("unknown path", unknown.GetProperty("error").GetString());
        Assert.DoesNotContain(handler.Calls, call => call.Uri.AbsolutePath.EndsWith("/delete"));

        var invalid = await Json(module.Trash(Req("""{"paths":["../NES/Mario"]}""", admin: true)));
        Assert.Equal("invalid path", invalid.GetProperty("error").GetString());

        var tooMany = string.Join(",", Enumerable.Range(0, 51).Select(i => $"\"NES/Game{i}\""));
        var limited = await Json(module.Trash(Req("{\"paths\":[" + tooMany + "]}", admin: true)));
        Assert.Equal("too many paths", limited.GetProperty("error").GetString());

        var ack = await Json(module.Trash(Req("""{"paths":["NES/Mario"],"ackDeps":false}""", admin: true)));
        Assert.False(ack.GetProperty("ok").GetBoolean());
        Assert.True(ack.GetProperty("needsAck").GetBoolean());
        Assert.Equal("needs_ack", ack.GetProperty("error").GetString());
        Assert.Equal("needed by Pac-Man", ack.GetProperty("warnings")[0].GetString());
        Assert.Contains("\"ack_deps\":false", handler.Calls.Last(call => call.Uri.AbsolutePath.EndsWith("/delete")).Body);
    }

    [Fact]
    public async Task SuccessfulTrashAndRestoreInvalidateTheCacheAndAreLogged()
    {
        var logs = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(builder => builder.AddProvider(new ListLog(logs))));
        var provider = services.BuildServiceProvider();
        var indexHits = 0;
        var handler = new RecordingHandler(call =>
        {
            if (call.Uri.AbsolutePath.EndsWith("/index"))
            {
                indexHits++;
                return Text(HttpStatusCode.OK, RomLibraryTests.SampleIndex);
            }

            return Text(HttpStatusCode.OK, """{"ok":true}""");
        });
        var module = new RomManagerModule(handler);
        await module.Games(Req("{}", services: provider));
        var trashed = await Json(module.Trash(Req("""{"paths":["NES/Mario"],"ackDeps":true}""", admin: true, services: provider)));
        Assert.True(trashed.GetProperty("ok").GetBoolean());
        await module.Games(Req("{}", services: provider));
        Assert.Equal(2, indexHits);
        Assert.Contains(logs, line => line.Contains("trash", StringComparison.Ordinal) && line.Contains("NES/Mario", StringComparison.Ordinal) && line.Contains("user", StringComparison.Ordinal));

        var restored = await Json(module.Restore(Req("""{"paths":["batch/NES/Mario"]}""", admin: true, services: provider)));
        Assert.True(restored.GetProperty("ok").GetBoolean());
        Assert.Contains(logs, line => line.Contains("restore", StringComparison.Ordinal) && line.Contains("batch/NES/Mario", StringComparison.Ordinal));
        Assert.Contains(handler.Calls, call => call.Uri.AbsolutePath.EndsWith("/delete") && call.Body != null && call.Body.Contains("\"ack_deps\":true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TrashDropsDuplicatePaths()
    {
        var handler = new RecordingHandler(call =>
            call.Uri.AbsolutePath.EndsWith("/index")
                ? Text(HttpStatusCode.OK, RomLibraryTests.SampleIndex)
                : Text(HttpStatusCode.OK, """{"ok":true}"""));
        var module = new RomManagerModule(handler);
        var trashed = await Json(module.Trash(Req("""{"paths":["NES/Mario","NES/Mario"],"ackDeps":true}""", admin: true)));
        Assert.Equal(1, trashed.GetProperty("moved").GetInt32());
        var body = handler.Calls.Last(call => call.Uri.AbsolutePath.EndsWith("/delete")).Body ?? string.Empty;
        Assert.Equal(1, body.Split("NES/Mario", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task UnexpectedFailuresStayInsideAnOkFalseBody()
    {
        var logs = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(builder => builder.AddProvider(new ListLog(logs))));
        var module = new RomManagerModule(new RecordingHandler(_ => throw new InvalidOperationException("boom")));
        var body = await Json(module.Games(Req("{}", services: services.BuildServiceProvider())));
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal("internal error", body.GetProperty("error").GetString());
        Assert.DoesNotContain("boom", body.GetRawText(), StringComparison.Ordinal);
        Assert.Contains(logs, line => line.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SystemsKeepsOnlyCountsStateAndBuiltTime()
    {
        var handler = new RecordingHandler(call =>
            call.Uri.AbsolutePath.EndsWith("/status")
                ? Text(HttpStatusCode.OK, """{"state":"ready","built":"2026-01-01T00:00:00Z","token":"secret"}""")
                : Text(HttpStatusCode.OK, """{"meta":{"counts":{"games":2,"roms":3},"state":"ready","token":"secret","built":true},"items":[]}"""));
        var body = await Json(new RomManagerModule(handler).Systems(Req("{}")));
        var meta = body.GetProperty("meta");
        Assert.True(meta.GetProperty("built").GetBoolean());
        Assert.Equal("ready", meta.GetProperty("state").GetString());
        Assert.Equal(2, meta.GetProperty("counts").GetProperty("games").GetInt32());
        Assert.False(meta.TryGetProperty("token", out _));
        var status = body.GetProperty("status");
        Assert.Equal("ready", status.GetProperty("state").GetString());
        Assert.Equal("2026-01-01T00:00:00Z", status.GetProperty("built").GetString());
        Assert.False(status.TryGetProperty("token", out _));
    }

    [Fact]
    public async Task AFailedCoverIsSkippedInsteadOfFailingThePage()
    {
        var handler = new RecordingHandler(call =>
        {
            if (call.Uri.Query.Contains("NES%2FMario", StringComparison.Ordinal) || call.Uri.Query.Contains("NES/Mario", StringComparison.Ordinal))
            {
                throw new HttpRequestException("down");
            }

            if (call.Uri.Query.Contains("Huge", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new LengthOnlyContent(RomChannel.MaxImageBytes + 1),
                };
                return response;
            }

            return Jpeg(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
        });
        LengthOnlyContent.WasRead = false;
        var body = await Json(new RomManagerModule(handler).Covers(Req("""
        {"items":[{"p":"NES/Mario","w":200},{"p":"NES/Other","w":200},{"p":"NES/Huge","w":200}]}
        """)));
        Assert.True(body.GetProperty("ok").GetBoolean());
        var covers = body.GetProperty("covers");
        Assert.False(covers.TryGetProperty("NES/Mario", out _));
        Assert.False(covers.TryGetProperty("NES/Huge", out _));
        Assert.True(covers.TryGetProperty("NES/Other", out _));
        Assert.False(LengthOnlyContent.WasRead);
    }

    [Fact]
    public async Task NoRouteCallsTheForbiddenChannelPaths()
    {
        var handler = new RecordingHandler(call =>
        {
            if (call.Uri.AbsolutePath.EndsWith("/index"))
            {
                return Text(HttpStatusCode.OK, RomLibraryTests.SampleIndex);
            }

            if (call.Uri.AbsolutePath.EndsWith("/thumb"))
            {
                return Jpeg(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            }

            if (call.Uri.AbsolutePath.EndsWith("/game"))
            {
                return Text(HttpStatusCode.OK, """{"description":"Hi"}""");
            }

            return Text(HttpStatusCode.OK, """{"items":[],"bytes":0,"warnings":[]}""");
        });
        var module = new RomManagerModule(handler);
        var admin = true;
        await module.Systems(Req("{}"));
        await module.Games(Req("{}"));
        await module.Game(Req("""{"key":"p:NES/Mario"}"""));
        await module.Covers(Req("""{"items":[{"p":"NES/Mario","w":200}]}"""));
        await module.Duplicates(Req("{}", admin: admin));
        await module.Rescan(Req("{}", admin: admin));
        await module.TrashList(Req("{}", admin: admin));
        await module.Deps(Req("""{"paths":["NES/Mario"]}""", admin: admin));
        await module.Trash(Req("""{"paths":["NES/Mario"],"ackDeps":true}""", admin: admin));
        await module.Restore(Req("""{"paths":["batch/NES/Mario"]}""", admin: admin));

        Assert.NotEmpty(handler.Calls);
        Assert.All(handler.Calls, call =>
        {
            Assert.DoesNotContain("empty", call.Uri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("arcade-refresh", call.Uri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(call.Uri.AbsolutePath, RomChannel.AllowedPaths);
        });
        Assert.DoesNotContain(RomChannel.AllowedPaths, path => path.Contains("empty", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SourceDoesNotNameForbiddenChannelRoutes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "plugins", "rom-manager", "plugin.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var root = Path.Combine(dir!.FullName, "plugins", "rom-manager");
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            Assert.DoesNotContain("romman/empty", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("arcade-refresh", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string DecodeToken(string token)
    {
        var padded = token.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static RomManagerModule Module(Func<RecordedCall, HttpResponseMessage> handle) => new(new RecordingHandler(handle));

    private static Func<RecordedCall, HttpResponseMessage> Library(string? game = null) => call =>
    {
        if (call.Uri.AbsolutePath.EndsWith("/game"))
        {
            return Text(HttpStatusCode.OK, game ?? """{"description":"A classic."}""");
        }

        if (call.Uri.AbsolutePath.EndsWith("/status"))
        {
            return Text(HttpStatusCode.OK, """{"state":"ready"}""");
        }

        return Text(HttpStatusCode.OK, RomLibraryTests.SampleIndex);
    };

    private static Func<RecordedCall, HttpResponseMessage> BigLibrary(int count)
    {
        var builder = new StringBuilder();
        builder.Append("{\"items\":[");
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"p\":\"NES/Game").Append(i).Append("\",\"s\":\"NES\",\"t\":\"Game ").Append(i).Append("\",\"f\":\"g.nes\",\"z\":1,\"m\":").Append(i).Append(",\"mg\":0}");
        }

        builder.Append("]}");
        var json = builder.ToString();
        return _ => Text(HttpStatusCode.OK, json);
    }

    private static PluginRouteRequest Req(string json, bool admin = false, IServiceProvider? services = null) => new()
    {
        Method = "POST",
        Body = json,
        IsAdmin = admin,
        UserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        Services = services,
    };

    private static async Task<JsonElement> Json(Task<PluginRouteResult> resultTask)
    {
        var result = await resultTask;
        Assert.Equal(200, result.StatusCode);
        using var doc = JsonDocument.Parse(result.Body);
        return doc.RootElement.Clone();
    }

    private static HttpResponseMessage Text(HttpStatusCode status, string body)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return response;
    }

    private static HttpResponseMessage Jpeg(byte[] bytes)
    {
        if (bytes.Length >= 3)
        {
            bytes[0] = 0xFF;
            bytes[1] = 0xD8;
            bytes[2] = 0xFF;
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return response;
    }

    private sealed class LengthOnlyContent : HttpContent
    {
        public static bool WasRead;

        public LengthOnlyContent(long length)
        {
            Headers.ContentLength = length;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasRead = true;
            throw new InvalidOperationException("cover body was downloaded");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Headers.ContentLength ?? 0;
            return true;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<RecordedCall, HttpResponseMessage> _handle;

        public RecordingHandler(Func<RecordedCall, HttpResponseMessage> handle) => _handle = handle;

        public List<RecordedCall> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var call = new RecordedCall(
                request.RequestUri!,
                request.Headers.AcceptEncoding.ToString(),
                request.Headers.TryGetValues("If-None-Match", out var tags) ? string.Join(",", tags) : null,
                body);
            lock (Calls)
            {
                Calls.Add(call);
            }

            return _handle(call);
        }
    }

    private sealed record RecordedCall(Uri Uri, string AcceptEncoding, string? IfNoneMatch, string? Body);

    private sealed class ListLog : ILoggerProvider, ILogger
    {
        private readonly List<string> _lines;

        public ListLog(List<string> lines) => _lines = lines;

        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose()
        {
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (exception != null)
            {
                text = text + " " + exception.Message;
            }

            _lines.Add(text);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}

public class FolderLibrary : DispatchProxy
{
    public List<VirtualFolderInfo> Folders { get; } = new();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "GetVirtualFolders")
        {
            return Folders;
        }

        if (targetMethod == null || targetMethod.ReturnType == typeof(void))
        {
            return null;
        }

        return targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }
}
