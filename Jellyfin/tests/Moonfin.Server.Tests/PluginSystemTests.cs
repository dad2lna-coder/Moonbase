using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Moonfin.Server.Api;
using Moonfin.Server.Plugins;
using Xunit;

namespace Moonfin.Server.Tests;

public class PluginCapabilityResolverTests
{
    [Fact]
    public void UnmetRequiresAreDormantAndNotAnError()
    {
        var status = ResolveOne(
            """
            {"id":"hello","name":"Hello","version":"1.0.0","description":"","provides":["hello.homeRow"],"requires":["tmdb"],"slots":["homeRow"],"server":"HelloModule","minHostApi":1}
            """,
            servers: Provides("hello", "hello.homeRow"));

        Assert.False(status.Active);
        Assert.True(status.Enabled);
        Assert.NotNull(status.DormantReason);
        Assert.Contains("tmdb", status.DormantReason, StringComparison.Ordinal);
        Assert.Empty(status.EffectiveProvides);
        Assert.False(PluginCapabilityResolver.ToPingFlags(new[] { status })["hello.homeRow"]);
    }

    [Fact]
    public void AFlagTurnsOnOnlyWhenTheServerModuleIsActive()
    {
        var manifest = Must(
            """
            {"id":"hello","name":"Hello","version":"1.0.0","description":"","provides":["hello.homeRow"],"requires":[],"slots":["homeRow"],"server":"HelloModule","minHostApi":1}
            """);

        var dormant = PluginCapabilityResolver.Resolve(
            new[] { manifest },
            new Dictionary<string, IReadOnlyCollection<string>>(),
            new HashSet<string>(),
            new HashSet<string>(),
            new HashSet<string>(),
            1);
        Assert.False(PluginCapabilityResolver.ToPingFlags(dormant)["hello.homeRow"]);
        Assert.Contains("Restart", Assert.Single(dormant).DormantReason, StringComparison.Ordinal);

        var mismatched = PluginCapabilityResolver.Resolve(
            new[] { manifest },
            Provides("hello", "something.else"),
            new HashSet<string>(),
            new HashSet<string>(),
            new HashSet<string>(),
            1);
        Assert.True(Assert.Single(mismatched).Active);
        Assert.False(PluginCapabilityResolver.ToPingFlags(mismatched)["hello.homeRow"]);

        var active = PluginCapabilityResolver.Resolve(
            new[] { manifest },
            Provides("hello", "hello.homeRow"),
            new HashSet<string>(),
            new HashSet<string>(),
            new HashSet<string>(),
            1);
        Assert.True(Assert.Single(active).Active);
        Assert.True(PluginCapabilityResolver.ToPingFlags(active)["hello.homeRow"]);
    }

    [Fact]
    public void AClientOnlyPluginCannotFakeAFlag()
    {
        var status = ResolveOne(
            """
            {"id":"shelf","name":"Shelf","version":"1.0.0","description":"","provides":["shelf.rows"],"slots":["homeRow"],"minHostApi":1}
            """);

        Assert.True(status.Active);
        Assert.Null(status.DormantReason);
        Assert.False(status.ServerLoaded);
        Assert.False(PluginCapabilityResolver.ToPingFlags(new[] { status })["shelf.rows"]);
    }

    [Fact]
    public void ADependentPluginActivatesAfterItsProvider()
    {
        var provider = Must(
            """
            {"id":"alpha","name":"Alpha","version":"1","description":"","provides":["alpha.ready"],"slots":["events"],"server":"AlphaModule","minHostApi":1}
            """);
        var dependent = Must(
            """
            {"id":"beta","name":"Beta","version":"1","description":"","provides":["beta.ready"],"requires":["alpha.ready"],"slots":["homeRow"],"server":"BetaModule","minHostApi":1}
            """);
        var statuses = PluginCapabilityResolver.Resolve(
            new[] { dependent, provider },
            new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["alpha"] = new[] { "alpha.ready" },
                ["beta"] = new[] { "beta.ready" },
            },
            new HashSet<string>(),
            new HashSet<string>(),
            new HashSet<string>(),
            1);

