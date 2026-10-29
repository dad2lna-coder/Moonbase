using Microsoft.Extensions.DependencyInjection;
using Moonfin.Server.Plugins;

namespace Moonfin.Plugins.Hello;

/// <summary>
/// Sample server half. GET /Moonfin/Plugins/hello/rows returns one home-row item.
/// </summary>
public sealed class HelloModule : IMoonbaseModule
{
    public string Id => "hello";

    public string Version => "1.0.0";

    public IReadOnlyCollection<string> Provides { get; } = new[] { "hello.homeRow" };

    public IReadOnlyCollection<string> Requires { get; } = Array.Empty<string>();

    public void RegisterServices(IServiceCollection services)
    {
    }

    public void MapRoutes(IMoonbaseRouteTable routes)
    {
        routes.MapGet("rows", static _ => Task.FromResult(PluginRouteResult.Json(new
        {
            id = "hello",
            title = "Hello",
            pluginId = "hello",
            items = new[]
            {
                new { id = "hello-1", title = "Hello, Moonfin", year = "2026", kind = "movie" },
            },
        })));
    }
}
