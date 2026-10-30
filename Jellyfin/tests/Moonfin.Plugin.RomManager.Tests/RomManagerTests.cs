using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moonfin.Plugins.RomManager;
using Moonfin.Server.Plugins;
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

    internal const string SampleIndex = """
    {
      "meta": {"built": true},
      "items": [
        {"p":"SNES/Chrono-eu","s":"SNES","t":"Chrono Trigger EU","f":"Chrono Trigger (Europe) (Rev 1).sfc","z":50,"y":1995,"tg":["(Europe)","(Rev 1)"],"c":1,"ge":"RPG","pub":"Square","dev":"Square","m":40,"sg":0,"ks":0,"mg":7,"km":0},
        {"p":"SNES/Chrono","s":"SNES","t":"Chrono Trigger","f":"Chrono Trigger (USA).sfc","z":1000,"y":1995,"tg":["(USA)","[!]"],"c":2,"lb":"123","ge":"RPG","pub":"Square","dev":"Square","m":100,"sg":0,"ks":0,"mg":7,"km":1},
        {"p":"NES/Mario","s":"NES","t":"Super Mario Bros.","f":"Super Mario Bros. (World).nes","z":400,"y":1985,"tg":["(World)"],"c":1,"ge":"Platform","pub":"Nintendo","dev":"Nintendo","m":900,"sg":3,"ks":1,"mg":0,"km":0},
        {"p":"NES/Mario-copy","s":"NES","t":"Super Mario Bros.","f":"Super Mario Bros. (World) (copy).nes","z":400,"y":1985,"tg":["(World)"],"c":0,"ge":"Platform","pub":"Nintendo","dev":"Nintendo","m":800,"sg":3,"ks":0,"mg":0,"km":0},
        {"p":"Genesis/Sonic","s":"Genesis","t":"Sonic","f":"Sonic (USA).md","z":20,"y":1991,"tg":["(USA)"],"c":0,"ge":"Platform","pub":"Sega","dev":"Sonic Team","m":10,"sg":0,"ks":0,"mg":0,"km":0},
        {"p":"SNES/Mana","s":"SNES","t":"Secret of Mana","f":"Secret of Mana (USA).sfc","z":10,"y":1993,"tg":["(USA)"],"c":0,"ge":"RPG","pub":"Square","dev":"Square","m":5,"sg":0,"ks":0,"mg":0,"km":0}
      ]
    }
    """;
}

public class RomManagerRouteTests
{
    [Fact]
    public async Task GamesRouteGroupsSearchesAndCapsThePage()
    {
        var module = Module(Library());
        var all = await Json(module.Games(Req("{}")));
        Assert.True(all.GetProperty("ok").GetBoolean());
        Assert.Equal(5, all.GetProperty("total").GetInt32());
        Assert.Contains(all.GetProperty("items").EnumerateArray(), item => item.GetProperty("key").GetString() == "m:SNES/Chrono" && item.GetProperty("title").GetString() == "Chrono Trigger" && item.GetProperty("versions").GetInt32() == 2);

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
    public async Task DuplicatesExposeCopyGroupsAndAVersionCount()
    {
        var body = await Json(Module(Library()).Duplicates(Req("{}")));
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        Assert.Equal(1, body.GetProperty("versionGroups").GetInt32());
        var members = Assert.Single(body.GetProperty("groups").EnumerateArray()).GetProperty("members").EnumerateArray().ToList();
        Assert.Contains(members, member => member.GetProperty("ks").GetBoolean());
        Assert.Contains(members, member => !member.GetProperty("ks").GetBoolean());
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
        await module.Duplicates(Req("{}"));
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
                || file.Contains($"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            Assert.DoesNotContain("romman/empty", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("arcade-refresh", text, StringComparison.OrdinalIgnoreCase);
        }
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
            Calls.Add(call);
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
            _lines.Add(formatter(state, exception));
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
