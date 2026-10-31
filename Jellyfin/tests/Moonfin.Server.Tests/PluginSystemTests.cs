using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moonfin.Server.Api;
using Moonfin.Server.Plugins;
using Moonfin.Server.Services;
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

    [Fact]
    public void ServerDllIsStoredAsMbmod()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-zip-" + Guid.NewGuid().ToString("N")));
        try
        {
            using var zip = MakeZip(
                ("plugin.json", PluginJson()),
                ("server/Moonfin.Plugin.Hello.dll", "MZ-not-really"));
            var installed = PluginZipInstaller.Install(zip, root.FullName);
            Assert.True(installed.Ok, installed.Error);
            var server = Path.Combine(root.FullName, "hello", "server");
            var stored = Path.Combine(server, "Moonfin.Plugin.Hello.dll.mbmod");
            Assert.True(File.Exists(stored));
            Assert.Equal("MZ-not-really", File.ReadAllText(stored));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(server),
                path => Path.GetFileName(path).EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public void HelloActivatesWhenMoonfinServerIsOutsideTheDefaultContext()
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "Moonfin.Server.dll");
        Assert.True(File.Exists(serverDll), "Moonfin.Server.dll was not copied beside the tests.");
        var helloDll = HelloDll();
        Assert.True(File.Exists(helloDll), "Build the hello sample before running tests.");

        var host = new JellyfinHostLoadContext();
        var serverAssembly = host.LoadFromAssemblyPath(serverDll);
        Assert.NotSame(typeof(IMoonbaseModule).Assembly, serverAssembly);
        Assert.Equal("Moonfin.Server", serverAssembly.GetName().Name);

        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-alc-" + Guid.NewGuid().ToString("N")));
        try
        {
            var pluginDir = Directory.CreateDirectory(Path.Combine(root.FullName, "hello"));
            File.Copy(Path.Combine(Path.GetDirectoryName(HelloSource())!, "plugin.json"), Path.Combine(pluginDir.FullName, "plugin.json"));
            var serverDir = Directory.CreateDirectory(Path.Combine(pluginDir.FullName, "server"));
            File.Copy(helloDll, Path.Combine(serverDir.FullName, "Moonfin.Plugin.Hello.dll"));

            var catalogType = serverAssembly.GetType("Moonfin.Server.Plugins.MoonbaseModuleCatalog", throwOnError: true)!;
            var catalog = Activator.CreateInstance(catalogType);
            Assert.NotNull(catalog);
            var ensure = catalogType.GetMethod("EnsureExternalLoaded", new[] { typeof(string) });
            Assert.NotNull(ensure);
            Exception? loadError = null;
            try
            {
                ensure!.Invoke(catalog, new object[] { root.FullName });
            }
            catch (TargetInvocationException ex)
            {
                loadError = ex.InnerException ?? ex;
            }

            var stored = Path.Combine(serverDir.FullName, "Moonfin.Plugin.Hello.dll.mbmod");
            Assert.True(File.Exists(stored));
            var bareDlls = Directory.EnumerateFiles(serverDir.FullName)
                .Select(Path.GetFileName)
                .Where(name => name!.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Empty(bareDlls);

            var notes = ReadStrings(catalog!, catalogType, "Notes");
            Assert.True(loadError == null, (loadError?.ToString() ?? string.Empty) + " | " + string.Join(" | ", notes));

            var modules = ((System.Collections.IEnumerable)catalogType.GetProperty("Modules")!.GetValue(catalog)!).Cast<object>().ToList();
            Assert.True(modules.Count == 1, "modules=" + modules.Count + " notes=" + string.Join(" | ", notes));
            var module = modules[0];
            Assert.Equal("hello", module.GetType().GetProperty("Id")!.GetValue(module));
            Assert.Equal("1.0.0", module.GetType().GetProperty("Version")!.GetValue(module));
            var provides = ((System.Collections.IEnumerable)module.GetType().GetProperty("Provides")!.GetValue(module)!).Cast<object>().Select(item => item?.ToString());
            Assert.Contains("hello.homeRow", provides);

            var contract = module.GetType().GetInterface("Moonfin.Server.Plugins.IMoonbaseModule");
            Assert.NotNull(contract);
            Assert.Same(serverAssembly, contract!.Assembly);
            Assert.NotSame(typeof(IMoonbaseModule).Assembly, contract.Assembly);

            var failed = ((System.Collections.IEnumerable)catalogType.GetMethod("FailedIds")!.Invoke(catalog, null)!).Cast<object>().Select(item => item?.ToString());
            Assert.DoesNotContain("hello", failed);
            Assert.DoesNotContain(notes, note => note.Contains("could not load", StringComparison.OrdinalIgnoreCase) || note.Contains("was not found", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try
            {
                Directory.Delete(root.FullName, recursive: true);
            }
            catch (IOException)
            {
                // The loaded module may keep the file mapped. The temp dir is disposable.
            }
        }
    }

    [Fact]
    public void HelloActivatesFromTheCatalogAndHidesTheServerDll()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-cat-" + Guid.NewGuid().ToString("N")));
        try
        {
            var pluginDir = Directory.CreateDirectory(Path.Combine(root.FullName, "hello"));
            File.Copy(Path.Combine(Path.GetDirectoryName(HelloSource())!, "plugin.json"), Path.Combine(pluginDir.FullName, "plugin.json"));
            var serverDir = Directory.CreateDirectory(Path.Combine(pluginDir.FullName, "server"));
            File.Copy(HelloDll(), Path.Combine(serverDir.FullName, "Moonfin.Plugin.Hello.dll"));

            var catalog = new MoonbaseModuleCatalog();
            catalog.EnsureExternalLoaded(root.FullName);

            Assert.True(File.Exists(Path.Combine(serverDir.FullName, "Moonfin.Plugin.Hello.dll.mbmod")));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(serverDir.FullName),
                path => Path.GetFileName(path).EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            var module = Assert.Single(catalog.Modules);
            Assert.Equal("hello", module.Id);
            Assert.Equal("1.0.0", module.Version);
            Assert.Contains("hello.homeRow", module.Provides);
            Assert.DoesNotContain("hello", catalog.FailedIds());
            Assert.DoesNotContain(catalog.Notes, note => note.Contains("could not load", StringComparison.OrdinalIgnoreCase) || note.Contains("was not found", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try
            {
                Directory.Delete(root.FullName, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static IEnumerable<string> ReadStrings(object target, Type type, string property)
    {
        var value = type.GetProperty(property)!.GetValue(target);
        return ((System.Collections.IEnumerable)value!).Cast<object>().Select(item => item?.ToString() ?? string.Empty);
    }

    private sealed class JellyfinHostLoadContext : AssemblyLoadContext
    {
        public JellyfinHostLoadContext()
            : base("jellyfin-plugin-host-sim", isCollectible: false)
        {
            Resolving += (_, assemblyName) =>
            {
                if (string.Equals(assemblyName.Name, "Moonfin.Server", StringComparison.Ordinal))
                {
                    return null;
                }

                foreach (var assembly in Default.Assemblies)
                {
                    if (string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.Ordinal))
                    {
                        return assembly;
                    }
                }

                try
                {
                    return Default.LoadFromAssemblyName(assemblyName);
                }
                catch
                {
                    return null;
                }
            };
        }

        protected override Assembly? Load(AssemblyName assemblyName) => null;
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

public class PluginServerLoadGateTests
{
    [Fact]
    public void DisabledAndUnmetRequiresAreNotLoaded()
    {
        var manifests = new[]
        {
            Must("""{"id":"off","name":"Off","version":"1","description":"","provides":["off.flag"],"slots":["events"],"server":"OffModule","minHostApi":1}"""),
            Must("""{"id":"need","name":"Need","version":"1","description":"","provides":["need.flag"],"requires":["tmdb"],"slots":["events"],"server":"NeedModule","minHostApi":1}"""),
            Must("""{"id":"ready","name":"Ready","version":"1","description":"","provides":["ready.flag"],"requires":["messages"],"slots":["events"],"server":"ReadyModule","minHostApi":1}"""),
            Must("""{"id":"shelf","name":"Shelf","version":"1","description":"","provides":["shelf.rows"],"slots":["homeRow"],"minHostApi":1}"""),
            Must("""{"id":"future","name":"Future","version":"1","description":"","provides":[],"slots":["events"],"server":"FutureModule","minHostApi":9}"""),
        };

        var ids = PluginCapabilityResolver.SelectServerLoadIds(
            manifests,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "off" },
            new HashSet<string>(StringComparer.Ordinal) { "messages" },
            MoonbasePlugins.HostApi);

        Assert.Equal(new[] { "ready" }, ids);
    }

    [Fact]
    public void ADependentLoadsOnlyAfterItsProvider()
    {
        var manifests = new[]
        {
            Must("""{"id":"beta","name":"Beta","version":"1","description":"","provides":["beta.ready"],"requires":["alpha.ready"],"slots":["homeRow"],"server":"BetaModule","minHostApi":1}"""),
            Must("""{"id":"alpha","name":"Alpha","version":"1","description":"","provides":["alpha.ready"],"slots":["events"],"server":"AlphaModule","minHostApi":1}"""),
        };

        var ids = PluginCapabilityResolver.SelectServerLoadIds(
            manifests,
            new HashSet<string>(),
            new HashSet<string>(),
            1);

        Assert.Equal(new[] { "alpha", "beta" }, ids);

        var disabledProvider = PluginCapabilityResolver.SelectServerLoadIds(
            manifests,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "alpha" },
            new HashSet<string>(),
            1);
        Assert.Empty(disabledProvider);
    }

    [Fact]
    public void UnloadedPluginWithUnmetRequiresDoesNotAskForARestart()
    {
        var status = Assert.Single(PluginCapabilityResolver.Resolve(
            new[]
            {
                Must("""{"id":"need","name":"Need","version":"1","description":"","provides":["need.flag"],"requires":["tmdb"],"slots":["events"],"server":"NeedModule","minHostApi":1}"""),
            },
            new Dictionary<string, IReadOnlyCollection<string>>(),
            new HashSet<string>(),
            new HashSet<string>(),
            new HashSet<string>(),
            1));

        Assert.False(status.Active);
        Assert.Contains("tmdb", status.DormantReason, StringComparison.Ordinal);
        Assert.DoesNotContain("Restart", status.DormantReason, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadExternalSkipsDisabledAndUnmetPlugins()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-load-" + Guid.NewGuid().ToString("N")));
        try
        {
            WritePlugin(root.FullName, "off", """{"id":"off","name":"Off","version":"1","description":"","provides":[],"requires":["messages"],"slots":["events"],"server":"OffModule","minHostApi":1}""");
            WritePlugin(root.FullName, "need", """{"id":"need","name":"Need","version":"1","description":"","provides":[],"requires":["tmdb"],"slots":["events"],"server":"NeedModule","minHostApi":1}""");
            WritePlugin(root.FullName, "ready", """{"id":"ready","name":"Ready","version":"1","description":"","provides":[],"requires":["messages"],"slots":["events"],"server":"ReadyModule","minHostApi":1}""");
            File.WriteAllText(
                Path.Combine(root.FullName, "_state.json"),
                """{"disabled":["off"]}""");

            var catalog = new MoonbaseModuleCatalog();
            catalog.EnsureExternalLoaded(root.FullName);

            Assert.Contains("ready", catalog.FailedIds());
            Assert.DoesNotContain("off", catalog.FailedIds());
            Assert.DoesNotContain("need", catalog.FailedIds());
            Assert.False(catalog.ShouldRegisterServices("ready"));
            Assert.False(catalog.ShouldRegisterServices("off"));
            Assert.False(catalog.ShouldRegisterServices("need"));
            Assert.DoesNotContain(catalog.Modules, module => module.Id is "off" or "need" or "ready");
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    private static void WritePlugin(string root, string id, string json)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, id));
        File.WriteAllText(Path.Combine(dir.FullName, "plugin.json"), json);
    }

    private static PluginManifest Must(string json)
    {
        var manifest = PluginManifest.TryParse(json, out var error);
        Assert.Null(error);
        Assert.NotNull(manifest);
        return manifest;
    }
}

public class PluginDataQuotaTests
{
    [Fact]
    public void TheTwoHundredFirstKeyAndFiveMegabytesAreRejected()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-data-" + Guid.NewGuid().ToString("N")));
        try
        {
            for (var i = 0; i < PluginDataQuota.MaxKeys; i++)
            {
                File.WriteAllText(Path.Combine(dir.FullName, "k" + i + ".json"), "{}");
            }

            Assert.False(PluginDataQuota.TryAccept(dir.FullName, "extra", 2, out var keyError));
            Assert.Contains("200", keyError, StringComparison.Ordinal);
            Assert.True(PluginDataQuota.TryAccept(dir.FullName, "k0", 2, out _));

            Directory.Delete(dir.FullName, recursive: true);
            Directory.CreateDirectory(dir.FullName);
            File.WriteAllText(Path.Combine(dir.FullName, "blob.json"), new string('x', 1024));
            Assert.False(PluginDataQuota.TryAccept(dir.FullName, "blob", (int)PluginDataQuota.MaxBytes + 1, out var sizeError));
            Assert.Contains("5 MB", sizeError, StringComparison.Ordinal);
            Assert.True(PluginDataQuota.TryAccept(dir.FullName, "blob", 1024, out _));
            Assert.False(PluginDataQuota.TryAccept(dir.FullName, "other", (int)PluginDataQuota.MaxBytes, out _));
        }
        finally
        {
            if (Directory.Exists(dir.FullName))
            {
                Directory.Delete(dir.FullName, recursive: true);
            }
        }
    }
}

public class PluginRouteAccessTests
{
    [Fact]
    public void DataAndProxyRequireAnActivePlugin()
    {
        Assert.False(PluginRouteAccess.CanUseData(enabled: true, active: false));
        Assert.False(PluginRouteAccess.CanProxy(true, false, "user", isAdmin: true));
        Assert.True(PluginRouteAccess.CanUseData(true, true));
    }

    [Fact]
    public void ProxyAccessDefaultsToAdmin()
    {
        var omitted = PluginManifest.TryParse(
            """{"id":"hello","name":"Hello","version":"1","description":"","provides":[],"slots":["events"],"minHostApi":1}""",
            out var omittedError);
        Assert.Null(omittedError);
        Assert.Equal("admin", omitted!.ProxyAccess);
        Assert.True(PluginRouteAccess.CanProxy(true, true, omitted.ProxyAccess, isAdmin: true));
        Assert.False(PluginRouteAccess.CanProxy(true, true, omitted.ProxyAccess, isAdmin: false));

        var users = PluginManifest.TryParse(
            """{"id":"hello","name":"Hello","version":"1","description":"","provides":[],"slots":["events"],"proxyAccess":"user","minHostApi":1}""",
            out var userError);
        Assert.Null(userError);
        Assert.Equal("user", users!.ProxyAccess);
        Assert.True(PluginRouteAccess.CanProxy(true, true, users.ProxyAccess, isAdmin: false));

        var bad = PluginManifest.TryParse(
            """{"id":"hello","name":"Hello","version":"1","description":"","provides":[],"slots":["events"],"proxyAccess":"world","minHostApi":1}""",
            out var badError);
        Assert.Null(bad);
        Assert.Contains("proxyAccess", badError, StringComparison.Ordinal);
    }
}

public class PluginProxyInjectionTests
{
    [Fact]
    public void AQuerySecretIsAppendedAndCannotBeOverridden()
    {
        Assert.True(PluginProxy.TryBuildUpstream(
            "https://api.themoviedb.org/3/",
            "movie/1",
            new Dictionary<string, string> { ["language"] = "en", ["api_key"] = "from-the-client" },
            out var upstream,
            out _));

        Assert.True(PluginProxy.TryAppendQuerySecret(upstream!, "api_key", "server-secret", out var signed, out var error));
        Assert.Null(error);
        Assert.Contains("language=en", signed!.Query, StringComparison.Ordinal);
        Assert.Contains("api_key=server-secret", signed.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("from-the-client", signed.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("server-secret", error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidQueryNameDoesNotEchoTheSecret()
    {
        Assert.False(PluginProxy.TryAppendQuerySecret(new Uri("https://api.themoviedb.org/3/movie/1"), "../key", "super-secret-value", out _, out var error));
        Assert.DoesNotContain("super-secret-value", error, StringComparison.Ordinal);
    }

    [Fact]
    public void NonJsonNonImageResponsesArePlainText()
    {
        var html = PluginProxy.PresentUpstream("text/html; charset=utf-8");
        Assert.Equal("text/plain; charset=utf-8", html.ContentType);
        Assert.False(html.Binary);

        var json = PluginProxy.PresentUpstream("application/json; charset=utf-8");
        Assert.Equal("application/json; charset=utf-8", json.ContentType);
        Assert.False(json.Binary);

        var image = PluginProxy.PresentUpstream("image/jpeg");
        Assert.Equal("image/jpeg", image.ContentType);
        Assert.True(image.Binary);

        var problem = PluginProxy.PresentUpstream("application/problem+json");
        Assert.Equal("application/problem+json", problem.ContentType);
        Assert.False(problem.Binary);
    }

    [Fact]
    public void SvgIsPlainTextAndEveryProxyResponseIsSandboxed()
    {
        var svg = PluginProxy.PresentUpstream("image/svg+xml; charset=utf-8");
        Assert.Equal("text/plain; charset=utf-8", svg.ContentType);
        Assert.False(svg.Binary);

        var headers = new HeaderDictionary();
        PluginProxy.ApplyResponseHeaders(headers);
        Assert.Equal("nosniff", headers[PluginProxy.ContentTypeOptionsHeader]);
        Assert.Equal("sandbox", headers[PluginProxy.ContentSecurityPolicyHeader]);
    }

    [Fact]
    public void ImageBodiesStayBytesAndSecretsAreScrubbedOnlyFromText()
    {
        var secret = "super-secret-value";
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0xFF, 0x00 };
        var raw = png.Concat(Encoding.ASCII.GetBytes(secret)).ToArray();
        var image = PluginProxy.PresentBody("image/png", raw, secret);
        Assert.Same(raw, image.RawBody);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(string.Empty, image.Body);

        var svg = PluginProxy.PresentBody("image/svg+xml", Encoding.UTF8.GetBytes("<svg>" + secret + "</svg>"), secret);
        Assert.Null(svg.RawBody);
        Assert.Equal("text/plain; charset=utf-8", svg.ContentType);
        Assert.DoesNotContain(secret, svg.Body, StringComparison.Ordinal);
        Assert.Contains(PluginConfigStore.RedactedMarker, svg.Body, StringComparison.Ordinal);

        var json = PluginProxy.PresentBody("application/json", Encoding.UTF8.GetBytes("{\"k\":\"" + secret + "\"}"), secret);
        Assert.Null(json.RawBody);
        Assert.DoesNotContain(secret, json.Body, StringComparison.Ordinal);
    }
}

public class PluginZipAsyncInstallTests
{
    [Fact]
    public async Task ANonSeekableUploadIsReadAsynchronously()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-async-" + Guid.NewGuid().ToString("N")));
        try
        {
            await using var zip = MakeZip(("plugin.json", PluginJson()), ("client/index.html", "<p>async</p>"));
            var result = await PluginZipInstaller.InstallAsync(new NonSeekableStream(zip.ToArray()), root.FullName);
            Assert.True(result.Ok, result.Error);
            Assert.Contains("async", File.ReadAllText(Path.Combine(root.FullName, "hello", "client", "index.html")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task AFailedReadDoesNotEscapeTheTry()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-async-" + Guid.NewGuid().ToString("N")));
        try
        {
            var result = await PluginZipInstaller.InstallAsync(new ThrowingStream(), root.FullName);
            Assert.False(result.Ok);
            Assert.Contains("could not be installed", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task AnOversizedNonSeekableUploadIsRejected()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-async-" + Guid.NewGuid().ToString("N")));
        try
        {
            var result = await PluginZipInstaller.InstallAsync(
                new NonSeekableStream(new byte[64]),
                root.FullName,
                new PluginZipLimits { MaxZipBytes = 16 });
            Assert.False(result.Ok);
            Assert.Contains("too large", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    private static string PluginJson() =>
        """
        {"id":"hello","name":"Hello","version":"1.0.0","description":"Hi","provides":["hello.homeRow"],"requires":[],"slots":["homeRow"],"server":"HelloModule","minHostApi":1}
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

    private sealed class NonSeekableStream : Stream
    {
        private readonly byte[] _data;
        private int _pos;

        public NonSeekableStream(byte[] data) => _data = data;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _data.Length)
            {
                return 0;
            }

            var n = Math.Min(count, _data.Length - _pos);
            Buffer.BlockCopy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("boom");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public class PluginRegistryCacheTests
{
    [Fact]
    public void TheListStaysCachedUntilStateOrConfigChanges()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-cache-" + Guid.NewGuid().ToString("N")));
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "hello"));
            File.WriteAllText(
                Path.Combine(folder.FullName, "plugin.json"),
                """{"id":"hello","name":"Hello","version":"1","description":"","provides":[],"slots":["homeRow"],"minHostApi":1}""");

            var registry = new MoonfinPluginRegistry(new MoonbaseModuleCatalog(), root.FullName);
            var first = registry.List();
            var second = registry.List();
            Assert.Same(first, second);
            Assert.True(Assert.Single(first).Enabled);

            var services = new ServiceCollection().BuildServiceProvider();
            var disabled = registry.SetEnabled("hello", false, services);
            Assert.NotNull(disabled);
            Assert.False(disabled!.Enabled);
            var afterDisable = registry.List();
            Assert.NotSame(first, afterDisable);

            registry.SetEnabled("hello", true, services);
            registry.WriteConfig("hello", """{"settings":{"label":"Hello"}}""");
            var afterConfig = registry.List();
            Assert.NotSame(afterDisable, afterConfig);

            registry.Invalidate();
            Assert.NotSame(afterConfig, registry.List());

            Assert.True(registry.TryDelete("hello", out _));
            Assert.Empty(registry.List());
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public void ASettingsSaveDropsTheCachedPluginList()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-cache-" + Guid.NewGuid().ToString("N")));
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "hello"));
            File.WriteAllText(
                Path.Combine(folder.FullName, "plugin.json"),
                """{"id":"hello","name":"Hello","version":"1","description":"","provides":[],"slots":["homeRow"],"minHostApi":1}""");

            var registry = new MoonfinPluginRegistry(new MoonbaseModuleCatalog(), root.FullName);
            var first = registry.List();
            var services = new ServiceCollection();
            services.AddSingleton(registry);
            MoonfinPlugin.InvalidatePluginRegistry(services.BuildServiceProvider());
            Assert.NotSame(first, registry.List());
            MoonfinPlugin.InvalidatePluginRegistry(null);
            MoonfinPlugin.InvalidatePluginRegistry(new ServiceCollection().BuildServiceProvider());
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }
}

public class PluginEventTests
{
    [Fact]
    public void BuiltInTypesAreBlockedAndPluginEventsArePrefixed()
    {
        Assert.Null(MoonbaseEventTypes.WireType("hello", "settingsUpdated"));
        Assert.Null(MoonbaseEventTypes.WireType("hello", "themesChanged"));
        Assert.Null(MoonbaseEventTypes.WireType("hello", "messagesChanged"));
        Assert.Equal("plugin:hello:custom", MoonbaseEventTypes.WireType("hello", "custom"));
        Assert.Equal("plugin:hello:pluginState", MoonbaseEventTypes.WireType("hello", "pluginState"));
    }

    [Fact]
    public void ATargetUserIsTheOnlyRecipient()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-events-" + Guid.NewGuid().ToString("N")));
        try
        {
            var settings = new MoonfinSettingsService(NullLogger<MoonfinSettingsService>.Instance, root.FullName);
            var events = new MoonbaseEvents(settings);
            var user = Guid.NewGuid();
            var other = Guid.NewGuid();
            var mine = settings.RegisterSseChannel(user);
            var theirs = settings.RegisterSseChannel(other);

            events.Emit("hello", "settingsUpdated", new { n = 1 }, user);
            events.Emit("hello", "themesChanged");
            events.Emit("hello", "messagesChanged", null, other);
            Assert.False(mine.Reader.TryRead(out _));
            Assert.False(theirs.Reader.TryRead(out _));

            events.Emit("hello", "custom", new { ok = true }, user);
            Assert.True(mine.Reader.TryRead(out var delivered));
            Assert.Contains("\"type\":\"plugin:hello:custom\"", delivered, StringComparison.Ordinal);
            Assert.Contains("\"pluginId\":\"hello\"", delivered, StringComparison.Ordinal);
            Assert.False(theirs.Reader.TryRead(out _));

            events.Emit("hello", "pluginState");
            Assert.True(mine.Reader.TryRead(out var broadcastMine));
            Assert.True(theirs.Reader.TryRead(out var broadcastTheirs));
            Assert.Contains("plugin:hello:pluginState", broadcastMine, StringComparison.Ordinal);
            Assert.Contains("plugin:hello:pluginState", broadcastTheirs, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }
}
