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

    public bool Nosniff { get; init; }
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

    /// <summary>
    /// JSON and images pass through. Everything else is forced to text/plain so a plugin proxy cannot serve active content.
    /// </summary>
    public static (string ContentType, bool Nosniff) PresentUpstream(string? mediaType)
    {
        var raw = mediaType?.Trim() ?? string.Empty;
        var essence = raw.Split(';')[0].Trim();
        if (essence.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || essence.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || essence.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return (string.IsNullOrEmpty(raw) ? essence : raw, false);
        }

        return ("text/plain; charset=utf-8", true);
    }

    public static bool TryAppendQuerySecret(Uri upstream, string? param, string secret, out Uri? signed, out string? error)
    {
        signed = null;
        error = null;
        if (!PluginManifest.IsValidToken(param))
        {
            error = "The allowlisted query parameter is not valid.";
            return false;
        }

        var existing = upstream.Query;
        if (existing.StartsWith('?'))
        {
            existing = existing[1..];
        }

        var kept = new List<string>();
        if (existing.Length > 0)
        {
            foreach (var pair in existing.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var rawKey = eq >= 0 ? pair[..eq] : pair;
                string key;
                try
                {
                    key = Uri.UnescapeDataString(rawKey);
                }
                catch (UriFormatException)
                {
                    key = rawKey;
                }

                if (string.Equals(key, param, StringComparison.Ordinal))
                {
                    continue;
                }

                kept.Add(pair);
            }
        }

        kept.Add(Uri.EscapeDataString(param!) + "=" + Uri.EscapeDataString(secret ?? string.Empty));
        signed = new Uri(upstream.GetLeftPart(UriPartial.Path) + "?" + string.Join("&", kept));
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

        var inject = string.IsNullOrWhiteSpace(rule.Inject) ? "header" : rule.Inject.Trim();
        var query = request.Query;
        if (inject.Equals("query", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(rule.Param)
            && query is { Count: > 0 })
        {
            query = new Dictionary<string, string>(query, StringComparer.Ordinal);
            query.Remove(rule.Param);
        }

        if (!TryBuildUpstream(rule.BaseUrl, request.Path, query, out var uri, out _))
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

        if (inject.Equals("query", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(secret))
            {
                return Error(StatusCodes.Status409Conflict, "The allowlisted secret is not set.");
            }

            if (!TryAppendQuerySecret(uri!, rule.Param, secret, out var signed, out _))
            {
                return Error(StatusCodes.Status409Conflict, "The allowlisted query parameter is not valid.");
            }

            uri = signed;
        }
        else if (!inject.Equals("header", StringComparison.OrdinalIgnoreCase))
        {
            return Error(StatusCodes.Status409Conflict, "The allowlisted inject mode is not valid.");
        }

        using var message = new HttpRequestMessage(new HttpMethod(method), uri);
        if (inject.Equals("header", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(rule.Header)
            && !string.IsNullOrEmpty(secret))
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

            var presented = PresentUpstream(response.Content.Headers.ContentType?.MediaType);
            return new PluginProxyResult
            {
                StatusCode = (int)response.StatusCode,
                ContentType = presented.ContentType,
                Body = body,
                Nosniff = presented.Nosniff,
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
                Inject = AsString(rule["inject"]),
                Header = AsString(rule["header"]),
                Scheme = AsString(rule["scheme"]),
                Param = AsString(rule["param"]),
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

        public string? Inject { get; init; }

        public string? Header { get; init; }

        public string? Scheme { get; init; }

        public string? Param { get; init; }

        public string? Secret { get; init; }
    }
}
