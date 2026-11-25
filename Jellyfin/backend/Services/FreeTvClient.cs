using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Moonfin.Server.Services;

/// <summary>
/// Reads and updates Free TV stacks. The LAN host is server-side only.
/// Play URLs use the public base. A timeout or a bad response becomes a
/// failed call and is never thrown at Jellyfin.
/// </summary>
public sealed class FreeTvClient
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpMessageHandler? _handler;
    private readonly Func<FreeTvConnection> _connection;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _timeout;
    private readonly object _cacheGate = new();
    private string? _cachedState;
    private DateTimeOffset _cachedAt;

    public FreeTvClient(IHttpClientFactory httpClientFactory)
        : this(httpClientFactory, null, ReadConfigured, () => DateTimeOffset.UtcNow, TimeSpan.FromSeconds(8))
    {
    }

    internal FreeTvClient(
        HttpMessageHandler? handler,
        Func<FreeTvConnection> connection,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? timeout = null)
        : this(null, handler, connection, clock ?? (() => DateTimeOffset.UtcNow), timeout ?? TimeSpan.FromSeconds(8))
    {
    }

    private FreeTvClient(
        IHttpClientFactory? httpClientFactory,
        HttpMessageHandler? handler,
        Func<FreeTvConnection> connection,
        Func<DateTimeOffset> clock,
        TimeSpan timeout)
    {
        _httpClientFactory = httpClientFactory;
        _handler = handler;
        _connection = connection;
        _clock = clock;
        _timeout = timeout;
    }

    public Task<FreeTvCall<IReadOnlyList<DirectStackFeed>>> GetFeedsAsync(string slug, CancellationToken cancellationToken) =>
        Guard(cancellationToken, async () =>
        {
            if (!IsSlug(slug))
            {
                return FreeTvCall<IReadOnlyList<DirectStackFeed>>.NotFound("Unknown stack.");
            }

            var connection = _connection();
            var state = await ReadStateAsync(connection, allowCache: true, cancellationToken).ConfigureAwait(false);
            if (!state.Ok || state.Body == null)
            {
                return state.Status == 404
                    ? FreeTvCall<IReadOnlyList<DirectStackFeed>>.NotFound("Unknown stack.")
                    : FreeTvCall<IReadOnlyList<DirectStackFeed>>.Upstream("Free TV could not be reached.");
            }

            IReadOnlyList<DirectStackFeed>? feeds;
            try
            {
                feeds = MapFeeds(state.Body, slug, ResolvePublicBase(state.Body, connection.PublicBase));
            }
            catch (JsonException)
            {
                return FreeTvCall<IReadOnlyList<DirectStackFeed>>.Upstream("Free TV returned a stack list that could not be read.");
            }

            return feeds == null
                ? FreeTvCall<IReadOnlyList<DirectStackFeed>>.NotFound("Unknown stack.")
                : FreeTvCall<IReadOnlyList<DirectStackFeed>>.Success(feeds);
        });

    public Task<FreeTvCall<bool>> SetPrimaryAsync(string slug, string? memberId, CancellationToken cancellationToken) =>
        Guard(cancellationToken, async () =>
        {
            if (!IsSlug(slug))
            {
                return FreeTvCall<bool>.NotFound("Unknown stack.");
            }

            var wanted = memberId?.Trim() ?? "";
            if (wanted.Length == 0)
            {
                return FreeTvCall<bool>.BadRequest("memberId is required.");
            }

            var connection = _connection();
            var state = await ReadStateAsync(connection, allowCache: false, cancellationToken).ConfigureAwait(false);
            if (!state.Ok || state.Body == null)
            {
                return state.Status == 404
                    ? FreeTvCall<bool>.NotFound("Unknown stack.")
                    : FreeTvCall<bool>.Upstream("Free TV could not be reached.");
            }

            string payload;
            try
            {
                var edited = EditStack(state.Body, slug, wanted);
                if (!edited.StackFound)
                {
                    return FreeTvCall<bool>.NotFound("Unknown stack.");
                }

                if (!edited.MemberFound || edited.Payload == null)
                {
                    return FreeTvCall<bool>.NotFound("Unknown member.");
                }

                payload = edited.Payload;
            }
            catch (JsonException)
            {
                return FreeTvCall<bool>.Upstream("Free TV returned a stack list that could not be read.");
            }

            var saved = await SendAsync(connection, HttpMethod.Post, "/mofin/free/pick/api/save", payload, cancellationToken).ConfigureAwait(false);
            if (saved.Status == 404)
            {
                return FreeTvCall<bool>.NotFound("Unknown stack.");
            }

            if (!saved.Ok)
            {
                return FreeTvCall<bool>.Upstream("Free TV could not be reached.");
            }

            ClearCache();
            return FreeTvCall<bool>.Success(true);
        });

    /// <summary>
    /// Null when the slug is not a stack. The list keeps the server's member order.
    /// </summary>
    internal static IReadOnlyList<DirectStackFeed>? MapFeeds(string stateJson, string slug, string publicBase)
    {
        var stack = FindStack(stateJson, slug);
        if (stack == null)
        {
            return null;
        }

        var members = stack["members"] as JsonArray;
        if (members == null)
        {
            return Array.Empty<DirectStackFeed>();
        }

        var primary = Text(stack["primary"]);
        var feeds = new List<DirectStackFeed>(members.Count);
        foreach (var member in members)
        {
            var id = MemberIdOf(member);
            if (id == null)
            {
                continue;
            }

            feeds.Add(new DirectStackFeed
            {
                MemberId = id,
                Provider = ProviderFor(id),
                Name = NameOf(member, id),
                PlayUrl = PlayUrl(publicBase, id),
                IsPrimary = primary != null && id.Equals(primary, StringComparison.OrdinalIgnoreCase),
            });
        }

        if (primary == null && feeds.Count > 0)
        {
            feeds[0] = feeds[0] with { IsPrimary = true };
        }

        return feeds;
    }

    /// <summary>
    /// The picker sets primary and moves that member to the front, then posts
    /// the whole document: channels (the state's selection, not the catalog),
    /// every stack, and settings. A missing selection is not replaced with {}
    /// — that would wipe the lineup. Every other stack and every other field
    /// is left as Free TV sent it.
    /// </summary>
    internal static StackEdit EditStack(string stateJson, string slug, string memberId)
    {
        var root = JsonNode.Parse(stateJson) as JsonObject
            ?? throw new JsonException("Free TV state was not an object.");
        var stack = FindStack(root, slug);
        if (stack == null)
        {
            return new StackEdit(null, false, false);
        }

        var members = stack["members"] as JsonArray;
        if (members == null)
        {
            return new StackEdit(null, true, false);
        }

        var index = -1;
        string? exact = null;
        for (var i = 0; i < members.Count; i++)
        {
            var id = MemberIdOf(members[i]);
            if (id != null && id.Equals(memberId, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                exact = id;
                break;
            }
        }

        if (index < 0 || exact == null)
        {
            return new StackEdit(null, true, false);
        }

        if (index > 0)
        {
            var chosen = members[index];
            members.RemoveAt(index);
            members.Insert(0, chosen);
        }

        stack["primary"] = exact;
        if (root["selection"] is not JsonObject selection)
        {
            throw new JsonException("Free TV state has no selection object to send as channels.");
        }

        var document = new JsonObject
        {
            ["channels"] = selection.DeepClone(),
            ["stacks"] = root["stacks"]!.DeepClone(),
            ["settings"] = CloneOrEmpty(root["settings"]),
        };
        return new StackEdit(document.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), true, true);
    }

    internal static string ProviderFor(string memberId)
    {
        var id = memberId.Trim();
        var dash = id.IndexOf('-');
        var prefix = (dash > 0 ? id[..dash] : id).ToLowerInvariant();
        return prefix switch
        {
            "plu" => "Pluto",
            "stvp" => "Samsung TV Plus",
            "rok" => "Roku",
            "plex" => "Plex",
            _ => prefix.Length == 0 ? "Feed" : prefix,
        };
    }

    internal static string PlayUrl(string publicBase, string memberId)
    {
        var root = publicBase.TrimEnd('/');
        return root + "/mofin/free/s/" + Uri.EscapeDataString(memberId) + ".m3u8";
    }

    internal static string ResolvePublicBase(string stateJson, string configured)
    {
        try
        {
            var root = JsonNode.Parse(stateJson) as JsonObject;
            if (root != null)
            {
                foreach (var key in new[] { "publicBase", "publicUrl", "public" })
                {
                    var advertised = PluginConfiguration.NormalizeSeerrUrl(Text(root[key]));
                    if (advertised != null)
                    {
                        return advertised;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // The caller reports a bad document. The configured base still builds a URL.
        }

        return configured;
    }

    internal static bool IsSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug.Length > 80 || slug is "." or "..")
        {
            return false;
        }

        foreach (var character in slug)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.')
            {
                return false;
            }
        }

        return true;
    }

    private async Task<(bool Ok, string? Body, int Status)> ReadStateAsync(
        FreeTvConnection connection,
        bool allowCache,
        CancellationToken cancellationToken)
    {
        if (allowCache)
        {
            lock (_cacheGate)
            {
                if (_cachedState != null && _clock() - _cachedAt < CacheTtl)
                {
                    return (true, _cachedState, 200);
                }
            }
        }

        var response = await SendAsync(connection, HttpMethod.Get, "/mofin/free/pick/api/state", null, cancellationToken).ConfigureAwait(false);
        if (!response.Ok || response.Body == null)
        {
            return (false, response.Body, response.Status);
        }

        lock (_cacheGate)
        {
            _cachedState = response.Body;
            _cachedAt = _clock();
        }

        return (true, response.Body, response.Status);
    }

    private void ClearCache()
    {
        lock (_cacheGate)
        {
            _cachedState = null;
        }
    }

    private async Task<Upstream> SendAsync(
        FreeTvConnection connection,
        HttpMethod method,
        string path,
        string? json,
        CancellationToken cancellationToken)
    {
        var client = CreateClient();
        try
        {
            using var request = new HttpRequestMessage(method, connection.LanUrl.TrimEnd('/') + path);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (json != null)
            {
                request.Headers.TryAddWithoutValidation("X-Mofin-Pick", "1");
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Content = content;
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new Upstream((int)response.StatusCode, body);
        }
        finally
        {
            client.Dispose();
        }
    }

    private HttpClient CreateClient()
    {
        if (_handler != null)
        {
            return new HttpClient(_handler, disposeHandler: false) { Timeout = _timeout };
        }

        var client = _httpClientFactory!.CreateClient();
        client.Timeout = _timeout;
        return client;
    }

    private async Task<FreeTvCall<T>> Guard<T>(CancellationToken cancellationToken, Func<Task<FreeTvCall<T>>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FreeTvCall<T>.Upstream("Free TV did not respond.");
        }
        catch (HttpRequestException)
        {
            return FreeTvCall<T>.Upstream("Free TV could not be reached.");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return FreeTvCall<T>.Upstream("Free TV could not be reached.");
        }
    }

    private static FreeTvConnection ReadConfigured()
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var lan = PluginConfiguration.NormalizeSeerrUrl(config?.FreeTvUrl) ?? PluginConfiguration.DefaultFreeTvUrl;
        var published = PluginConfiguration.NormalizeSeerrUrl(config?.FreeTvPublicUrl) ?? PluginConfiguration.DefaultFreeTvPublicUrl;
        return new FreeTvConnection(lan, published);
    }

    private static JsonObject? FindStack(string stateJson, string slug)
    {
        var root = JsonNode.Parse(stateJson) as JsonObject;
        return FindStack(root, slug);
    }

    private static JsonObject? FindStack(JsonObject? root, string slug)
    {
        if (root?["stacks"] is not JsonArray stacks)
        {
            return null;
        }

        foreach (var item in stacks)
        {
            if (item is JsonObject stack && StackMatches(stack, slug))
            {
                return stack;
            }
        }

        return null;
    }

    private static JsonNode CloneOrEmpty(JsonNode? node)
    {
        var cloned = node?.DeepClone();
        return cloned ?? new JsonObject();
    }

    private static bool StackMatches(JsonObject stack, string slug)
    {
        var id = Text(stack["id"]);
        var named = Text(stack["slug"]);
        if (Same(id, slug) || Same(named, slug) || Same(id, "stack-" + slug))
        {
            return true;
        }

        return id != null
            && id.StartsWith("stack-", StringComparison.OrdinalIgnoreCase)
            && Same(id["stack-".Length..], slug);
    }

    private static string? MemberIdOf(JsonNode? node)
    {
        var direct = Text(node);
        if (direct != null)
        {
            return direct;
        }

        if (node is not JsonObject obj)
        {
            return null;
        }

        return Text(obj["id"])
            ?? Text(obj["memberId"])
            ?? Text(obj["member"])
            ?? Text(obj["tvgId"])
            ?? Text(obj["tvg_id"]);
    }

    private static string NameOf(JsonNode? node, string memberId)
    {
        if (node is JsonObject obj)
        {
            var name = Text(obj["name"]) ?? Text(obj["title"]) ?? Text(obj["label"]);
            if (name != null)
            {
                return name;
            }
        }

        return memberId;
    }

    private static string? Text(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            var trimmed = text.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        return null;
    }

    private static bool Same(string? left, string right) =>
        left != null && left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private readonly record struct Upstream(int Status, string? Body)
    {
        public bool Ok => Status is >= 200 and < 300;
    }
}

internal sealed record FreeTvConnection(string LanUrl, string PublicBase);

internal readonly record struct StackEdit(string? Payload, bool StackFound, bool MemberFound);

public enum FreeTvCallStatus
{
    Ok,
    BadRequest,
    NotFound,
    Upstream,
}

public sealed class FreeTvCall<T>
{
    public FreeTvCallStatus Status { get; private init; }

    public T? Value { get; private init; }

    public string? Error { get; private init; }

    public static FreeTvCall<T> Success(T value) => new() { Status = FreeTvCallStatus.Ok, Value = value };

    public static FreeTvCall<T> BadRequest(string error) => new() { Status = FreeTvCallStatus.BadRequest, Error = error };

    public static FreeTvCall<T> NotFound(string error) => new() { Status = FreeTvCallStatus.NotFound, Error = error };

    public static FreeTvCall<T> Upstream(string error) => new() { Status = FreeTvCallStatus.Upstream, Error = error };
}

public sealed record DirectStackFeed
{
    [System.Text.Json.Serialization.JsonPropertyName("memberId")]
    public string MemberId { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("provider")]
    public string Provider { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("playUrl")]
    public string PlayUrl { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("isPrimary")]
    public bool IsPrimary { get; init; }
}
