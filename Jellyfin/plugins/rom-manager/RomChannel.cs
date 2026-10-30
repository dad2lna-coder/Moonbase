using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moonfin.Server.Plugins;

namespace Moonfin.Plugins.RomManager;

public sealed class LibrarySnapshot
{
    public required string Primary { get; init; }

    public required DateTimeOffset FetchedAt { get; init; }

    public bool Indexing { get; init; }

    public string? MetaJson { get; init; }

    public required IReadOnlyList<RomEntry> Items { get; init; }

    public required IReadOnlyList<GameRecord> Games { get; init; }

    public required Dictionary<string, GameRecord> ByKey { get; init; }

    public required HashSet<string> Paths { get; init; }

    public static LibrarySnapshot Create(string primary, DateTimeOffset fetchedAt, bool indexing, string? metaJson, IReadOnlyList<RomEntry> items)
    {
        var games = RomIndex.Group(items);
        var byKey = new Dictionary<string, GameRecord>(games.Count, StringComparer.Ordinal);
        foreach (var game in games)
        {
            byKey.TryAdd(game.Key, game);
        }

        var paths = new HashSet<string>(items.Count, StringComparer.Ordinal);
        foreach (var item in items)
        {
            paths.Add(item.Path);
        }

        return new LibrarySnapshot
        {
            Primary = primary,
            FetchedAt = fetchedAt,
            Indexing = indexing,
            MetaJson = metaJson,
            Items = items,
            Games = games,
            ByKey = byKey,
            Paths = paths,
        };
    }
}

public sealed class CoverMemory
{
    public const int MaxBytes = 40 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _order = new();
    private long _bytes;

    public bool TryGet(string key, out byte[] bytes)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var node))
            {
                bytes = Array.Empty<byte>();
                return false;
            }

            _order.Remove(node);
            _order.AddFirst(node);
            bytes = node.Value.Bytes;
            return true;
        }
    }

    public void Put(string key, byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > RomChannel.MaxImageBytes)
        {
            return;
        }

        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _bytes -= existing.Value.Bytes.Length;
                _order.Remove(existing);
                _map.Remove(key);
            }

            while (_bytes + bytes.Length > MaxBytes && _order.Last != null)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
                _bytes -= last.Value.Bytes.Length;
            }

            if (_bytes + bytes.Length > MaxBytes)
            {
                return;
            }

            var entry = new Entry(key, bytes);
            var node = new LinkedListNode<Entry>(entry);
            _order.AddFirst(node);
            _map[key] = node;
            _bytes += bytes.Length;
        }
    }

    private sealed record Entry(string Key, byte[] Bytes);
}

/// <summary>
/// Talks to Channel Manager. Only the paths in <see cref="AllowedPaths"/> are ever requested.
/// </summary>
public sealed class RomChannel : IDisposable
{
    public const string DefaultBaseUrl = "http://192.168.1.119:3002";
    public const string FallbackBaseUrl = "http://172.17.0.1:3002";
    public const string IndexingMessage = "Library is being indexed";
    public static readonly TimeSpan IndexTtl = TimeSpan.FromSeconds(60);
    public const int MaxImageBytes = 300 * 1024;
    public const int MaxDataUriChars = 240_000;
    public const int MaxCovers = 24;
    public const int MaxPaths = 50;
    public const int CoverParallelism = 4;
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan IndexTimeout = TimeSpan.FromSeconds(60);

    public static readonly IReadOnlySet<string> AllowedPaths = new HashSet<string>(StringComparer.Ordinal)
    {
        "/api/romman/index",
        "/api/romman/game",
        "/api/romman/thumb",
        "/api/romman/status",
        "/api/romman/rescan",
        "/api/romman/deps",
        "/api/romman/delete",
        "/api/romman/trash",
        "/api/romman/restore",
    };

    private readonly HttpClient _http;
    private readonly HttpMessageHandler? _ownedHandler;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private readonly object _publish = new();
    private readonly CoverMemory _covers = new();
    private LibrarySnapshot? _snapshot;
    private string? _etag;
    private int _generation;
    private int _disposed;

    public RomChannel(HttpMessageHandler? handler = null)
    {
        if (handler == null)
        {
            handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = false,
                ConnectTimeout = ConnectTimeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            };
            _ownedHandler = handler;
        }
        else if (handler is HttpClientHandler httpHandler)
        {
            httpHandler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            httpHandler.AllowAutoRedirect = false;
        }

