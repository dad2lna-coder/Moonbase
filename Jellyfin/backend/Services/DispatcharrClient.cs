using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Moonfin.Server.Services;

/// <summary>
/// Server-side Dispatcharr calls. The API key is a request header and is never
/// copied into a result, an error, or a log line.
/// </summary>
public sealed class DispatcharrClient
{
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpMessageHandler? _handler;
    private readonly Func<DispatcharrConnection?> _connection;

    public DispatcharrClient(IHttpClientFactory httpClientFactory)
        : this(httpClientFactory, null, ReadConfigured)
    {
    }

    internal DispatcharrClient(HttpMessageHandler handler, Func<DispatcharrConnection?> connection)
        : this(null, handler, connection)
    {
    }

    private DispatcharrClient(
        IHttpClientFactory? httpClientFactory,
        HttpMessageHandler? handler,
        Func<DispatcharrConnection?> connection)
    {
        _httpClientFactory = httpClientFactory;
        _handler = handler;
        _connection = connection;
    }

    public Task<DispatcharrCall<IReadOnlyList<DirectChannelFeed>>> GetFeedsAsync(string channelId, CancellationToken cancellationToken) =>
        WithConnection(async (connection, token) =>
        {
            if (!IsChannelId(channelId))
            {
                return DispatcharrCall<IReadOnlyList<DirectChannelFeed>>.BadRequest("A numeric channel id is required.");
            }

            var streams = await SendAsync(connection, HttpMethod.Get, "/api/channels/channels/" + channelId + "/streams/", null, token).ConfigureAwait(false);
            if (!streams.Ok || streams.Body == null)
            {
                return MapFailure<IReadOnlyList<DirectChannelFeed>>(streams);
            }

            List<ParsedStream> parsed;
            try
            {
                parsed = ParseStreams(streams.Body);
            }
            catch (JsonException)
            {
                return DispatcharrCall<IReadOnlyList<DirectChannelFeed>>.Upstream("Dispatcharr returned a stream list that could not be read.");
            }

            var names = await AccountNamesAsync(connection, parsed, token).ConfigureAwait(false);
            var feeds = new List<DirectChannelFeed>(parsed.Count);
            for (var i = 0; i < parsed.Count; i++)
            {
                var stream = parsed[i];
                var account = stream.AccountName;
                if (string.IsNullOrWhiteSpace(account) && stream.AccountId is long accountId && names.TryGetValue(accountId, out var resolved))
                {
                    account = resolved;
                }

                feeds.Add(new DirectChannelFeed
                {
                    Id = stream.Id,
                    Name = stream.Name,
                    M3uAccountName = string.IsNullOrWhiteSpace(account) ? null : account,
                    StreamHash = stream.Hash,
                    PlayUrl = PlayUrl(connection.Url, stream.Hash),
                    IsPrimary = i == 0,
                });
            }

            return DispatcharrCall<IReadOnlyList<DirectChannelFeed>>.Success(feeds);
        }, cancellationToken);

    public Task<DispatcharrCall<bool>> SetPrimaryAsync(string channelId, string streamId, CancellationToken cancellationToken) =>
        WithConnection(async (connection, token) =>
        {
            if (!IsChannelId(channelId) || !IsChannelId(streamId))
            {
                return DispatcharrCall<bool>.BadRequest("A numeric channel id and stream id are required.");
            }

            var channel = await SendAsync(connection, HttpMethod.Get, "/api/channels/channels/" + channelId + "/", null, token).ConfigureAwait(false);
            if (!channel.Ok || channel.Body == null)
            {
                return MapFailure<bool>(channel);
            }

            List<long> ids;
            try
            {
                ids = ParseChannelStreamIds(channel.Body);
            }
            catch (JsonException)
            {
                return DispatcharrCall<bool>.Upstream("Dispatcharr returned a channel that could not be read.");
            }

            var wanted = long.Parse(streamId, CultureInfo.InvariantCulture);
            var index = ids.IndexOf(wanted);
            if (index < 0)
            {
                return DispatcharrCall<bool>.NotFound("That stream is not on this channel.");
            }

            if (index > 0)
            {
                ids.RemoveAt(index);
                ids.Insert(0, wanted);
            }

            var payload = "{\"streams\":[" + string.Join(",", ids) + "]}";
            var patched = await SendAsync(connection, HttpMethod.Patch, "/api/channels/channels/" + channelId + "/", payload, token).ConfigureAwait(false);
            return patched.Ok
                ? DispatcharrCall<bool>.Success(true)
                : MapFailure<bool>(patched);
        }, cancellationToken);