        Assert.All(statuses, status => Assert.True(status.Active));
        var flags = PluginCapabilityResolver.ToPingFlags(statuses);
        Assert.True(flags["alpha.ready"]);
        Assert.True(flags["beta.ready"]);
    }

    [Fact]
    public void CircularRequirementsStayDormant()
    {
        var left = Must(
            """
            {"id":"left","name":"Left","version":"1","description":"","provides":["left.flag"],"requires":["right.flag"],"slots":["events"],"server":"LeftModule","minHostApi":1}
            """);
        var right = Must(
            """
            {"id":"right","name":"Right","version":"1","description":"","provides":["right.flag"],"requires":["left.flag"],"slots":["events"],"server":"RightModule","minHostApi":1}
            """);
        var statuses = PluginCapabilityResolver.Resolve(
            new[] { left, right },
            Provides(("left", "left.flag"), ("right", "right.flag")),
            new HashSet<string>(),
            new HashSet<string>(),
            new HashSet<string>(),
            1);

        Assert.All(statuses, status =>
        {
            Assert.False(status.Active);
            Assert.NotNull(status.DormantReason);
        });
        var flags = PluginCapabilityResolver.ToPingFlags(statuses);
        Assert.False(flags["left.flag"]);
        Assert.False(flags["right.flag"]);
    }

    [Fact]
    public void DisabledAndNewerHostApiDoNotActivate()
    {
        var json =
            """
            {"id":"hello","name":"Hello","version":"1.0.0","description":"","provides":["hello.homeRow"],"slots":["homeRow"],"server":"HelloModule","minHostApi":1}
            """;
        var disabled = ResolveOne(json, Provides("hello", "hello.homeRow"), disabled: new HashSet<string> { "hello" });
        Assert.False(disabled.Enabled);
        Assert.False(disabled.Active);
        Assert.Null(disabled.DormantReason);

        var tooNew = Must(
            """
            {"id":"hello","name":"Hello","version":"1.0.0","description":"","provides":["hello.homeRow"],"slots":["homeRow"],"server":"HelloModule","minHostApi":9}
            """);
        var status = Assert.Single(PluginCapabilityResolver.Resolve(
            new[] { tooNew },
            Provides("hello", "hello.homeRow"),
            new HashSet<string>(),
            new HashSet<string>(),
            new HashSet<string>(),
            MoonbasePlugins.HostApi));
        Assert.False(status.Active);
        Assert.Contains("host API", status.DormantReason, StringComparison.Ordinal);
    }

    private static PluginCapabilityResolver.PluginStatus ResolveOne(
        string json,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? servers = null,
        IReadOnlySet<string>? disabled = null)
    {
        return Assert.Single(PluginCapabilityResolver.Resolve(
            new[] { Must(json) },
            servers ?? new Dictionary<string, IReadOnlyCollection<string>>(),
            new HashSet<string>(),
            disabled ?? new HashSet<string>(),
            new HashSet<string>(),
            MoonbasePlugins.HostApi));
    }

    private static PluginManifest Must(string json)
    {
        var manifest = PluginManifest.TryParse(json, out var error);
        Assert.Null(error);
        Assert.NotNull(manifest);
        return manifest;
    }

    private static Dictionary<string, IReadOnlyCollection<string>> Provides(string id, params string[] flags) =>
        new(StringComparer.OrdinalIgnoreCase) { [id] = flags };

    private static Dictionary<string, IReadOnlyCollection<string>> Provides(params (string Id, string Flag)[] pairs)
    {
        var map = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs)
        {
            map[pair.Id] = new[] { pair.Flag };
        }

        return map;
    }
}

