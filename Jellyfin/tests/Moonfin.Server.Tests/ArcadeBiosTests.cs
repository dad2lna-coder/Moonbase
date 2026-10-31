using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Model.Entities;
using Moonfin.Server.Api;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public class ArcadeBiosTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "moonfin-arcade-bios-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Catalog_UsesParentChainAndDefaultsNeoGeoToNeogeoZip()
    {
        Directory.CreateDirectory(_root);
        var dat = Path.Combine(_root, "fbneo.dat");
        File.WriteAllText(
            dat,
            """
            <datafile>
              <game name="neogeo" isbios="yes"></game>
              <game name="svc" romof="neogeo" sourcefile="neogeo/d_neogeo.cpp"></game>
              <game name="svchack" romof="svc"></game>
              <game name="plainneo" sourcefile="neogeo/d_neogeo.cpp"></game>
              <game name="kov" romof="pgm" sourcefile="pgm/d_pgm.cpp"></game>
              <game name="cyvern" romof="skns" sourcefile="pst90s/d_suprnova.cpp"></game>
              <game name="pacman"></game>
            </datafile>
            """);

        var catalog = ArcadeBiosCatalog.Load(dat);

        Assert.Equal("neogeo.zip", Resolve(catalog, "svc"));
        Assert.Equal("neogeo.zip", Resolve(catalog, "svchack"));
        Assert.Equal("neogeo.zip", Resolve(catalog, "plainneo"));
        Assert.Equal("pgm.zip", Resolve(catalog, "kov"));
        Assert.Equal("skns.zip", Resolve(catalog, "cyvern"));
        Assert.True(catalog.TryResolve("pacman", out var pacman));
        Assert.Null(pacman);
        Assert.False(catalog.TryResolve("not-in-dat", out _));
        Assert.Equal("neogeo.zip", ArcadeBiosCatalog.FallbackZip("svc"));
        Assert.Null(ArcadeBiosCatalog.FallbackZip("pacman"));
    }

    [Fact]
    public async Task HiddenArcadeBios_SvcUsesNeogeoAndBiosZipsStayOutOfTheGameList()
    {
        var library = Path.Combine(_root, "Retro Games");
        var svc = Path.Combine(library, "Arcade", "svc", "svc.zip");
        var pacman = Path.Combine(library, "Arcade", "Pac-Man", "pacman.zip");
        var kov = Path.Combine(library, "Arcade", "kov", "kov.zip");
        WriteZip(svc);
        WriteZip(pacman);
        WriteZip(kov);
        var biosDir = Path.Combine(library, ".bios", "Arcade");
        Directory.CreateDirectory(biosDir);
        var neogeo = Path.Combine(biosDir, "neogeo.zip");
        File.WriteAllBytes(neogeo, [9, 9]);
        File.WriteAllBytes(Path.Combine(biosDir, "pgm.zip"), [8]);
        File.WriteAllBytes(Path.Combine(biosDir, "skns.zip"), [7]);
        var mario = Path.Combine(library, "NES", "Mario", "Mario.nes");
        Directory.CreateDirectory(Path.GetDirectoryName(mario)!);
        File.WriteAllBytes(mario, [1]);
        var fdsDir = Path.Combine(library, ".bios", "NES");
        Directory.CreateDirectory(fdsDir);
        var fds = Path.Combine(fdsDir, "fdsbios.zip");
        File.WriteAllBytes(fds, [2]);
        var ridge = Path.Combine(library, "PSX", "Ridge", "ridge.pbp");
        Directory.CreateDirectory(Path.GetDirectoryName(ridge)!);
        File.WriteAllBytes(ridge, [3]);
        File.WriteAllBytes(Path.Combine(library, "PSX", "scph1001.bin"), [4]);

        var service = new GamesService(new FakeLibraryManager(
        [
            new VirtualFolderInfo
            {
                Name = "Retro Games",
                ItemId = "lib-1",
                Locations = [library],
            },
        ]));

        var systems = service.GetSystems("lib-1");
        Assert.DoesNotContain(systems, system => system.Name.StartsWith('.'));
        Assert.Contains(systems, system => system.Name == "Arcade");

        var games = service.GetGames("lib-1", null);
        var names = games.Select(game => game.FileName).ToList();
        Assert.Contains("svc.zip", names);
        Assert.Contains("pacman.zip", names);
        Assert.Contains("kov.zip", names);
        Assert.DoesNotContain("neogeo.zip", names);
        Assert.DoesNotContain("pgm.zip", names);
        Assert.DoesNotContain("skns.zip", names);
        Assert.DoesNotContain("fdsbios.zip", names);

        var svcDetail = await service.GetGameAsync("lib-1", GamePathResolver.EncodeToken(Path.GetFullPath(svc)), null, CancellationToken.None);
        var svcBios = Assert.Single(svcDetail!.Bios);
        Assert.Equal("neogeo.zip", svcBios.FileName);
        Assert.EndsWith("/neogeo.zip", svcBios.Url, StringComparison.Ordinal);
        Assert.Contains("/Bios/", svcBios.Url, StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(neogeo), Decode(svcBios.Id));

        var pacmanDetail = await service.GetGameAsync("lib-1", GamePathResolver.EncodeToken(Path.GetFullPath(pacman)), null, CancellationToken.None);
        Assert.Empty(pacmanDetail!.Bios);

        var kovDetail = await service.GetGameAsync("lib-1", GamePathResolver.EncodeToken(Path.GetFullPath(kov)), null, CancellationToken.None);
        Assert.Equal("pgm.zip", Assert.Single(kovDetail!.Bios).FileName);

        var marioDetail = await service.GetGameAsync("lib-1", GamePathResolver.EncodeToken(Path.GetFullPath(mario)), null, CancellationToken.None);
        Assert.Equal("fdsbios.zip", Assert.Single(marioDetail!.Bios).FileName);

        var ridgeDetail = await service.GetGameAsync("lib-1", GamePathResolver.EncodeToken(Path.GetFullPath(ridge)), null, CancellationToken.None);
        Assert.Equal("scph1001.bin", Assert.Single(ridgeDetail!.Bios).FileName);
    }

    [Fact]
    public void BiosFilenameRoute_ReturnsNeogeoZipAndKeepsTheTokenRoute()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "NEOGEO.ZIP");
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.Equal("neogeo.zip", GamesController.BiosRouteFileName(path, "neogeo.zip"));
        Assert.Equal("neogeo.zip", GamesController.BiosRouteFileName(path, null));
        Assert.Null(GamesController.BiosRouteFileName(path, "pgm.zip"));

        var method = typeof(GamesController).GetMethod(nameof(GamesController.GetBios));
        Assert.NotNull(method);
        var gets = method!.GetCustomAttributes<HttpGetAttribute>().Select(attribute => attribute.Template).ToList();
        var heads = method.GetCustomAttributes<HttpHeadAttribute>().Select(attribute => attribute.Template).ToList();
        Assert.Contains("{libraryId}/Bios/{token}", gets);
        Assert.Contains("{libraryId}/Bios/{token}/{fileName}", gets);
        Assert.Contains("{libraryId}/Bios/{token}", heads);
        Assert.Contains("{libraryId}/Bios/{token}/{fileName}", heads);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string? Resolve(ArcadeBiosCatalog catalog, string romset)
    {
        Assert.True(catalog.TryResolve(romset, out var zip));
        return zip;
    }

    private static void WriteZip(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        using var stream = zip.CreateEntry("chip.bin").Open();
        stream.Write(new byte[] { 1, 2, 3, 4 });
    }

    private static string Decode(string token)
    {
        var padded = token.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }
}