    public Task<DispatcharrCall<long>> LookupAsync(string? uuid, string? number, string? tvgId, CancellationToken cancellationToken) =>
        WithConnection(async (connection, token) =>
        {
            if (string.IsNullOrWhiteSpace(uuid) && string.IsNullOrWhiteSpace(number) && string.IsNullOrWhiteSpace(tvgId))
            {
                return DispatcharrCall<long>.BadRequest("uuid, number, or tvgId is required.");
            }

            var rows = new List<ChannelRow>();
            var summary = await SendAsync(connection, HttpMethod.Get, "/api/channels/channels/summary/", null, token).ConfigureAwait(false);
            if (summary.Status is 401 or 403)
            {
                return MapFailure<long>(summary);
            }

            if (summary.Ok && summary.Body != null)
            {
                try
                {
                    rows.AddRange(ParseSummary(summary.Body));
                }
                catch (JsonException)
                {
                    rows.Clear();
                }
            }

            var matched = SelectChannel(rows, uuid, number, tvgId);
            // Summary has uuid and number, not tvg-id. A missing summary endpoint falls through to the channel list.
            if (matched == null && (!summary.Ok || !string.IsNullOrWhiteSpace(tvgId)))
            {
                var listed = await ReadChannelPagesAsync(connection, token).ConfigureAwait(false);
                if (listed.Status != DispatcharrCallStatus.Ok || listed.Value == null)
                {
                    return listed.Status == DispatcharrCallStatus.Ok
                        ? DispatcharrCall<long>.NotFound("No matching Dispatcharr channel.")
                        : DispatcharrCall<long>.Fail(listed.Status, listed.Error);
                }

                matched = SelectChannel(listed.Value, uuid, number, tvgId);
            }

            return matched == null
                ? DispatcharrCall<long>.NotFound("No matching Dispatcharr channel.")
                : DispatcharrCall<long>.Success(matched.Value);
        }, cancellationToken);

    internal static long? SelectChannel(IReadOnlyList<ChannelRow> rows, string? uuid, string? number, string? tvgId)
    {
        if (!string.IsNullOrWhiteSpace(uuid))
        {
            foreach (var row in rows)
            {
                if (UuidEquals(row.Uuid, uuid))
                {
                    return row.Id;
                }
            }
        }

        if (TryNumber(number, out var wantedNumber))
        {
            var hits = new List<ChannelRow>();
            foreach (var row in rows)
            {
                if (NumberEquals(row.Number, wantedNumber) || NumberEquals(row.EffectiveNumber, wantedNumber))
                {
                    hits.Add(row);
                }
            }

            if (!string.IsNullOrWhiteSpace(tvgId))
            {
                var narrowed = hits.Where(row => TvgEquals(row, tvgId)).ToList();
                if (narrowed.Count > 0)
                {
                    hits = narrowed;
                }
            }

            if (hits.Count > 0)
            {
                return hits[0].Id;
            }
        }

        if (!string.IsNullOrWhiteSpace(tvgId))
        {
            foreach (var row in rows)
            {
                if (TvgEquals(row, tvgId))
                {
                    return row.Id;
                }
            }
        }

        return null;
    }

    internal static string? PlayUrl(string baseUrl, string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }

