using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Serialization;
using Moonfin.Server.Services;
using Newtonsoft.Json;
using Xunit;

namespace Moonfin.Server.Tests;

public class DispatcharrClientTests
{
    private const string BaseUrl = "http://dispatcharr.example:9191";
    private const string ApiKey = "super-secret-key";

    [Fact]
    public async Task GetFeeds_ReturnsOrderNameAccountAndPlayUrl_WithoutTheKeyOrProviderUrl()
    {
        var handler = new StubHandler();
        handler.When("/api/channels/channels/7/streams/", """
            [
              {"id":4,"name":"Backup","url":"http://prov.example/live/user/pass/4.ts","m3u_account":2,"stream_hash":"hash-b"},
              {"id":9,"name":"Main","url":"http://prov.example/live/user/pass/9.ts","m3u_account":{"id":3,"name":"Sports"},"stream_hash":"hash-a"}
            ]
            """);
        handler.When("/api/m3u/accounts/?page_size=1000", """
            {"results":[{"id":2,"name":"Movies"},{"id":3,"name":"Sports"}]}
            """);
        var client = new DispatcharrClient(handler, () => new DispatcharrConnection(BaseUrl, ApiKey));

        var result = await client.GetFeedsAsync("7", CancellationToken.None);

        Assert.Equal(DispatcharrCallStatus.Ok, result.Status);
        var feeds = Assert.IsAssignableFrom<IReadOnlyList<DirectChannelFeed>>(result.Value);
        Assert.Equal(2, feeds.Count);
        Assert.True(feeds[0].IsPrimary);
        Assert.False(feeds[1].IsPrimary);
        Assert.Equal(4, feeds[0].Id);
        Assert.Equal("Backup", feeds[0].Name);
        Assert.Equal("Movies", feeds[0].M3uAccountName);
        Assert.Equal("hash-b", feeds[0].StreamHash);
        Assert.Equal(BaseUrl + "/proxy/ts/stream/hash-b", feeds[0].PlayUrl);
        Assert.Equal("Sports", feeds[1].M3uAccountName);
        Assert.Equal(BaseUrl + "/proxy/ts/stream/hash-a", feeds[1].PlayUrl);

        var json = System.Text.Json.JsonSerializer.Serialize(feeds);
        Assert.DoesNotContain(ApiKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain("pass", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcharrApiKey", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcharrUrl", json, StringComparison.Ordinal);
        Assert.Equal("ApiKey " + ApiKey, handler.Calls[0].Auth);
        Assert.Equal(ApiKey, handler.Calls[0].ApiKey);
        Assert.DoesNotContain(ApiKey, handler.Calls[0].Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetPrimary_PatchesStreamsWithTheChosenIdFirst()
    {
        var handler = new StubHandler();
        handler.When("/api/channels/channels/7/", """{"id":7,"streams":[4,9,2]}""");
        var client = new DispatcharrClient(handler, () => new DispatcharrConnection(BaseUrl, ApiKey));

        var result = await client.SetPrimaryAsync("7", "9", CancellationToken.None);

        Assert.Equal(DispatcharrCallStatus.Ok, result.Status);
        Assert.Equal("PATCH", handler.Calls[1].Method);
        Assert.Equal("/api/channels/channels/7/", handler.Calls[1].Uri);
        Assert.Equal("{\"streams\":[9,4,2]}", handler.Calls[1].Body);
        Assert.DoesNotContain(ApiKey, handler.Calls[1].Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetPrimary_WhenTheStreamIsMissing_DoesNotPatch()
    {
        var handler = new StubHandler();
        handler.When("/api/channels/channels/7/", """{"id":7,"streams":[4,2]}""");
        var client = new DispatcharrClient(handler, () => new DispatcharrConnection(BaseUrl, ApiKey));

        var result = await client.SetPrimaryAsync("7", "9", CancellationToken.None);

        Assert.Equal(DispatcharrCallStatus.NotFound, result.Status);
        Assert.Single(handler.Calls);
        Assert.DoesNotContain(ApiKey, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lookup_PrefersUuidThenNumberThenTvgId()
    {
        var handler = new StubHandler();
        handler.When("/api/channels/channels/summary/", """
            [
              {"id":3,"uuid":"abc","channel_number":5},
              {"id":8,"uuid":"def","channel_number":12.1}
            ]
            """);
        handler.When("/api/channels/channels/?page_size=1000", """
            {"results":[
              {"id":3,"uuid":"abc","channel_number":5,"tvg_id":"news"},
              {"id":8,"uuid":"def","channel_number":12.1,"tvg_id":"espn.us"},
              {"id":11,"uuid":"ghi","channel_number":40,"effective_tvg_id":"cnn.us"}
            ]}
            """);
        var client = new DispatcharrClient(handler, () => new DispatcharrConnection(BaseUrl, ApiKey));

        var byUuid = await client.LookupAsync("DEF", null, "espn.us", CancellationToken.None);
        var byNumber = await client.LookupAsync(null, "12.10", null, CancellationToken.None);
        var byTvg = await client.LookupAsync(null, null, "CNN.US", CancellationToken.None);

        Assert.Equal(8, byUuid.Value);
        Assert.Equal(8, byNumber.Value);
        Assert.Equal(11, byTvg.Value);
        Assert.Contains(handler.Calls, call => call.Uri.Contains("page_size=1000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lookup_WithoutAClue_IsABadRequest_AndDoesNotCallDispatcharr()
    {
        var handler = new StubHandler();
        var client = new DispatcharrClient(handler, () => new DispatcharrConnection(BaseUrl, ApiKey));

        var result = await client.LookupAsync(" ", null, null, CancellationToken.None);

        Assert.Equal(DispatcharrCallStatus.BadRequest, result.Status);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Calls_WhenDispatcharrIsNotConfigured_DoNotMentionAKey()
    {
        var client = new DispatcharrClient(new StubHandler(), () => null);

        var result = await client.GetFeedsAsync("7", CancellationToken.None);

        Assert.Equal(DispatcharrCallStatus.NotConfigured, result.Status);
        Assert.DoesNotContain("key", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectChannel_NumberAndTvgNarrowsADuplicateNumber()
    {
        var rows = new[]
        {
            new ChannelRow(1, "a", "5", "one", null, null),
            new ChannelRow(2, "b", "5", "two", null, null),
        };

        Assert.Equal(2, DispatcharrClient.SelectChannel(rows, null, "5", "two"));
        Assert.Equal(1, DispatcharrClient.SelectChannel(rows, null, "5", null));
    }

    [Fact]
    public void ConfigurationJsonOmitsDispatcharrSecrets_AndStillAcceptsThemOnWrite()
    {
        var stored = new PluginConfiguration
        {
            DispatcharrUrl = "http://secret-host.example",
            DispatcharrApiKey = ApiKey,
        };

        var systemText = System.Text.Json.JsonSerializer.Serialize(stored);
        var newtonsoft = JsonConvert.SerializeObject(stored);
        Assert.DoesNotContain(ApiKey, systemText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-host", systemText, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcharrUrl", systemText, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcharrApiKey", systemText, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, newtonsoft, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-host", newtonsoft, StringComparison.Ordinal);

        var posted = """{"DispatcharrUrl":"http://d.example:9191/","DispatcharrApiKey":"next-key"}""";
        var fromSystem = System.Text.Json.JsonSerializer.Deserialize<PluginConfiguration>(posted);
        var fromNewtonsoft = JsonConvert.DeserializeObject<PluginConfiguration>(posted);
        Assert.Equal("http://d.example:9191", fromSystem!.DispatcharrUrl);
        Assert.Equal("next-key", fromSystem.DispatcharrApiKey);
        Assert.Equal("http://d.example:9191", fromNewtonsoft!.DispatcharrUrl);
        Assert.Equal("next-key", fromNewtonsoft.DispatcharrApiKey);

        var blank = new PluginConfiguration();
        blank.RetainDispatcharrSecrets(stored);
        Assert.Equal("http://secret-host.example", blank.DispatcharrUrl);
        Assert.Equal(ApiKey, blank.DispatcharrApiKey);

        var cleared = System.Text.Json.JsonSerializer.Deserialize<PluginConfiguration>("""{"DispatcharrApiKey":""}""");
        cleared!.RetainDispatcharrSecrets(stored);
        Assert.Null(cleared.DispatcharrApiKey);
        Assert.Equal("http://secret-host.example", cleared.DispatcharrUrl);
    }

    [Fact]
    public void ConfigurationXml_RoundTripsTheSecrets()
    {
        var config = new PluginConfiguration
        {
            DispatcharrUrl = "http://d.example",
            DispatcharrApiKey = ApiKey,
        };

        using var stream = new MemoryStream();
        new XmlSerializer(typeof(PluginConfiguration)).Serialize(stream, config);
        stream.Position = 0;
        var read = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(stream)!;

        Assert.Equal("http://d.example", read.DispatcharrUrl);
        Assert.Equal(ApiKey, read.DispatcharrApiKey);
        Assert.Contains(ApiKey, Encoding.UTF8.GetString(stream.ToArray()), StringComparison.Ordinal);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal);

        public List<Call> Calls { get; } = new();

        public void When(string pathAndQuery, string body) => _bodies[pathAndQuery] = body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var path = request.RequestUri!.PathAndQuery;
            Calls.Add(new Call(
                request.Method.Method,
                path,
                request.Headers.TryGetValues("Authorization", out var auth) ? string.Join(",", auth) : null,
                request.Headers.TryGetValues("X-API-Key", out var key) ? string.Join(",", key) : null,
                body));
            var payload = _bodies.TryGetValue(path, out var found) ? found : "[]";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }

    private readonly record struct Call(string Method, string Uri, string? Auth, string? ApiKey, string? Body);
}
