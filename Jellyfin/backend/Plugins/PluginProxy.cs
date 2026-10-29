using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace Moonfin.Server.Plugins;

public sealed class PluginProxyRequestBody
{
    public string? Name { get; set; }

    public string? Path { get; set; }

    public string? Method { get; set; }

    public Dictionary<string, string>? Query { get; set; }
}

public sealed class PluginProxyResult
{
    public int StatusCode { get; init; }

    public string ContentType { get; init; } = "application/json; charset=utf-8";

    public string Body { get; init; } = string.Empty;
}

/// <summary>
/// Calls an admin-allowlisted URL and adds the plugin's secret as a header. The secret never
/// goes back to the client, including on failure.
/// </summary>
public static class PluginProxy
{
    public const int MaxResponseBytes = 1024 * 1024;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public static bool TryBuildUpstream(
        string? baseUrl,
        string? path,
        IReadOnlyDictionary<string, string>? query,
        out Uri? uri,
        out string? error)
    {
        uri = null;
        error = null;
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            error = "Allowlist URL must be absolute http or https.";
            return false;
        }

        if ((baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp) || !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            error = "Allowlist URL must be absolute http or https.";
            return false;
        }

        var relative = (path ?? string.Empty).Replace('\\', '/').Trim();
        if (relative.StartsWith("/", StringComparison.Ordinal))
        {
            relative = relative.TrimStart('/');
        }

        if (relative.Contains("..", StringComparison.Ordinal)
            || relative.Contains("://", StringComparison.Ordinal)
            || relative.Contains('?', StringComparison.Ordinal)
            || relative.Contains('#', StringComparison.Ordinal)
            || relative.Contains('%', StringComparison.Ordinal)
            || relative.Contains('\0', StringComparison.Ordinal))
        {
            error = "Path is not allowed.";
            return false;
        }

        var prefix = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? baseUri.AbsoluteUri : baseUri.AbsoluteUri + "/";
        if (!Uri.TryCreate(prefix + relative, UriKind.Absolute, out var built))
        {
            error = "Path is not allowed.";
            return false;
        }

        if (!string.Equals(built.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(built.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
            || built.Port != baseUri.Port)
        {
            error = "Path escaped the allowlisted host.";
            return false;
        }

        if (query is { Count: > 0 })
        {
            var builder = new StringBuilder(built.GetLeftPart(UriPartial.Path));
            builder.Append('?');
            var first = true;
            foreach (var pair in query)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Contains("..", StringComparison.Ordinal) || pair.Key.Contains('\r') || pair.Key.Contains('\n'))
                {
                    error = "Query is not allowed.";
                    return false;
                }

                if (!first)
                {
                    builder.Append('&');
                }

                first = false;
                builder.Append(Uri.EscapeDataString(pair.Key));
                builder.Append('=');
                builder.Append(Uri.EscapeDataString(pair.Value ?? string.Empty));
            }

            built = new Uri(builder.ToString());
        }

        uri = built;
        return true;
    }

    public static async Task<PluginProxyResult> SendAsync(
        string? storedConfig,
        PluginProxyRequestBody request,
        CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
        {
            return Error(StatusCodes.Status400BadRequest, "Name an allowlisted proxy.");
        }

        var method = string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method.Trim().ToUpperInvariant();
        if (method != "GET" && method != "POST")
        {
            return Error(StatusCodes.Status400BadRequest, "Only GET and POST are proxied.");
        }

        var rule = FindRule(storedConfig, request.Name);
        if (rule == null)
        {
            return Error(StatusCodes.Status404NotFound, "That proxy is not allowlisted.");
        }

        if (!TryBuildUpstream(rule.BaseUrl, request.Path, request.Query, out var uri, out _))
        {
            return Error(StatusCodes.Status400BadRequest, "That path is not allowed.");
        }

        string? secret = null;
        if (!string.IsNullOrWhiteSpace(rule.Secret))
        {
            secret = PluginConfigStore.Secret(storedConfig, rule.Secret);
            if (string.IsNullOrEmpty(secret))
            {
                return Error(StatusCodes.Status409Conflict, "The allowlisted secret is not set.");
            }
        }

        using var message = new HttpRequestMessage(new HttpMethod(method), uri);
        if (!string.IsNullOrWhiteSpace(rule.Header) && !string.IsNullOrEmpty(secret))
        {
            if (rule.Header.Contains('\r') || rule.Header.Contains('\n') || rule.Header.Contains(':'))
            {
                return Error(StatusCodes.Status409Conflict, "The allowlisted header is not valid.");
            }

            var headerValue = string.IsNullOrWhiteSpace(rule.Scheme) ? secret : rule.Scheme.Trim() + " " + secret;
            if (!message.Headers.TryAddWithoutValidation(rule.Header, headerValue))
            {
                return Error(StatusCodes.Status409Conflict, "The allowlisted header is not valid.");
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return Error(StatusCodes.Status502BadGateway, "Upstream request failed.");
        }

        using (response)
        {
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                return Error(StatusCodes.Status502BadGateway, "Upstream redirect was blocked.");
            }

            var bytes = await ReadBounded(response.Content, cancellationToken).ConfigureAwait(false);
            var body = Encoding.UTF8.GetString(bytes);
            if (!string.IsNullOrEmpty(secret))
            {
                body = body.Replace(secret, PluginConfigStore.RedactedMarker, StringComparison.Ordinal);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            return new PluginProxyResult
            {
                StatusCode = (int)response.StatusCode,
                ContentType = mediaType,
                Body = body,
            };
        }
    }

    private static ProxyRule? FindRule(string? storedConfig, string name)
    {
        if (string.IsNullOrWhiteSpace(storedConfig) || JsonNode.Parse(storedConfig) is not JsonObject root)
        {
            return null;
        }

        if (root["proxyAllow"] is not JsonArray rules)
        {
            return null;
        }

        foreach (var node in rules)
        {
            if (node is not JsonObject rule)
            {
                continue;
            }

            var ruleName = AsString(rule["name"]);
            if (!string.Equals(ruleName, name, StringComparison.Ordinal))
            {
                continue;
            }

            return new ProxyRule
            {
                BaseUrl = AsString(rule["baseUrl"]),
                Header = AsString(rule["header"]),
                Scheme = AsString(rule["scheme"]),
                Secret = AsString(rule["secret"]),
            };
        }

        return null;
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static async Task<byte[]> ReadBounded(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (buffer.Length < MaxResponseBytes)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var room = MaxResponseBytes - (int)buffer.Length;
            buffer.Write(chunk, 0, Math.Min(read, room));
            if (read > room)
            {
                break;
            }
        }

        return buffer.ToArray();
    }

    private static PluginProxyResult Error(int status, string message) =>
        new()
        {
            StatusCode = status,
            Body = "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(message) + "}",
        };

    private sealed class ProxyRule
    {
        public string? BaseUrl { get; init; }

        public string? Header { get; init; }

        public string? Scheme { get; init; }

        public string? Secret { get; init; }
    }
}