public class PluginZipInstallerTests
{
    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("foo/../../outside.txt")]
    [InlineData("C:/Windows/system.ini")]
    public void UnsafePathsAreRejected(string entryName)
    {
        Assert.False(PluginZipInstaller.EntryIsSafe(entryName));
    }

    [Fact]
    public void ZipSlipWritesNothing()
    {
        using var zip = MakeZip(("plugin.json", PluginJson()), ("../outside.txt", "nope"));
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-zip-" + Guid.NewGuid().ToString("N")));
        try
        {
            var result = PluginZipInstaller.Install(zip, root.FullName);
            Assert.False(result.Ok);
            Assert.Contains("unsafe", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root.FullName));
            Assert.False(File.Exists(Path.Combine(Directory.GetParent(root.FullName)!.FullName, "outside.txt")));
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public void ASafeZipInstallsAndASecondInstallKeepsConfig()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-zip-" + Guid.NewGuid().ToString("N")));
        try
        {
            using var first = MakeZip(
                ("plugin.json", PluginJson()),
                ("client/index.html", "<p>hi</p>"));
            var installed = PluginZipInstaller.Install(first, root.FullName);
            Assert.True(installed.Ok);
            Assert.Equal("hello", installed.Id);
            Assert.True(installed.RestartRequired);
            var folder = Path.Combine(root.FullName, "hello");
            Assert.True(File.Exists(Path.Combine(folder, "client", "index.html")));

            File.WriteAllText(Path.Combine(folder, "config.json"), """{"secrets":{"apiKey":"keep-me"}}""");
            Directory.CreateDirectory(Path.Combine(folder, "data"));
            File.WriteAllText(Path.Combine(folder, "data", "note.txt"), "user");

            using var second = MakeZip(
                ("plugin.json", PluginJson()),
                ("client/index.html", "<p>updated</p>"),
                ("config.json", """{"secrets":{"apiKey":"replaced"}}"""));
            var updated = PluginZipInstaller.Install(second, root.FullName);
            Assert.True(updated.Ok);
            Assert.Equal("keep-me", File.ReadAllText(Path.Combine(folder, "config.json")).Contains("keep-me", StringComparison.Ordinal) ? "keep-me" : "lost");
            Assert.Equal("user", File.ReadAllText(Path.Combine(folder, "data", "note.txt")));
            Assert.Contains("updated", File.ReadAllText(Path.Combine(folder, "client", "index.html")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public void ASingleWrapperFolderIsStripped()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-zip-" + Guid.NewGuid().ToString("N")));
        try
        {
            using var zip = MakeZip(
                ("Moonfin.Plugin.Hello/plugin.json", PluginJson()),
                ("Moonfin.Plugin.Hello/client/index.html", "<p>nested</p>"));
            var installed = PluginZipInstaller.Install(zip, root.FullName);
            Assert.True(installed.Ok);
            Assert.Equal("hello", installed.Id);
            Assert.Contains("nested", File.ReadAllText(Path.Combine(root.FullName, "hello", "client", "index.html")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public void OversizedEntriesAreRejected()
    {
        using var zip = MakeZip(("plugin.json", PluginJson()), ("client/app.js", new string('x', 64)));
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-zip-" + Guid.NewGuid().ToString("N")));
        try
        {
            var result = PluginZipInstaller.Install(zip, root.FullName, new PluginZipLimits { MaxEntryBytes = 16 });
            Assert.False(result.Ok);
            Assert.Contains("too large", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "hello")));
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    private static string PluginJson() =>
        """
        {"id":"hello","name":"Hello","version":"1.0.0","description":"Hi","provides":["hello.homeRow"],"requires":[],"slots":["homeRow"],"server":"HelloModule","minHostApi":1,"client":{"html":"client/index.html","js":"client/app.js"}}
        """;

    private static MemoryStream MakeZip(params (string Name, string Text)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(text);
            }
        }

        stream.Position = 0;
        return stream;
    }
}

public class PluginPingMergeTests
{
    [Fact]
    public void MergeAddsPluginFlagsWithoutChangingExistingFields()
    {
        var ping = new MoonfinPingResponse
        {
            Installed = true,
            Version = "2.4.0",
            SettingsSyncEnabled = true,
            ServerName = "Jellyfin",
            SeerrEnabled = false,
            SeerrUrl = null,
            MdblistAvailable = false,
            TmdbAvailable = true,
            TmdbThumbsSupported = true,
            TmdbPersonLinksSupported = true,
            TmdbReviewsSupported = true,
            TmdbDetailsSupported = true,
            MessagesSupported = true,
            PinSupported = true,
            IncompleteRecordingsSupported = true,
            ExtendedCompleteShowingsSupported = true,
            RecommendationsSupported = true,
            DefaultSettings = null,
        };

        var before = Snapshot(ping);
        PluginPingMerge.Apply(ping, new Dictionary<string, bool> { ["hello.homeRow"] = true, ["shelf.rows"] = false });

        Assert.Equal(before, Snapshot(ping));
        Assert.NotNull(ping.Plugins);
        Assert.True(ping.Plugins["hello.homeRow"]);
        Assert.False(ping.Plugins["shelf.rows"]);

        var json = JsonSerializer.Serialize(ping);
        Assert.Contains("\"plugins\"", json, StringComparison.Ordinal);
        Assert.Contains("\"installed\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"tmdbAvailable\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"serverName\":\"Jellyfin\"", json, StringComparison.Ordinal);
    }

    private static Dictionary<string, object?> Snapshot(MoonfinPingResponse ping)
    {
        return typeof(MoonfinPingResponse)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.Name != nameof(MoonfinPingResponse.Plugins))
            .ToDictionary(property => property.Name, property => property.GetValue(ping));
    }
}

public class PluginConfigSecretsTests
{
    [Fact]
    public void SecretValuesAreNeverReturned()
    {
        const string Secret = "super-secret-value";
        var stored = """
            {
              "settings": { "label": "Hello", "password": "super-secret-value" },
              "secrets": { "apiKey": "super-secret-value" },
              "proxyAllow": [ { "name": "example", "baseUrl": "https://api.example.com/", "header": "Authorization", "scheme": "Bearer", "secret": "apiKey" } ]
            }
            """;

        var view = PluginConfigStore.ToClientJson(stored);
        Assert.DoesNotContain(Secret, view, StringComparison.Ordinal);
        Assert.Contains("apiKey", view, StringComparison.Ordinal);
        Assert.Contains("example", view, StringComparison.Ordinal);
        Assert.Contains("https://api.example.com/", view, StringComparison.Ordinal);
        Assert.Contains("Hello", view, StringComparison.Ordinal);
    }

    [Fact]
    public void ARedactedWriteDoesNotReplaceTheStoredSecret()
    {
        const string Secret = "super-secret-value";
        var stored = """{"settings":{"label":"Hello"},"secrets":{"apiKey":"super-secret-value"}}""";
        var merged = PluginConfigStore.Merge(stored, """{"settings":{"label":"Updated"},"secrets":{"apiKey":"********"}}""");
        Assert.Contains(Secret, merged, StringComparison.Ordinal);
        Assert.Contains("Updated", merged, StringComparison.Ordinal);

        var view = PluginConfigStore.ToClientJson(merged);
        Assert.DoesNotContain(Secret, view, StringComparison.Ordinal);
        Assert.Equal(Secret, PluginConfigStore.Secret(merged, "apiKey"));
    }
}

public class HelloPluginSampleTests
{
    [Fact]
    public async Task HelloModuleMatchesItsManifestAndServesARow()
    {
        var dll = HelloDll();
        Assert.True(File.Exists(dll), "Build the hello sample before running tests.");

        var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        var type = assembly.GetType("Moonfin.Plugins.Hello.HelloModule");
        Assert.NotNull(type);
        var module = Assert.IsAssignableFrom<IMoonbaseModule>(Activator.CreateInstance(type!));
        var manifest = PluginManifest.TryParse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(HelloSource())!, "plugin.json")), out var error);
        Assert.Null(error);
        Assert.Equal(manifest!.Id, module.Id);
        Assert.Equal(manifest.Version, module.Version);
        Assert.Contains("hello.homeRow", module.Provides);

        var routes = new Dictionary<string, PluginRouteHandler>(StringComparer.OrdinalIgnoreCase);
        module.MapRoutes(new RecordingRoutes(routes));
        Assert.True(routes.TryGetValue("GET rows", out var handler));
        var result = await handler!(new PluginRouteRequest());
        Assert.Contains("Hello, Moonfin", result.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", result.Body, StringComparison.OrdinalIgnoreCase);
    }

    private static string HelloSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "server-plugin.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "samples", "plugins", "hello", "HelloModule.cs");
    }

    private static string HelloDll()
    {
        var root = Path.GetDirectoryName(HelloSource())!;
        var release = Path.Combine(root, "bin", "Release", "net8.0", "Moonfin.Plugin.Hello.dll");
        var debug = Path.Combine(root, "bin", "Debug", "net8.0", "Moonfin.Plugin.Hello.dll");
        return File.Exists(release) ? release : debug;
    }

    private sealed class RecordingRoutes : IMoonbaseRouteTable
    {
        private readonly Dictionary<string, PluginRouteHandler> _routes;

        public RecordingRoutes(Dictionary<string, PluginRouteHandler> routes) => _routes = routes;

        public void MapGet(string relativePath, PluginRouteHandler handler) => _routes["GET " + relativePath] = handler;

        public void MapPost(string relativePath, PluginRouteHandler handler) => _routes["POST " + relativePath] = handler;

        public void MapPut(string relativePath, PluginRouteHandler handler) => _routes["PUT " + relativePath] = handler;

        public void MapDelete(string relativePath, PluginRouteHandler handler) => _routes["DELETE " + relativePath] = handler;
    }
}
