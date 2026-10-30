using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moonfin.Server;
using Moonfin.Server.Api;
using Moonfin.Server.Plugins;
using System.Text.Encodings.Web;
using Xunit;

namespace Moonfin.Server.Tests;

public class PluginClientFileTests
{
    private const string Secret = "super-secret-value";

    [Fact]
    public async Task AnonymousClientFilesArePublicAndEverythingElseStaysLocked()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-files-" + Guid.NewGuid().ToString("N")));
        WebApplication? app = null;
        try
        {
            WritePlugin(root.FullName, "hello", ClientManifest("hello"), "<!DOCTYPE html><title>Hello</title>");
            WritePlugin(root.FullName, "silent", ClientManifest("silent"), "<p>silent</p>");
            WritePlugin(
                root.FullName,
                "need",
                ClientManifest("need", "\"requires\":[\"definitely.missing\"],"),
                "<p>dormant</p>");
            File.WriteAllText(Path.Combine(root.FullName, "_state.json"), """{"disabled":["silent"]}""");
            File.WriteAllText(Path.Combine(root.FullName, "hello", "client", ".env"), Secret);

            var catalog = new MoonbaseModuleCatalog();
            var registry = new MoonfinPluginRegistry(catalog, root.FullName);
            var listed = List(registry, catalog);
            var hello = Assert.Single(listed.Plugins, item => item.Id == "hello");
            Assert.True(hello.Enabled);
            Assert.True(hello.Active);
            Assert.Equal("/Moonfin/Plugins/hello/files/client/index.html", hello.ClientEntryUrl);
            Assert.False(Assert.Single(listed.Plugins, item => item.Id == "silent").Enabled);
            Assert.False(Assert.Single(listed.Plugins, item => item.Id == "need").Active);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = NoUserHandler.Name;
                options.DefaultChallengeScheme = NoUserHandler.Name;
            }).AddScheme<AuthenticationSchemeOptions, NoUserHandler>(NoUserHandler.Name, _ => { });
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("RequiresElevation", policy => policy.RequireRole("Administrator"));
            });
            builder.Services.AddSingleton<IStartupFilter, PluginClientFileGuard>();
            builder.Services.AddSingleton(registry);
            builder.Services.AddSingleton(catalog);
            builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
            {
                manager.ApplicationParts.Clear();
                manager.ApplicationParts.Add(new Microsoft.AspNetCore.Mvc.ApplicationParts.AssemblyPart(typeof(MoonfinPluginsController).Assembly));
                manager.FeatureProviders.Clear();
                manager.FeatureProviders.Add(new OnlyPluginController());
            });

            app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            await app.StartAsync();

            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

            var html = await client.GetAsync(hello.ClientEntryUrl);
            Assert.Equal(HttpStatusCode.OK, html.StatusCode);
            Assert.Equal("text/html; charset=utf-8", html.Content.Headers.ContentType?.ToString());
            Assert.Equal(ClientPage(app.Urls.Single()), Header(html, "Content-Security-Policy"));
            Assert.Equal("nosniff", Header(html, "X-Content-Type-Options"));
            Assert.Equal("no-cache", Header(html, "Cache-Control"));
            Assert.Equal("*", Header(html, "Access-Control-Allow-Origin"));
            var etag = Header(html, "ETag");
            Assert.False(string.IsNullOrEmpty(etag));
            var htmlBody = await html.Content.ReadAsStringAsync();
            Assert.Contains("Hello", htmlBody, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, htmlBody, StringComparison.Ordinal);
            Assert.Equal(1, CountOf(htmlBody, BridgeTag));
            Assert.StartsWith(BridgeTag, htmlBody, StringComparison.Ordinal);

            var script = await client.GetAsync("/Moonfin/Plugins/hello/files/client/app.js");
            Assert.Equal(HttpStatusCode.OK, script.StatusCode);
            Assert.Equal("application/javascript", script.Content.Headers.ContentType?.ToString());
            Assert.Equal("nosniff", Header(script, "X-Content-Type-Options"));
            Assert.Equal("no-cache", Header(script, "Cache-Control"));
            Assert.Equal("*", Header(script, "Access-Control-Allow-Origin"));
            Assert.Equal(string.Empty, Header(script, "Content-Security-Policy"));
            Assert.Equal("console.log('hello-app');", await script.Content.ReadAsStringAsync());

            using var cachedRequest = new HttpRequestMessage(HttpMethod.Get, hello.ClientEntryUrl);
            cachedRequest.Headers.TryAddWithoutValidation("If-None-Match", etag);
            var cached = await client.SendAsync(cachedRequest);
            Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
            Assert.Equal(etag, Header(cached, "ETag"));
            Assert.Equal("no-cache", Header(cached, "Cache-Control"));
            Assert.Equal(string.Empty, await cached.Content.ReadAsStringAsync());

            var unknown = await client.GetAsync("/Moonfin/Plugins/missing/files/client/index.html");
            var disabled = await client.GetAsync("/Moonfin/Plugins/silent/files/client/index.html");
            var dormant = await client.GetAsync("/Moonfin/Plugins/need/files/client/index.html");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Equal(string.Empty, Header(unknown, "Access-Control-Allow-Origin"));
            await AssertSame(unknown, disabled);
            await AssertSame(unknown, dormant);

            string[] blocked =
            {
                "/Moonfin/Plugins/hello/files/plugin.json",
                "/Moonfin/Plugins/hello/files/config.json",
                "/Moonfin/Plugins/hello/files/data/secret.json",
                "/Moonfin/Plugins/hello/files/client/native.dll",
                "/Moonfin/Plugins/hello/files/server/Hello.dll",
                "/Moonfin/Plugins/hello/files/client/native.so",
                "/Moonfin/Plugins/hello/files/client/.env",
            };
            foreach (var path in blocked)
            {
                var denied = await client.GetAsync(path);
                var body = await denied.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
                Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);
                Assert.DoesNotContain("hello-app", body, StringComparison.Ordinal);
                Assert.DoesNotContain("MZ-", body, StringComparison.Ordinal);
            }

            var baseUri = new Uri(app.Urls.Single());
            foreach (var path in new[]
            {
                "/Moonfin/Plugins/hello/files/../plugin.json",
                "/Moonfin/Plugins/hello/files/%2e%2e/config.json",
                "/Moonfin/Plugins/hello/files/%2E%2E/config.json",
                "/Moonfin/Plugins/hello/files/client\\app.js",
                "/Moonfin/Plugins/hello/files/C:/Windows/win.ini",
            })
            {
                var raw = await Raw(baseUri, "GET " + path + " HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
                Assert.StartsWith("HTTP/1.1 404", raw, StringComparison.Ordinal);
                Assert.DoesNotContain(Secret, raw, StringComparison.Ordinal);
                Assert.DoesNotContain("hello-app", raw, StringComparison.Ordinal);
            }

            var data = await client.GetAsync("/Moonfin/Plugins/hello/Data/notes");
            var proxy = await client.PostAsync("/Moonfin/Plugins/hello/Proxy", new StringContent("{}", Encoding.UTF8, "application/json"));
            var catchAll = await client.GetAsync("/Moonfin/Plugins/hello/client/index.html");
            Assert.Equal(HttpStatusCode.Unauthorized, data.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, proxy.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, catchAll.StatusCode);
            Assert.Equal(string.Empty, Header(data, "Access-Control-Allow-Origin"));
            Assert.Equal(string.Empty, Header(proxy, "Access-Control-Allow-Origin"));
            Assert.Equal(string.Empty, Header(catchAll, "Access-Control-Allow-Origin"));
            Assert.DoesNotContain("Hello", await catchAll.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            if (app != null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }

            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task RootEntriesSymlinksAndBlockedPathsStayClosed()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-files-" + Guid.NewGuid().ToString("N")));
        string? fileLink = null;
        string? dirLink = null;
        WebApplication? app = null;
        try
        {
            WritePlugin(root.FullName, "edge", ClientManifest("edge"), "EDGEPAGE");
            var edgeClient = Path.Combine(root.FullName, "edge", "client");
            File.WriteAllText(Path.Combine(edgeClient, "app.js.map"), "{\"version\":3}");
            File.WriteAllText(Path.Combine(edgeClient, "font.ttf"), "ttf-font");
            File.WriteAllText(Path.Combine(edgeClient, "icon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");
            var outside = Directory.CreateDirectory(Path.Combine(root.FullName, "edge", "outside"));
            File.WriteAllText(Path.Combine(outside.FullName, "secret.txt"), Secret);
            fileLink = Path.Combine(edgeClient, "linked.js");
            dirLink = Path.Combine(edgeClient, "linked");
            File.CreateSymbolicLink(fileLink, Path.Combine(root.FullName, "edge", "config.json"));
            Directory.CreateSymbolicLink(dirLink, outside.FullName);

            WriteRootPlugin(root.FullName, "rooty", "index.html", "app.js", "ROOTPAGE", "root-app");
            WriteRootPlugin(root.FullName, "dotroot", "./index.html", "./app.js", "DOTROOT", "dot-app");
            WritePlugin(root.FullName, "clean", ManifestWithClient("clean", "./client/./index.html", "client//app.js"), "CLEANPAGE");
            WritePlugin(root.FullName, "climb", ManifestWithClient("climb", "../config.json"), "FALLBACK");
            WritePlugin(root.FullName, "srv", ManifestWithClient("srv", "server/index.html"), "SRVCLIENT");
            File.WriteAllText(Path.Combine(root.FullName, "srv", "server", "index.html"), "SERVERPAGE");
            WritePlugin(root.FullName, "dat", ManifestWithClient("dat", "data/secret.json"), "DATCLIENT");
            WritePlugin(root.FullName, "mix", ManifestWithClient("mix", "index.html", "client/app.js"), "client-index");
            File.WriteAllText(Path.Combine(root.FullName, "mix", "index.html"), "MIXPAGE");
            File.WriteAllText(Path.Combine(root.FullName, "mix", "client", "app.js"), "mix-app");
            File.WriteAllText(Path.Combine(root.FullName, "mix", "client", "style.css"), "mix-css");
            File.WriteAllText(Path.Combine(root.FullName, "mix", "extra.html"), "MIXEXTRA");

            var catalog = new MoonbaseModuleCatalog();
            var registry = new MoonfinPluginRegistry(catalog, root.FullName);
            var listed = List(registry, catalog);

            var edge = Assert.Single(listed.Plugins, item => item.Id == "edge");
            Assert.Equal("/Moonfin/Plugins/edge/files/client/index.html", edge.ClientEntryUrl);
            var rooty = Assert.Single(listed.Plugins, item => item.Id == "rooty");
            Assert.Equal("/Moonfin/Plugins/rooty/files/index.html", rooty.ClientEntryUrl);
            var dotroot = Assert.Single(listed.Plugins, item => item.Id == "dotroot");
            Assert.Equal("/Moonfin/Plugins/dotroot/files/index.html", dotroot.ClientEntryUrl);
            var clean = Assert.Single(listed.Plugins, item => item.Id == "clean");
            Assert.Equal("/Moonfin/Plugins/clean/files/client/index.html", clean.ClientEntryUrl);
            Assert.Null(Assert.Single(listed.Plugins, item => item.Id == "climb").ClientEntryUrl);
            Assert.Null(Assert.Single(listed.Plugins, item => item.Id == "srv").ClientEntryUrl);
            Assert.Null(Assert.Single(listed.Plugins, item => item.Id == "dat").ClientEntryUrl);
            var mix = Assert.Single(listed.Plugins, item => item.Id == "mix");
            Assert.Equal("/Moonfin/Plugins/mix/files/index.html", mix.ClientEntryUrl);

            app = BuildFilesHost(registry, catalog);
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

            var rootHtml = await client.GetAsync(rooty.ClientEntryUrl);
            Assert.Equal(HttpStatusCode.OK, rootHtml.StatusCode);
            Assert.Equal(ClientPage(app.Urls.Single()), Header(rootHtml, "Content-Security-Policy"));
            Assert.Contains("ROOTPAGE", await rootHtml.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            var rootJs = await client.GetAsync("/Moonfin/Plugins/rooty/files/app.js");
            Assert.Equal(HttpStatusCode.OK, rootJs.StatusCode);
            Assert.Contains("root-app", await rootJs.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            await AssertMissing(client, "/Moonfin/Plugins/rooty/files/client/index.html", "NESTED");
            await AssertMissing(client, "/Moonfin/Plugins/rooty/files/extra.html", "EXTRA");
            await AssertMissing(client, "/Moonfin/Plugins/rooty/files/readme.txt", "README");
            await AssertMissing(client, "/Moonfin/Plugins/rooty/files/config.json", Secret);
            await AssertMissing(client, "/Moonfin/Plugins/rooty/files/plugin.json", Secret);

            var dotHtml = await client.GetAsync(dotroot.ClientEntryUrl);
            Assert.Equal(HttpStatusCode.OK, dotHtml.StatusCode);
            Assert.Contains("DOTROOT", await dotHtml.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            var dotJs = await client.GetAsync("/Moonfin/Plugins/dotroot/files/app.js");
            Assert.Equal(HttpStatusCode.OK, dotJs.StatusCode);
            Assert.Contains("dot-app", await dotJs.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            await AssertMissing(client, "/Moonfin/Plugins/dotroot/files/extra.html", "EXTRA");

            var cleanHtml = await client.GetAsync(clean.ClientEntryUrl);
            Assert.Equal(HttpStatusCode.OK, cleanHtml.StatusCode);
            Assert.Contains("CLEANPAGE", await cleanHtml.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            var cleanJs = await client.GetAsync("/Moonfin/Plugins/clean/files/client/app.js");
            Assert.Equal(HttpStatusCode.OK, cleanJs.StatusCode);

            var mixHtml = await client.GetAsync(mix.ClientEntryUrl);
            Assert.Equal(HttpStatusCode.OK, mixHtml.StatusCode);
            Assert.Contains("MIXPAGE", await mixHtml.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            var mixJs = await client.GetAsync("/Moonfin/Plugins/mix/files/client/app.js");
            Assert.Equal(HttpStatusCode.OK, mixJs.StatusCode);
            Assert.Contains("mix-app", await mixJs.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            var mixCss = await client.GetAsync("/Moonfin/Plugins/mix/files/client/style.css");
            Assert.Equal(HttpStatusCode.OK, mixCss.StatusCode);
            Assert.Contains("mix-css", await mixCss.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            await AssertMissing(client, "/Moonfin/Plugins/mix/files/extra.html", "MIXEXTRA");
            await AssertMissing(client, "/Moonfin/Plugins/mix/files/app.js", "root-app");

            var fallback = await client.GetAsync("/Moonfin/Plugins/climb/files/client/index.html");
            Assert.Equal(HttpStatusCode.OK, fallback.StatusCode);
            Assert.Contains("FALLBACK", await fallback.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            await AssertMissing(client, "/Moonfin/Plugins/climb/files/config.json", Secret);

            await AssertMissing(client, "/Moonfin/Plugins/srv/files/server/index.html", "SERVERPAGE");
            await AssertMissing(client, "/Moonfin/Plugins/srv/files/data/secret.json", Secret);
            var srvClient = await client.GetAsync("/Moonfin/Plugins/srv/files/client/index.html");
            Assert.Equal(HttpStatusCode.OK, srvClient.StatusCode);
            Assert.Contains("SRVCLIENT", await srvClient.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            await AssertMissing(client, "/Moonfin/Plugins/dat/files/data/secret.json", Secret);
            await AssertMissing(client, "/Moonfin/Plugins/dat/files/server/Hello.dll", "MZ-server");
            var datClient = await client.GetAsync("/Moonfin/Plugins/dat/files/client/index.html");
            Assert.Equal(HttpStatusCode.OK, datClient.StatusCode);
            Assert.Contains("DATCLIENT", await datClient.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            var map = await client.GetAsync("/Moonfin/Plugins/edge/files/client/app.js.map");
            Assert.Equal(HttpStatusCode.OK, map.StatusCode);
            Assert.Equal("application/json", map.Content.Headers.ContentType?.ToString());
            Assert.Equal(string.Empty, Header(map, "Content-Security-Policy"));
            var ttf = await client.GetAsync("/Moonfin/Plugins/edge/files/client/font.ttf");
            Assert.Equal(HttpStatusCode.OK, ttf.StatusCode);
            Assert.Equal("font/ttf", ttf.Content.Headers.ContentType?.ToString());
            Assert.Equal(string.Empty, Header(ttf, "Content-Security-Policy"));
            var svg = await client.GetAsync("/Moonfin/Plugins/edge/files/client/icon.svg");
            Assert.Equal(HttpStatusCode.OK, svg.StatusCode);
            Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType?.ToString());
            Assert.Equal(ClientPage(app.Urls.Single()), Header(svg, "Content-Security-Policy"));
            Assert.Equal("nosniff", Header(svg, "X-Content-Type-Options"));
            Assert.Equal("no-cache", Header(svg, "Cache-Control"));
            Assert.Equal("*", Header(svg, "Access-Control-Allow-Origin"));
            Assert.Equal("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>", await svg.Content.ReadAsStringAsync());

            await AssertMissing(client, "/Moonfin/Plugins/edge/files/client/linked.js", Secret);
            await AssertMissing(client, "/Moonfin/Plugins/edge/files/client/linked/secret.txt", Secret);
            await AssertMissing(client, "/Moonfin/Plugins/edge/files/Client/index.html", "EDGEPAGE");

            var baseUri = new Uri(app.Urls.Single());
            foreach (var path in new[]
            {
                "/Moonfin/Plugins/edge/files/%252e%252e/config.json",
                "/Moonfin/Plugins/edge/files/client/%252e%252e/config.json",
                "/Moonfin/Plugins/edge/files/client/index.html.",
                "/Moonfin/Plugins/edge/files/client/index.html%20",
                "/Moonfin/Plugins/climb/files/../config.json",
            })
            {
                var raw = await Raw(baseUri, "GET " + path + " HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
                Assert.StartsWith("HTTP/1.1 404", raw, StringComparison.Ordinal);
                Assert.DoesNotContain(Secret, raw, StringComparison.Ordinal);
                Assert.DoesNotContain("EDGEPAGE", raw, StringComparison.Ordinal);
                Assert.DoesNotContain("FALLBACK", raw, StringComparison.Ordinal);
            }

            using var starred = new HttpRequestMessage(HttpMethod.Get, "/Moonfin/Plugins/edge/files/config.json");
            starred.Headers.TryAddWithoutValidation("If-None-Match", "*");
            var blockedStar = await client.SendAsync(starred);
            Assert.Equal(HttpStatusCode.NotFound, blockedStar.StatusCode);
            Assert.DoesNotContain(Secret, await blockedStar.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var liveStar = new HttpRequestMessage(HttpMethod.Get, edge.ClientEntryUrl);
            liveStar.Headers.TryAddWithoutValidation("If-None-Match", "*");
            var live = await client.SendAsync(liveStar);
            Assert.Equal(HttpStatusCode.NotModified, live.StatusCode);
        }
        finally
        {
            if (app != null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }

            if (fileLink != null && File.Exists(fileLink))
            {
                File.Delete(fileLink);
            }

            if (dirLink != null && Directory.Exists(dirLink))
            {
                Directory.Delete(dirLink, recursive: false);
            }

            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public void OnlyTheClientFileRouteIsAnonymous()
    {
        var methods = typeof(MoonfinPluginsController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .ToList();

        Assert.Contains(methods, method => method.Name == nameof(MoonfinPluginsController.PluginFile));
        foreach (var method in methods)
        {
            var anonymous = method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true);
            var authorize = method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>().ToList();
            if (method.Name == nameof(MoonfinPluginsController.PluginFile))
            {
                Assert.NotEmpty(anonymous);
                Assert.Empty(authorize);
                Assert.Equal("Plugins/{id}/files/{**path}", Assert.Single(method.GetCustomAttributes<HttpGetAttribute>()).Template);
                continue;
            }

            if (method.Name == nameof(MoonfinPluginsController.PluginHostBridge))
            {
                Assert.NotEmpty(anonymous);
                Assert.Empty(authorize);
                Assert.Equal("PluginHost/bridge.js", Assert.Single(method.GetCustomAttributes<HttpGetAttribute>()).Template);
                continue;
            }

            Assert.Empty(anonymous);
            var auth = Assert.Single(authorize);
            var elevated = method.Name is nameof(MoonfinPluginsController.Install)
                or nameof(MoonfinPluginsController.Enable)
                or nameof(MoonfinPluginsController.Disable)
                or nameof(MoonfinPluginsController.Delete)
                or nameof(MoonfinPluginsController.GetConfig)
                or nameof(MoonfinPluginsController.PutConfig);
            Assert.Equal(elevated ? "RequiresElevation" : null, auth.Policy);
        }

        var dispatch = typeof(MoonfinPluginsController).GetMethod(nameof(MoonfinPluginsController.Dispatch))!;
        Assert.Equal(
            new[] { "Plugins/{id}/{**path}" },
            dispatch.GetCustomAttributes(inherit: true).OfType<HttpMethodAttribute>().Select(attribute => attribute.Template).Distinct());
    }

    [Fact]
    public void FrameAncestorOriginsDropPathsWildcardsAndJunk()
    {
        var kept = PluginConfiguration.NormalizeFrameAncestors(new[]
        {
            "https://web.example.com",
            "https://web.example.com/moonfin",
            "https://*.example.com",
            "http://127.0.0.1:8096",
            "javascript:alert(1)",
            "https://evil.example.com/",
            "not a url",
            "HTTPS://Web.Example.com",
            "https://web.example.com",
            " http://localhost:8080 ",
        });

        Assert.Equal(
            new[] { "https://web.example.com", "http://127.0.0.1:8096", "http://localhost:8080" },
            kept);
    }

    [Fact]
    public async Task SandboxedWebHostGetsOneBridgeScriptAndALockedDownPagePolicy()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "moonfin-files-" + Guid.NewGuid().ToString("N")));
        WebApplication? app = null;
        try
        {
            const string page = "<!DOCTYPE html><header>not-head</header><head lang=\"en\"><title>Embed</title></head><body>EMBEDPAGE</body><head><title>second</title></head>";
            WritePlugin(root.FullName, "embed", ClientManifest("embed"), page);
            File.WriteAllText(Path.Combine(root.FullName, "embed", "client", "icon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");

            string[] configured =
            {
                "https://web.example.com",
                "https://web.example.com/moonfin",
                "https://*.example.com",
                "http://127.0.0.1:8096",
                "javascript:alert(1)",
                "https://evil.example.com/",
                "",
            };

            var catalog = new MoonbaseModuleCatalog();
            var registry = new MoonfinPluginRegistry(catalog, root.FullName);
            app = BuildFilesHost(registry, catalog, configured);
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

            var html = await client.GetAsync("/Moonfin/Plugins/embed/files/client/index.html");
            Assert.Equal(HttpStatusCode.OK, html.StatusCode);
            var htmlBody = await html.Content.ReadAsStringAsync();
            var head = "<head lang=\"en\">";
            Assert.Equal(1, CountOf(htmlBody, BridgeTag));
            Assert.Equal(htmlBody.IndexOf(head, StringComparison.Ordinal) + head.Length, htmlBody.IndexOf(BridgeTag, StringComparison.Ordinal));
            Assert.Contains("EMBEDPAGE", htmlBody, StringComparison.Ordinal);
            Assert.Contains("<header>not-head</header>", htmlBody, StringComparison.Ordinal);
            Assert.Equal("*", Header(html, "Access-Control-Allow-Origin"));
            var policy = Header(html, "Content-Security-Policy");
            Assert.Equal(ClientPage(app.Urls.Single(), configured), policy);
            var origin = new Uri(app.Urls.Single()).GetLeftPart(UriPartial.Authority);
            Assert.Contains(origin, policy, StringComparison.Ordinal);
            Assert.Contains("frame-ancestors 'self' https://web.example.com http://127.0.0.1:8096", policy, StringComparison.Ordinal);
            Assert.DoesNotContain("/moonfin", policy, StringComparison.Ordinal);
            Assert.DoesNotContain("*", policy, StringComparison.Ordinal);
            Assert.DoesNotContain("evil.example.com", policy, StringComparison.Ordinal);
            Assert.Contains(PluginClientPages.BridgeVersion, Header(html, "ETag"), StringComparison.Ordinal);

            var script = await client.GetAsync("/Moonfin/Plugins/embed/files/client/app.js");
            Assert.Equal(HttpStatusCode.OK, script.StatusCode);
            Assert.Equal("console.log('hello-app');", await script.Content.ReadAsStringAsync());
            Assert.Equal("*", Header(script, "Access-Control-Allow-Origin"));
            Assert.Equal(string.Empty, Header(script, "Content-Security-Policy"));

            var svg = await client.GetAsync("/Moonfin/Plugins/embed/files/client/icon.svg");
            Assert.Equal(HttpStatusCode.OK, svg.StatusCode);
            Assert.Equal("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>", await svg.Content.ReadAsStringAsync());
            Assert.Equal("*", Header(svg, "Access-Control-Allow-Origin"));
            Assert.Equal(policy, Header(svg, "Content-Security-Policy"));

            var bridge = await client.GetAsync("/Moonfin/PluginHost/bridge.js");
            Assert.Equal(HttpStatusCode.OK, bridge.StatusCode);
            Assert.Equal("application/javascript", bridge.Content.Headers.ContentType?.ToString());
            Assert.Equal("nosniff", Header(bridge, "X-Content-Type-Options"));
            Assert.Equal(PluginClientPages.BridgeEtag, Header(bridge, "ETag"));
            var bridgeBody = await bridge.Content.ReadAsStringAsync();
            Assert.Contains("window.MoFinHost", bridgeBody, StringComparison.Ordinal);
            Assert.Contains("mofinNonce", bridgeBody, StringComparison.Ordinal);
            Assert.Contains("MoFinPluginBridge", bridgeBody, StringComparison.Ordinal);
            Assert.Contains("postMessage", bridgeBody, StringComparison.Ordinal);

            using var cached = new HttpRequestMessage(HttpMethod.Get, "/Moonfin/PluginHost/bridge.js");
            cached.Headers.TryAddWithoutValidation("If-None-Match", Header(bridge, "ETag"));
            var notModified = await client.SendAsync(cached);
            Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
            Assert.Equal("nosniff", Header(notModified, "X-Content-Type-Options"));
            Assert.Equal(PluginClientPages.BridgeEtag, Header(notModified, "ETag"));

            var data = await client.GetAsync("/Moonfin/Plugins/embed/Data/notes");
            var proxy = await client.PostAsync("/Moonfin/Plugins/embed/Proxy", new StringContent("{}", Encoding.UTF8, "application/json"));
            var route = await client.GetAsync("/Moonfin/Plugins/embed/rows");
            Assert.Equal(HttpStatusCode.Unauthorized, data.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, proxy.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, route.StatusCode);
            Assert.Equal(string.Empty, Header(data, "Access-Control-Allow-Origin"));
            Assert.Equal(string.Empty, Header(proxy, "Access-Control-Allow-Origin"));
            Assert.Equal(string.Empty, Header(route, "Access-Control-Allow-Origin"));
            Assert.Equal(string.Empty, Header(bridge, "Access-Control-Allow-Origin"));
        }
        finally
        {
            if (app != null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }

            Directory.Delete(root.FullName, recursive: true);
        }
    }

    private static PluginListResponse List(MoonfinPluginRegistry registry, MoonbaseModuleCatalog catalog)
    {
        var action = new MoonfinPluginsController(registry, catalog).List();
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        return Assert.IsType<PluginListResponse>(ok.Value);
    }

    private static string Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            return string.Join(", ", values);
        }

        if (response.Content?.Headers.TryGetValues(name, out var content) == true)
        {
            return string.Join(", ", content);
        }

        return string.Empty;
    }

    private static async Task AssertSame(HttpResponseMessage expected, HttpResponseMessage actual)
    {
        Assert.Equal(expected.StatusCode, actual.StatusCode);
        Assert.Equal(expected.Content.Headers.ContentType?.ToString(), actual.Content.Headers.ContentType?.ToString());
        Assert.Equal(await expected.Content.ReadAsStringAsync(), await actual.Content.ReadAsStringAsync());
    }

    private static async Task<string> Raw(Uri baseUri, string request)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(baseUri.Host, baseUri.Port);
        await using var stream = tcp.GetStream();
        var bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private const string BridgeTag = "<script src=\"/Moonfin/PluginHost/bridge.js\"></script>";

    private static string ClientPage(string baseUrl, params string[] frameAncestors) =>
        PluginClientPages.Policy(new Uri(baseUrl).GetLeftPart(UriPartial.Authority), frameAncestors);

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ClientManifest(string id, string extra = "") =>
        "{\"id\":\"" + id + "\",\"name\":\"" + id + "\",\"version\":\"1.0.0\",\"description\":\"Hi\",\"provides\":[]," + extra + "\"slots\":[\"homeRow\"],\"client\":{\"html\":\"client/index.html\",\"js\":\"client/app.js\"},\"minHostApi\":1}";

    private static void WritePlugin(string root, string id, string json, string html)
    {
        var client = Directory.CreateDirectory(Path.Combine(root, id, "client"));
        Directory.CreateDirectory(Path.Combine(root, id, "server"));
        Directory.CreateDirectory(Path.Combine(root, id, "data"));
        File.WriteAllText(Path.Combine(root, id, "plugin.json"), json);
        File.WriteAllText(Path.Combine(root, id, "config.json"), "{\"secrets\":{\"apiKey\":\"" + Secret + "\"}}");
        File.WriteAllText(Path.Combine(client.FullName, "index.html"), html);
        File.WriteAllText(Path.Combine(client.FullName, "app.js"), "console.log('hello-app');");
        File.WriteAllText(Path.Combine(client.FullName, "native.dll"), "MZ-dll");
        File.WriteAllText(Path.Combine(client.FullName, "native.so"), "ELF");
        File.WriteAllText(Path.Combine(root, id, "server", "Hello.dll"), "MZ-server");
        File.WriteAllText(Path.Combine(root, id, "data", "secret.json"), "{\"token\":\"" + Secret + "\"}");
    }

    private static string ManifestWithClient(string id, string html, string? js = null)
    {
        var client = js == null
            ? "\"html\":\"" + html + "\""
            : "\"html\":\"" + html + "\",\"js\":\"" + js + "\"";
        return "{\"id\":\"" + id + "\",\"name\":\"" + id + "\",\"version\":\"1.0.0\",\"description\":\"Hi\",\"provides\":[],\"slots\":[\"homeRow\"],\"client\":{" + client + "},\"minHostApi\":1}";
    }

    private static void WriteRootPlugin(string root, string id, string html, string js, string page, string script)
    {
        WritePlugin(root, id, ManifestWithClient(id, html, js), "NESTED");
        File.WriteAllText(Path.Combine(root, id, "index.html"), page);
        File.WriteAllText(Path.Combine(root, id, "app.js"), script);
        File.WriteAllText(Path.Combine(root, id, "extra.html"), "EXTRA");
        File.WriteAllText(Path.Combine(root, id, "readme.txt"), "README");
    }

    private static WebApplication BuildFilesHost(
        MoonfinPluginRegistry registry,
        MoonbaseModuleCatalog catalog,
        IReadOnlyList<string>? frameAncestors = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = NoUserHandler.Name;
            options.DefaultChallengeScheme = NoUserHandler.Name;
        }).AddScheme<AuthenticationSchemeOptions, NoUserHandler>(NoUserHandler.Name, _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("RequiresElevation", policy => policy.RequireRole("Administrator"));
        });
        builder.Services.AddSingleton<IStartupFilter, PluginClientFileGuard>();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(catalog);
        if (frameAncestors != null)
        {
            builder.Services.AddSingleton<IPluginFrameAncestorProvider>(new FixedFrameAncestors(frameAncestors));
        }
        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            manager.ApplicationParts.Clear();
            manager.ApplicationParts.Add(new Microsoft.AspNetCore.Mvc.ApplicationParts.AssemblyPart(typeof(MoonfinPluginsController).Assembly));
            manager.FeatureProviders.Clear();
            manager.FeatureProviders.Add(new OnlyPluginController());
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        return app;
    }

    private static async Task AssertMissing(HttpClient client, string path, string absent)
    {
        var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(absent, body, StringComparison.Ordinal);
    }

    private sealed class OnlyPluginController : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == typeof(MoonfinPluginsController);
    }

    private sealed class FixedFrameAncestors : IPluginFrameAncestorProvider
    {
        public FixedFrameAncestors(IReadOnlyList<string> raw) => Raw = raw;

        public IReadOnlyList<string>? Raw { get; }
    }

    private sealed class NoUserHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string Name = "NoUser";

        public NoUserHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}