        return baseUrl.TrimEnd('/') + "/proxy/ts/stream/" + Uri.EscapeDataString(hash.Trim());
    }

    private async Task<Dictionary<long, string>> AccountNamesAsync(
        DispatcharrConnection connection,
        List<ParsedStream> streams,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<long, string>();
        var missing = false;
        foreach (var stream in streams)
        {
            if (string.IsNullOrWhiteSpace(stream.AccountName) && stream.AccountId != null)
            {
                missing = true;
                break;
            }
        }

        if (!missing)
        {
            return names;
        }

        var accounts = await SendAsync(connection, HttpMethod.Get, "/api/m3u/accounts/?page_size=1000", null, cancellationToken).ConfigureAwait(false);
        if (!accounts.Ok || accounts.Body == null)
        {
            return names;
        }

        try
        {
            using var document = JsonDocument.Parse(accounts.Body);
            foreach (var account in EnumerateRows(document.RootElement))
            {
                if (ReadId(account) is long id && ReadString(account, "name") is string name && name.Length > 0)
                {
                    names[id] = name;
                }
            }
        }
        catch (JsonException)
        {
            return names;
        }

        return names;
    }

    private async Task<DispatcharrCall<IReadOnlyList<ChannelRow>>> ReadChannelPagesAsync(
        DispatcharrConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new List<ChannelRow>();
        var path = "/api/channels/channels/?page_size=1000";
        for (var page = 0; page < 10 && path != null; page++)
        {
            var response = await SendAsync(connection, HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
            if (!response.Ok || response.Body == null)
            {
                return MapFailure<IReadOnlyList<ChannelRow>>(response);
            }

            try
            {
                using var document = JsonDocument.Parse(response.Body);
                foreach (var channel in EnumerateRows(document.RootElement))
                {
                    if (ReadId(channel) is long id)
                    {
                        rows.Add(ReadChannel(channel, id));
                    }
                }

                path = document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("next", out var next)
                    && next.ValueKind == JsonValueKind.String
                    ? SameOriginPath(connection.Url, next.GetString())
                    : null;
            }
            catch (JsonException)
            {
                return DispatcharrCall<IReadOnlyList<ChannelRow>>.Upstream("Dispatcharr returned a channel list that could not be read.");
            }
        }

        return DispatcharrCall<IReadOnlyList<ChannelRow>>.Success(rows);
    }

    private async Task<DispatcharrCall<T>> WithConnection<T>(
        Func<DispatcharrConnection, CancellationToken, Task<DispatcharrCall<T>>> action,
        CancellationToken cancellationToken)
    {
        var connection = _connection();
        if (connection == null)
        {
            return DispatcharrCall<T>.NotConfigured("Dispatcharr is not configured.");
        }

        try
        {
            return await action(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DispatcharrCall<T>.Upstream("Dispatcharr did not respond.");
        }
        catch (HttpRequestException)
        {
            return DispatcharrCall<T>.Upstream("Dispatcharr could not be reached.");
        }
    }

    private async Task<UpstreamResponse> SendAsync(
        DispatcharrConnection connection,
        HttpMethod method,
        string pathAndQuery,
        string? json,
        CancellationToken cancellationToken)
    {
        var client = CreateClient();
        try
        {
            using var request = new HttpRequestMessage(method, connection.Url + pathAndQuery);
            request.Headers.TryAddWithoutValidation("Authorization", "ApiKey " + connection.ApiKey);
            request.Headers.TryAddWithoutValidation("X-API-Key", connection.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (json != null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new UpstreamResponse((int)response.StatusCode, body);
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
            return new HttpClient(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        }

        var client = _httpClientFactory!.CreateClient("MoonfinHttpClient");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private static DispatcharrConnection? ReadConfigured()
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var url = PluginConfiguration.NormalizeDispatcharrUrl(config?.DispatcharrUrl);
        var key = config?.DispatcharrApiKey?.Trim();
        if (url == null || string.IsNullOrEmpty(key))
        {
            return null;
        }

        return new DispatcharrConnection(url, key);
    }

    private static List<ParsedStream> ParseStreams(string json)
    {
        using var document = JsonDocument.Parse(json);
        var parsed = new List<ParsedStream>();
        foreach (var stream in EnumerateRows(document.RootElement))
        {
            if (ReadId(stream) is not long id)
            {
                continue;
            }

            parsed.Add(new ParsedStream(
                id,
                ReadString(stream, "name") ?? "",
                ReadString(stream, "stream_hash"),
                AccountName(stream),
                AccountId(stream)));
        }

        return parsed;
    }

    private static List<long> ParseChannelStreamIds(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("streams", out var streams))
        {
            root = streams;
        }

        var ids = new List<long>();
        if (root.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }

        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                if (ReadId(item) is long id)
                {
                    ids.Add(id);
                }
            }
            else if (ReadLong(item) is long bare)
            {
                ids.Add(bare);
            }
        }

        return ids;
    }

    private static List<ChannelRow> ParseSummary(string json)
    {
        using var document = JsonDocument.Parse(json);
        var rows = new List<ChannelRow>();
        foreach (var channel in EnumerateRows(document.RootElement))
        {
            if (ReadId(channel) is long id)
            {
                rows.Add(ReadChannel(channel, id));
            }
        }

        return rows;
    }

    private static ChannelRow ReadChannel(JsonElement channel, long id) => new(
        id,
        ReadString(channel, "uuid"),
        ReadNumber(channel, "channel_number"),
        ReadString(channel, "tvg_id"),
        ReadString(channel, "effective_tvg_id"),
        ReadNumber(channel, "effective_channel_number"));

    private static IEnumerable<JsonElement> EnumerateRows(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray();
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            return results.EnumerateArray();
        }

        return Array.Empty<JsonElement>();
    }

    private static string? AccountName(JsonElement stream)
    {
        var named = ReadString(stream, "m3u_account_name");
        if (!string.IsNullOrWhiteSpace(named))
        {
            return named;
        }

        if (!stream.TryGetProperty("m3u_account", out var account))
        {
            return null;
        }

        if (account.ValueKind == JsonValueKind.Object)
        {
            return ReadString(account, "name");
        }

        return account.ValueKind == JsonValueKind.String ? account.GetString() : null;
    }

    private static long? AccountId(JsonElement stream)
    {
        if (!stream.TryGetProperty("m3u_account", out var account))
        {
            return null;
        }

        if (account.ValueKind == JsonValueKind.Object && account.TryGetProperty("id", out var id))
        {
            return ReadLong(id);
        }

        return ReadLong(account);
    }

    private static long? ReadId(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("id", out var id) ? ReadLong(id) : null;

    private static long? ReadLong(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string? ReadNumber(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetRawText();
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool UuidEquals(string? candidate, string wanted)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var left = candidate.Trim();
        var right = wanted.Trim();
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Guid.TryParse(left, out var leftId) && Guid.TryParse(right, out var rightId) && leftId == rightId;
    }

    private static bool TryNumber(string? text, out decimal number)
    {
        number = 0;
        return !string.IsNullOrWhiteSpace(text)
            && decimal.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static bool NumberEquals(string? candidate, decimal wanted) =>
        TryNumber(candidate, out var parsed) && parsed == wanted;

    private static bool TvgEquals(ChannelRow row, string tvgId)
    {
        var wanted = tvgId.Trim();
        return string.Equals(row.TvgId?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(row.EffectiveTvg?.Trim(), wanted, StringComparison.OrdinalIgnoreCase);
    }

    private static string? SameOriginPath(string baseUrl, string? next)
    {
        if (string.IsNullOrWhiteSpace(next) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin))
        {
            return null;
        }

        if (!Uri.TryCreate(next, UriKind.Absolute, out var absolute))
        {
            return next.StartsWith('/') ? next : null;
        }

        if (!string.Equals(absolute.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(absolute.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
            || absolute.Port != origin.Port)
        {
            return null;
        }

        return absolute.PathAndQuery;
    }

    private static bool IsChannelId(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(char.IsAsciiDigit) && value != "0" && value.Length <= 18;

    private static DispatcharrCall<T> MapFailure<T>(UpstreamResponse response)
    {
        if (response.Status == 404)
        {
            return DispatcharrCall<T>.NotFound("Dispatcharr has no channel with that id.");
        }

        if (response.Status is 401 or 403)
        {
            return DispatcharrCall<T>.Upstream("Dispatcharr rejected the API key.");
        }

        return DispatcharrCall<T>.Upstream("Dispatcharr could not complete the request.");
    }

    private readonly record struct ParsedStream(long Id, string Name, string? Hash, string? AccountName, long? AccountId);

    private readonly record struct UpstreamResponse(int Status, string? Body)
    {
        public bool Ok => Status is >= 200 and < 300;
    }
}

internal sealed record DispatcharrConnection(string Url, string ApiKey);

internal readonly record struct ChannelRow(
    long Id,
    string? Uuid,
    string? Number,
    string? TvgId,
    string? EffectiveTvg,
    string? EffectiveNumber);

public enum DispatcharrCallStatus
{
    Ok,
    NotConfigured,
    BadRequest,
    NotFound,
    Upstream,
}

public sealed class DispatcharrCall<T>
{
    public DispatcharrCallStatus Status { get; private init; }

    public T? Value { get; private init; }

    public string? Error { get; private init; }

    public static DispatcharrCall<T> Success(T value) => new() { Status = DispatcharrCallStatus.Ok, Value = value };

    public static DispatcharrCall<T> NotConfigured(string error) => Fail(DispatcharrCallStatus.NotConfigured, error);

    public static DispatcharrCall<T> BadRequest(string error) => Fail(DispatcharrCallStatus.BadRequest, error);

    public static DispatcharrCall<T> NotFound(string error) => Fail(DispatcharrCallStatus.NotFound, error);

    public static DispatcharrCall<T> Upstream(string error) => Fail(DispatcharrCallStatus.Upstream, error);

    public static DispatcharrCall<T> Fail(DispatcharrCallStatus status, string? error) =>
        new() { Status = status, Error = error };
}

public sealed class DirectChannelFeed
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public long Id { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("m3uAccountName")]
    public string? M3uAccountName { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("streamHash")]
    public string? StreamHash { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("playUrl")]
    public string? PlayUrl { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("isPrimary")]
    public bool IsPrimary { get; init; }
}
