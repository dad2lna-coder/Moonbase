using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Moonfin.Server.Plugins;

/// <summary>
/// Server half of a Moonbase plugin. Built-in modules live in this assembly; installed
/// modules are loaded from <c>{data}/moonfin-plugins/{id}</c> on startup.
/// </summary>
public interface IMoonbaseModule
{
    string Id { get; }

    string Version { get; }

    /// <summary>Capability flags this module actually serves. A flag is not advertised unless it is listed here and the module is active.</summary>
    IReadOnlyCollection<string> Provides { get; }

    /// <summary>Flags that must already be active or this module stays dormant.</summary>
    IReadOnlyCollection<string> Requires { get; }

    void RegisterServices(IServiceCollection services);

    void MapRoutes(IMoonbaseRouteTable routes)
    {
    }

    void OnStart(IServiceProvider services)
    {
    }

    void OnStop()
    {
    }
}

/// <summary>Routes a module maps under <c>/Moonfin/Plugins/{id}/{path}</c>.</summary>
public interface IMoonbaseRouteTable
{
    void MapGet(string relativePath, PluginRouteHandler handler);

    void MapPost(string relativePath, PluginRouteHandler handler);

    void MapPut(string relativePath, PluginRouteHandler handler);

    void MapDelete(string relativePath, PluginRouteHandler handler);
}

public delegate Task<PluginRouteResult> PluginRouteHandler(PluginRouteRequest request);

public sealed class PluginRouteRequest
{
    public string Method { get; init; } = "GET";

    public string Path { get; init; } = string.Empty;

    public string? Body { get; init; }

    public Guid? UserId { get; init; }

    public bool IsAdmin { get; init; }

    public IServiceProvider? Services { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

public sealed class PluginRouteResult
{
    public int StatusCode { get; init; } = StatusCodes.Status200OK;

    public string ContentType { get; init; } = "application/json; charset=utf-8";

    public string Body { get; init; } = string.Empty;

    public static PluginRouteResult Json(object value, int statusCode = StatusCodes.Status200OK)
    {
        return new PluginRouteResult
        {
            StatusCode = statusCode,
            Body = JsonSerializer.Serialize(value, PluginJson.CamelCase),
        };
    }

    public static PluginRouteResult Text(string body, int statusCode = StatusCodes.Status200OK) =>
        new()
        {
            StatusCode = statusCode,
            ContentType = "text/plain; charset=utf-8",
            Body = body,
        };
}

internal static class PluginJson
{
    public static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public static readonly JsonSerializerOptions Manifest = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
