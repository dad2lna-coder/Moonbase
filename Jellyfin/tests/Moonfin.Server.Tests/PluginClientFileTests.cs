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
            Assert.Equal(ClientPage(), Header(html, "Content-Security-Policy"));
            Assert.Equal("nosniff", Header(html, "X-Content-Type-Options"));
            Assert.Equal("no-cache", Header(html, "Cache-Control"));
            var etag = Header(html, "ETag");
            Assert.False(string.IsNullOrEmpty(etag));
            var htmlBody = await html.Content.ReadAsStringAsync();
            Assert.Contains("Hello", htmlBody, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, htmlBody, StringComparison.Ordinal);

            var script = await client.GetAsync("/Moonfin/Plugins/hello/files/client/app.js");
            Assert.Equal(HttpStatusCode.OK, script.StatusCode);
            Assert.Equal("application/javascript", script.Content.Headers.ContentType?.ToString());
            Assert.Equal("nosniff", Header(script, "X-Content-Type-Options"));
            Assert.Equal("no-cache", Header(script, "Cache-Control"));
            Assert.Equal(string.Empty, Header(script, "Content-Security-Policy"));
            Assert.Contains("hello-app", await script.Content.ReadAsStringAsync(), StringComparison.Ordinal);

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

    private static string ClientPage() =>
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'; frame-ancestors 'self'; object-src 'none'; base-uri 'self'";

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

    private sealed class OnlyPluginController : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == typeof(MoonfinPluginsController);
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
