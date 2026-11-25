using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Api;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public class FreeTvClientTests
{
    private const string Lan = "http://192.168.1.119:3004";
    private const string PublicBase = "https://dad2lna.duckdns.org:8443";

    private const string State = """
        {
          "publicBase": "https://tv.example:8443",
          "channels": [
            {"id": "catalog-news", "name": "Catalog only"}
          ],
          "selection": {
            "stack-news": "stvp-2",
            "weather": {"id": "plex-9", "keep": true}
          },
          "settings": {
            "theme": "night",
            "guideDays": 7
          },
          "stacks": [
            {
              "id": "stack-news",
              "name": "News",
              "members": ["stvp-2", "plu-1", "rok-3"],
              "primary": "stvp-2",
              "record": "https://rec.example/news.ts",
              "on": true,
              "group": "News",
              "chno": "5",
              "lang": "en"
            },
            {
              "id": "weather",
              "name": "Weather",
              "members": [
                {"id": "plex-9", "name": "Weather Plex"},
                {"memberId": "plu-8", "name": "Weather Pluto"}
              ],
              "primary": "plex-9",
              "record": "",
              "on": false,
              "group": "Weather",
              "chno": 12,
              "lang": "es"
            }
          ]
        }
        """;

    [Fact]
    public async Task GetFeeds_MapsMembersProviderPlayUrlAndPrimary()
    {
        var handler = new StubHandler();
        handler.When("/mofin/free/pick/api/state", State);
        var client = Client(handler);

        var result = await client.GetFeedsAsync("news", CancellationToken.None);

        Assert.Equal(FreeTvCallStatus.Ok, result.Status);
        var feeds = Assert.IsAssignableFrom<IReadOnlyList<DirectStackFeed>>(result.Value);
        Assert.Equal(3, feeds.Count);
        Assert.Equal("stvp-2", feeds[0].MemberId);
        Assert.Equal("Samsung TV Plus", feeds[0].Provider);
        Assert.True(feeds[0].IsPrimary);
        Assert.Equal("https://tv.example:8443/mofin/free/s/stvp-2.m3u8", feeds[0].PlayUrl);
        Assert.Equal("Pluto", feeds[1].Provider);
        Assert.False(feeds[1].IsPrimary);
        Assert.Equal("Roku", feeds[2].Provider);
        Assert.Equal("https://tv.example:8443/mofin/free/s/plu-1.m3u8", feeds[1].PlayUrl);
        Assert.DoesNotContain(Lan, JsonSerializer.Serialize(feeds), StringComparison.Ordinal);

        var objects = await client.GetFeedsAsync("weather", CancellationToken.None);
        var weather = Assert.IsAssignableFrom<IReadOnlyList<DirectStackFeed>>(objects.Value);
        Assert.Equal("Plex", weather[0].Provider);
        Assert.Equal("Weather Plex", weather[0].Name);
        Assert.True(weather[0].IsPrimary);
        Assert.Equal("Pluto", weather[1].Provider);
        Assert.Equal("Weather Pluto", weather[1].Name);
    }

    [Fact]
    public async Task GetFeeds_UsesTheConfiguredPublicBaseWhenStateHasNone()
    {
        var handler = new StubHandler();
        handler.When("/mofin/free/pick/api/state", """
            {"stacks":[{"id":"news","members":["plu-1"],"primary":"plu-1","on":true}]}
            """);
        var client = Client(handler);

        var result = await client.GetFeedsAsync("news", CancellationToken.None);

        Assert.Equal(PublicBase + "/mofin/free/s/plu-1.m3u8", result.Value![0].PlayUrl);
    }

    [Fact]
    public async Task GetFeeds_CachesStateForThirtySeconds()
    {
        var handler = new StubHandler();
        handler.When("/mofin/free/pick/api/state", State);
        var now = DateTimeOffset.Parse("2026-11-24T12:00:00Z");
        var client = Client(handler, () => now);

        await client.GetFeedsAsync("news", CancellationToken.None);
        await client.GetFeedsAsync("news", CancellationToken.None);
        Assert.Single(handler.Calls);

        now = now.AddSeconds(31);
        await client.GetFeedsAsync("news", CancellationToken.None);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task SetPrimary_PostsThePickerDocumentAndClearsTheCache()
    {
        var handler = new StubHandler();
        handler.When("/mofin/free/pick/api/state", State);
        var client = Client(handler);

        await client.GetFeedsAsync("news", CancellationToken.None);
        var saved = await client.SetPrimaryAsync("news", "plu-1", CancellationToken.None);

        Assert.Equal(FreeTvCallStatus.Ok, saved.Status);
        var post = handler.Calls.Single(call => call.Method == "POST");
        Assert.Equal("/mofin/free/pick/api/save", post.Path);
        Assert.Equal("1", post.Pick);
        Assert.Equal("application/json", post.ContentType);
        using var document = JsonDocument.Parse(post.Body!);
        var root = document.RootElement;
        Assert.Equal(3, root.EnumerateObject().Count());
        Assert.False(root.TryGetProperty("id", out _));
        Assert.False(root.TryGetProperty("members", out _));
        Assert.False(root.TryGetProperty("selection", out _));
        Assert.False(root.TryGetProperty("publicBase", out _));

        var channels = root.GetProperty("channels");
        Assert.Equal(JsonValueKind.Object, channels.ValueKind);
        Assert.Equal("stvp-2", channels.GetProperty("stack-news").GetString());
        Assert.Equal("plex-9", channels.GetProperty("weather").GetProperty("id").GetString());
        Assert.True(channels.GetProperty("weather").GetProperty("keep").GetBoolean());
        Assert.False(channels.TryGetProperty("0", out _));
        Assert.NotEqual("catalog-news", channels.TryGetProperty("id", out var catalogId) ? catalogId.GetString() : null);

        var settings = root.GetProperty("settings");
        Assert.Equal("night", settings.GetProperty("theme").GetString());
        Assert.Equal(7, settings.GetProperty("guideDays").GetInt32());

        var stacks = root.GetProperty("stacks");
        Assert.Equal(2, stacks.GetArrayLength());
        var news = stacks[0];
        Assert.Equal("plu-1", news.GetProperty("primary").GetString());
        Assert.Equal("plu-1", news.GetProperty("members")[0].GetString());
        Assert.Equal("stvp-2", news.GetProperty("members")[1].GetString());
        Assert.Equal("rok-3", news.GetProperty("members")[2].GetString());
        Assert.Equal("https://rec.example/news.ts", news.GetProperty("record").GetString());
        Assert.True(news.GetProperty("on").GetBoolean());
        Assert.Equal("News", news.GetProperty("group").GetString());
        Assert.Equal("News", news.GetProperty("name").GetString());
        Assert.Equal("5", news.GetProperty("chno").GetString());
        Assert.Equal("en", news.GetProperty("lang").GetString());
        Assert.Equal("stack-news", news.GetProperty("id").GetString());

        var weather = stacks[1];
        Assert.Equal("weather", weather.GetProperty("id").GetString());
        Assert.Equal("plex-9", weather.GetProperty("primary").GetString());
        Assert.Equal("plex-9", weather.GetProperty("members")[0].GetProperty("id").GetString());
        Assert.Equal("plu-8", weather.GetProperty("members")[1].GetProperty("memberId").GetString());
        Assert.Equal("Weather", weather.GetProperty("name").GetString());
        Assert.Equal("", weather.GetProperty("record").GetString());
        Assert.False(weather.GetProperty("on").GetBoolean());
        Assert.Equal("Weather", weather.GetProperty("group").GetString());
        Assert.Equal(12, weather.GetProperty("chno").GetInt32());
        Assert.Equal("es", weather.GetProperty("lang").GetString());

        await client.GetFeedsAsync("news", CancellationToken.None);
        Assert.Equal(3, handler.Calls.Count(call => call.Method == "GET"));
    }

    [Fact]
    public async Task SetPrimary_MovesAnObjectMemberAndKeepsItsFields()
    {
        var handler = new StubHandler();
        handler.When("/mofin/free/pick/api/state", State);
        var client = Client(handler);

        var saved = await client.SetPrimaryAsync("weather", "plu-8", CancellationToken.None);

        Assert.Equal(FreeTvCallStatus.Ok, saved.Status);
        var post = handler.Calls.Single(call => call.Method == "POST");
        Assert.Equal("1", post.Pick);
        Assert.Equal("application/json", post.ContentType);
        using var document = JsonDocument.Parse(post.Body!);
        var root = document.RootElement;
        Assert.Equal("stvp-2", root.GetProperty("channels").GetProperty("stack-news").GetString());
        Assert.Equal("night", root.GetProperty("settings").GetProperty("theme").GetString());
        var stacks = root.GetProperty("stacks");
        Assert.Equal("stack-news", stacks[0].GetProperty("id").GetString());
        Assert.Equal("stvp-2", stacks[0].GetProperty("primary").GetString());
        var members = stacks[1].GetProperty("members");
        Assert.Equal("plu-8", members[0].GetProperty("memberId").GetString());
        Assert.Equal("Weather Pluto", members[0].GetProperty("name").GetString());
        Assert.Equal("plex-9", members[1].GetProperty("id").GetString());
        Assert.Equal("Weather Plex", members[1].GetProperty("name").GetString());
        Assert.Equal("plu-8", stacks[1].GetProperty("primary").GetString());
        Assert.Equal("Weather", stacks[1].GetProperty("name").GetString());
        Assert.Equal("", stacks[1].GetProperty("record").GetString());
        Assert.False(stacks[1].GetProperty("on").GetBoolean());
        Assert.Equal("Weather", stacks[1].GetProperty("group").GetString());
        Assert.Equal(12, stacks[1].GetProperty("chno").GetInt32());
        Assert.Equal("es", stacks[1].GetProperty("lang").GetString());
    }

    [Fact]
    public async Task SetPrimary_UnknownStackOrMember_DoesNotPost()
    {
        var handler = new StubHandler();
        handler.When("/mofin/free/pick/api/state", State);
        var client = Client(handler);

        var missingStack = await client.SetPrimaryAsync("sports", "plu-1", CancellationToken.None);
        var missingMember = await client.SetPrimaryAsync("news", "plex-nope", CancellationToken.None);
        var blank = await client.SetPrimaryAsync("news", " ", CancellationToken.None);

        Assert.Equal(FreeTvCallStatus.NotFound, missingStack.Status);
        Assert.Equal(FreeTvCallStatus.NotFound, missingMember.Status);
        Assert.Equal(FreeTvCallStatus.BadRequest, blank.Status);
        Assert.DoesNotContain(handler.Calls, call => call.Method == "POST");
    }

    [Fact]
    public async Task SetPrimary_WithoutASelectionObject_DoesNotPost()
    {
        var handler = new StubHandler();
        handler.When("/mofin/free/pick/api/state", """
            {"stacks":[{"id":"news","members":["plu-1"],"primary":"plu-1","name":"News"}],"channels":[{"id":"catalog"}]}
            """);
        var client = Client(handler);

        var saved = await client.SetPrimaryAsync("news", "plu-1", CancellationToken.None);

        Assert.Equal(FreeTvCallStatus.Upstream, saved.Status);
        Assert.DoesNotContain(handler.Calls, call => call.Method == "POST");
    }

    [Fact]
    public async Task GetFeeds_UnknownStack_IsNotFound()
    {
        var handler = new StubHandler();
        handler.When("/mofin/free/pick/api/state", State);
        var client = Client(handler);

        var result = await client.GetFeedsAsync("../etc", CancellationToken.None);

        Assert.Equal(FreeTvCallStatus.NotFound, result.Status);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Calls_WhenFreeTvIsDownOrSlow_DoNotThrow()
    {
        var down = new StubHandler { Status = HttpStatusCode.InternalServerError };
        var downResult = await Client(down).GetFeedsAsync("news", CancellationToken.None);
        Assert.Equal(FreeTvCallStatus.Upstream, downResult.Status);

        var gateway = new StubHandler { Status = HttpStatusCode.BadGateway };
        var gatewayResult = await Client(gateway).SetPrimaryAsync("news", "plu-1", CancellationToken.None);
        Assert.Equal(FreeTvCallStatus.Upstream, gatewayResult.Status);

        var broken = new StubHandler { Throw = new HttpRequestException("refused") };
        var brokenResult = await Client(broken).SetPrimaryAsync("news", "plu-1", CancellationToken.None);
        Assert.Equal(FreeTvCallStatus.Upstream, brokenResult.Status);

        var hanging = new StubHandler { Hang = true };
        var slow = Client(hanging, timeout: TimeSpan.FromMilliseconds(200));
        var timed = await slow.GetFeedsAsync("news", CancellationToken.None);
        Assert.Equal(FreeTvCallStatus.Upstream, timed.Status);
        Assert.Contains("respond", timed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetPrimary_WhenSaveFails_DoesNotThrowAndKeepsTheCache()
    {
        var handler = new StubHandler { PostStatus = HttpStatusCode.BadGateway };
        handler.When("/mofin/free/pick/api/state", State);
        var now = DateTimeOffset.Parse("2026-11-24T12:00:00Z");
        var client = Client(handler, () => now);

        await client.GetFeedsAsync("news", CancellationToken.None);
        var saved = await client.SetPrimaryAsync("news", "plu-1", CancellationToken.None);

        Assert.Equal(FreeTvCallStatus.Upstream, saved.Status);
        Assert.Contains(handler.Calls, call => call.Method == "POST" && call.Pick == "1");
        Assert.Equal(2, handler.Calls.Count(call => call.Method == "GET"));

        await client.GetFeedsAsync("news", CancellationToken.None);
        Assert.Equal(2, handler.Calls.Count(call => call.Method == "GET"));

        var hanging = new StubHandler { HangPost = true };
        hanging.When("/mofin/free/pick/api/state", State);
        var slow = Client(hanging, () => now, TimeSpan.FromMilliseconds(200));
        await slow.GetFeedsAsync("news", CancellationToken.None);
        var timed = await slow.SetPrimaryAsync("news", "plu-1", CancellationToken.None);
        Assert.Equal(FreeTvCallStatus.Upstream, timed.Status);
        Assert.Contains("respond", timed.Error, StringComparison.OrdinalIgnoreCase);
        var gets = hanging.Calls.Count(call => call.Method == "GET");
        await slow.GetFeedsAsync("news", CancellationToken.None);
        Assert.Equal(gets, hanging.Calls.Count(call => call.Method == "GET"));
    }

    [Fact]
    public async Task Calls_WhenTheBodyIsNotJson_DoNotThrow()
    {
        var handler = new StubHandler();
        handler.When("/mofin/free/pick/api/state", "<html>nope</html>");
        var client = Client(handler);

        var feeds = await client.GetFeedsAsync("news", CancellationToken.None);
        var save = await client.SetPrimaryAsync("news", "plu-1", CancellationToken.None);

        Assert.Equal(FreeTvCallStatus.Upstream, feeds.Status);
        Assert.Equal(FreeTvCallStatus.Upstream, save.Status);
    }

    [Theory]
    [InlineData("plu-1", "Pluto")]
    [InlineData("stvp-44", "Samsung TV Plus")]
    [InlineData("rok-2", "Roku")]
    [InlineData("plex-9", "Plex")]
    public void ProviderFor_UsesTheIdPrefix(string memberId, string provider)
    {
        Assert.Equal(provider, FreeTvClient.ProviderFor(memberId));
    }

    [Fact]
    public void StacksController_IsForAnySignedInUser_NotLiveTvManagement()
    {
        var auth = typeof(DirectIptvStacksController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .ToList();
        Assert.Contains(auth, attribute => string.IsNullOrEmpty(attribute.Policy));
        Assert.DoesNotContain(auth, attribute => attribute.Policy == "LiveTvManagement");
        Assert.DoesNotContain(
            typeof(DirectIptvStacksController).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
            method => method.GetCustomAttributes<AuthorizeAttribute>().Any(attribute => attribute.Policy == "LiveTvManagement"));

        var legacy = typeof(DirectIptvChannelsController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal("LiveTvManagement", legacy?.Policy);
    }

    [Fact]
    public async Task Controller_RejectsACallerWithNoUser_AndDoesNotCallFreeTv()
    {
        var handler = new StubHandler();
        handler.When("/mofin/free/pick/api/state", State);
        var controller = ControllerFor(handler, signedIn: false);

        var feeds = await controller.Feeds("news", CancellationToken.None);
        var save = await controller.SetPrimary("news", new PrimaryMemberBody { MemberId = "plu-1" }, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(feeds.Result);
        Assert.IsType<UnauthorizedResult>(save);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Controller_SignedInUser_GetsFeedsAndNoContent()
    {
        var handler = new StubHandler();
        handler.When("/mofin/free/pick/api/state", State);
        var controller = ControllerFor(handler, signedIn: true);

        var feeds = await controller.Feeds("news", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(feeds.Result);
        var list = Assert.IsAssignableFrom<IReadOnlyList<DirectStackFeed>>(ok.Value);
        Assert.Equal("Samsung TV Plus", list[0].Provider);

        var save = await controller.SetPrimary("news", new PrimaryMemberBody { MemberId = "rok-3" }, CancellationToken.None);
        Assert.IsType<NoContentResult>(save);

        var missing = await controller.SetPrimary("news", new PrimaryMemberBody { MemberId = "nope" }, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(missing);
    }

    [Fact]
    public void Defaults_MatchTheLanServerAndThePublicBase()
    {
        var config = new PluginConfiguration();
        Assert.Equal(PluginConfiguration.DefaultFreeTvUrl, config.FreeTvUrl);
        Assert.Equal("http://192.168.1.119:3004", config.FreeTvUrl);
        Assert.Equal("https://dad2lna.duckdns.org:8443", config.FreeTvPublicUrl);
    }

    private static FreeTvClient Client(
        StubHandler handler,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? timeout = null) =>
        new(handler, () => new FreeTvConnection(Lan, PublicBase), clock, timeout);

    private static DirectIptvStacksController ControllerFor(StubHandler handler, bool signedIn)
    {
        var controller = new DirectIptvStacksController(Client(handler));
        var identity = signedIn
            ? new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", Guid.NewGuid().ToString("D")) }, "test")
            : new ClaimsIdentity();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        return controller;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal);

        public List<Call> Calls { get; } = new();

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public HttpStatusCode PostStatus { get; set; } = HttpStatusCode.OK;

        public Exception? Throw { get; set; }

        public bool Hang { get; set; }

        public bool HangPost { get; set; }

        public void When(string path, string body) => _bodies[path] = body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Hang || (HangPost && request.Method == HttpMethod.Post))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            if (Throw != null)
            {
                throw Throw;
            }

            var body = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var path = request.RequestUri!.PathAndQuery;
            var pick = request.Headers.TryGetValues("X-Mofin-Pick", out var values)
                ? string.Join(",", values)
                : null;
            Calls.Add(new Call(request.Method.Method, path, body, pick, request.Content?.Headers.ContentType?.MediaType));
            var payload = _bodies.TryGetValue(path, out var found) ? found : "{}";
            var status = request.Method == HttpMethod.Post ? PostStatus : Status;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }

    private readonly record struct Call(string Method, string Path, string? Body, string? Pick, string? ContentType);
}