        _http = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public Func<DateTimeOffset> Now { get; set; } = static () => DateTimeOffset.UtcNow;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _http.Dispose();
        _ownedHandler?.Dispose();
        _indexGate.Dispose();
    }

    public Task Invalidate()
    {
        lock (_publish)
        {
            _generation++;
            _snapshot = null;
            _etag = null;
        }

        return Task.CompletedTask;
    }

    public async Task<LibrarySnapshot> GetIndexAsync(IServiceProvider? services, CancellationToken cancellationToken)
    {
        var primary = PrimaryBase(services);
        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LibrarySnapshot? previous;
            int generation;
            lock (_publish)
            {
                generation = _generation;
                previous = _snapshot;
                if (previous != null
                    && string.Equals(previous.Primary, primary, StringComparison.Ordinal)
                    && Now() - previous.FetchedAt < IndexTtl)
                {
                    return previous;
                }
            }

            var response = await SendAsync(services, HttpMethod.Get, "/api/romman/index", null, useEtag: true, cancellationToken, IndexTimeout)
                .ConfigureAwait(false);
            LibrarySnapshot built;
            string? etag = null;
            if (response.Status == (int)HttpStatusCode.NotModified && previous != null)
            {
                built = LibrarySnapshot.Create(primary, Now(), indexing: false, previous.MetaJson, previous.Items);
            }
            else if (response.Status == (int)HttpStatusCode.Accepted)
            {
                built = previous != null
                    ? LibrarySnapshot.Create(primary, Now(), indexing: true, previous.MetaJson, previous.Items)
                    : LibrarySnapshot.Create(primary, Now(), indexing: true, null, Array.Empty<RomEntry>());
            }
            else if (response.Status is < 200 or > 299)
            {
                throw new ChannelException("Channel Manager returned " + response.Status + ".");
            }
            else
            {
                var items = RomIndex.Parse(response.Body, out var meta);
                built = LibrarySnapshot.Create(primary, Now(), indexing: false, meta, items);
                etag = response.Etag;
            }

            lock (_publish)
            {
                if (_generation == generation)
                {
                    if (!string.IsNullOrEmpty(etag))
                    {
                        _etag = etag;
                    }

                    _snapshot = built;
                }
            }

            return built;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    public async Task<string?> GetGameDetailsAsync(IServiceProvider? services, string path, CancellationToken cancellationToken)
    {
        var relative = "/api/romman/game?p=" + Uri.EscapeDataString(path);
        var response = await SendAsync(services, HttpMethod.Get, relative, null, useEtag: false, cancellationToken).ConfigureAwait(false);
        if (response.Status is < 200 or > 299 || string.IsNullOrWhiteSpace(response.Body))
        {
            return null;
        }

        return response.Body;
    }

    public async Task<string?> GetStatusAsync(IServiceProvider? services, CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendAsync(services, HttpMethod.Get, "/api/romman/status", null, useEtag: false, cancellationToken)
                .ConfigureAwait(false);
            if (response.Status is < 200 or > 299 || string.IsNullOrWhiteSpace(response.Body))
            {
                return null;
            }

            return response.Body;
        }
        catch (ChannelException)
        {
            return null;
        }
    }

    public async Task<ChannelResult> PostAsync(IServiceProvider? services, string path, string json, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var response = await SendAsync(services, HttpMethod.Post, path, bytes, useEtag: false, cancellationToken).ConfigureAwait(false);
        return new ChannelResult(response.Status, response.Body);
    }

    public async Task<ChannelResult> GetAsync(IServiceProvider? services, string path, CancellationToken cancellationToken)
    {
        var response = await SendAsync(services, HttpMethod.Get, path, null, useEtag: false, cancellationToken).ConfigureAwait(false);
        return new ChannelResult(response.Status, response.Body);
    }

    public async Task<byte[]?> GetThumbAsync(IServiceProvider? services, string path, string? lb, int width, bool trash, CancellationToken cancellationToken)
    {
        var key = (trash ? "t:" : "l:") + width + ":" + (lb ?? string.Empty) + ":" + path;
        if (_covers.TryGet(key, out var cached))
        {
            return cached;
        }

        var relative = "/api/romman/thumb?p=" + Uri.EscapeDataString(path) + "&w=" + width;
        if (!string.IsNullOrWhiteSpace(lb))
        {
            relative += "&lb=" + Uri.EscapeDataString(lb);
        }

        if (trash)
        {
            relative += "&trash=1";
        }

        var bytes = await SendBytesAsync(services, relative, cancellationToken).ConfigureAwait(false);
        if (bytes == null || bytes.Length == 0 || bytes.Length > MaxImageBytes)
        {
            return null;
        }

        if (!IsJpeg(bytes))
        {
            return null;
        }

        if (DataUriLength(bytes.Length) <= MaxDataUriChars)
        {
            _covers.Put(key, bytes);
        }

        return bytes;
    }

    public static string? ParseBaseUrl(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? raw = null;
            if (doc.RootElement.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object
                && settings.TryGetProperty("romManagerUrl", out var nested)
                && nested.ValueKind == JsonValueKind.String)
            {
                raw = nested.GetString();
            }

            if (string.IsNullOrWhiteSpace(raw)
                && doc.RootElement.TryGetProperty("romManagerUrl", out var top)
                && top.ValueKind == JsonValueKind.String)
            {
                raw = top.GetString();
            }

            return NormalizeBase(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? ConfiguredBase(IServiceProvider? services)
    {
        if (services?.GetService(typeof(MoonfinPluginRegistry)) is not MoonfinPluginRegistry registry)
        {
            return null;
        }

        try
        {
            return ParseBaseUrl(registry.ReadConfig("rom-manager"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    public static int DataUriLength(int rawBytes) =>
        "data:image/jpeg;base64,".Length + ((rawBytes + 2) / 3) * 4;

    public static bool IsJpeg(byte[] bytes) => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private string PrimaryBase(IServiceProvider? services) => ConfiguredBase(services) ?? DefaultBaseUrl;

    private IReadOnlyList<string> Bases(IServiceProvider? services)
    {
        var configured = ConfiguredBase(services);
        if (!string.IsNullOrEmpty(configured))
        {
            return new[] { configured };
        }

        return new[] { DefaultBaseUrl, FallbackBaseUrl };
    }

    private async Task<ChannelResult> SendAsync(
        IServiceProvider? services,
        HttpMethod method,
        string relative,
        byte[]? body,
        bool useEtag,
        CancellationToken cancellationToken,
        TimeSpan? budget = null)
    {
        var path = relative.Split('?', 2)[0];
        if (!AllowedPaths.Contains(path))
        {
            throw new InvalidOperationException("Blocked Channel Manager path.");
        }

        var bases = Bases(services);
        Exception? last = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget ?? RequestTimeout);
        for (var i = 0; i < bases.Count; i++)
        {
            try
            {
                using var request = new HttpRequestMessage(method, bases[i].TrimEnd('/') + relative);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
                request.Headers.Accept.ParseAdd("application/json");
                if (useEtag && !string.IsNullOrEmpty(_etag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
                }

                if (body != null)
                {
                    request.Content = new ByteArrayContent(body);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                }

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                var text = response.StatusCode == HttpStatusCode.NotModified
                    ? string.Empty
                    : await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                return new ChannelResult((int)response.StatusCode, text, ReadEtag(response));
            }
            catch (Exception ex) when (IsTransport(ex) && !cancellationToken.IsCancellationRequested)
            {
                last = ex;
                if (!timeout.IsCancellationRequested && i < bases.Count - 1)
                {
                    continue;
                }
            }
        }

        throw new ChannelException("Channel Manager is unreachable.", last);
    }

    private async Task<byte[]?> SendBytesAsync(IServiceProvider? services, string relative, CancellationToken cancellationToken)
    {
        var path = relative.Split('?', 2)[0];
        if (!AllowedPaths.Contains(path))
        {
            throw new InvalidOperationException("Blocked Channel Manager path.");
        }

        var bases = Bases(services);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        for (var i = 0; i < bases.Count; i++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, bases[i].TrimEnd('/') + relative);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
                request.Headers.Accept.ParseAdd("image/jpeg");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return null;
                }

                if (response.Content.Headers.ContentLength is long length && length > MaxImageBytes)
                {
                    return null;
                }

                return await ReadAtMostAsync(response.Content, MaxImageBytes, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransport(ex) && !cancellationToken.IsCancellationRequested)
            {
                if (!timeout.IsCancellationRequested && i < bases.Count - 1)
                {
                    continue;
                }

                return null;
            }
        }

        return null;
    }

    private static async Task<byte[]?> ReadAtMostAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[maxBytes + 1];
        var read = 0;
        while (read <= maxBytes)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        if (read == 0 || read > maxBytes)
        {
            return null;
        }

        var exact = new byte[read];
        buffer.AsSpan(0, read).CopyTo(exact);
        return exact;
    }

    private static string? ReadEtag(HttpResponseMessage response)
    {
        if (response.Headers.ETag != null)
        {
            var tag = response.Headers.ETag.Tag ?? string.Empty;
            if (tag.Length == 0)
            {
                return null;
            }

            if (!tag.StartsWith('"'))
            {
                tag = "\"" + tag + "\"";
            }

            return response.Headers.ETag.IsWeak ? "W/" + tag : tag;
        }

        return response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null;
    }

    private static string? NormalizeBase(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        var origin = uri.GetLeftPart(UriPartial.Authority);
        return string.IsNullOrEmpty(path) ? origin : origin + path;
    }

    private static bool IsTransport(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or ChannelException;

    public readonly record struct ChannelResult(int Status, string Body, string? Etag = null);
}

public sealed class ChannelException : Exception
{
    public ChannelException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
