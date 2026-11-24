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
          "stacks": [
            {
              "id": "stack-news",
              "name": "News",
              "members": ["stvp-2", "plu-1", "rok-3"],
              "primary": "stvp-2",
              "record": "https://rec.example/news.ts",
              "on": true,
              "group": "News",
              "chno": "5"
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
              "chno": 12
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
    public async Task SetPrimary_PostsTheStackWithThatMemberFirstAndClearsTheCache()
    {
        var handler = new StubHandler();
        handler.When("/mofin/free/pick/api/state", State);
        var client = Client(handler);

        await client.GetFeedsAsync("news", CancellationToken.None);
        var saved = await client.SetPrimaryAsync("news", "plu-1", CancellationToken.None);

        Assert.Equal(FreeTvCallStatus.Ok, saved.Status);
        var post = handler.Calls.Single(call => call.Method == "POST");
        Assert.Equal("/mofin/free/pick/api/save", post.Path);
        using var document = JsonDocument.Parse(post.Body!);
        Assert.Equal("plu-1", document.RootElement.GetProperty("primary").GetString());
        Assert.Equal("plu-1", document.RootElement.GetProperty("members")[0].GetString());
        Assert.Equal("stvp-2", document.RootElement.GetProperty("members")[1].GetString());
        Assert.Equal("rok-3", document.RootElement.GetProperty("members")[2].GetString());
        Assert.Equal("https://rec.example/news.ts", document.RootElement.GetProperty("record").GetString());
        Assert.True(document.RootElement.GetProperty("on").GetBoolean());
        Assert.Equal("News", document.RootElement.GetProperty("group").GetString());
        Assert.Equal("5", document.RootElement.GetProperty("chno").GetString());
        Assert.Equal("stack-news", document.RootElement.GetProperty("id").GetString());

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
        using var document = JsonDocument.Parse(handler.Calls.Single(call => call.Method == "POST").Body!);
        var members = document.RootElement.GetProperty("members");
        Assert.Equal("plu-8", members[0].GetProperty("memberId").GetString());
        Assert.Equal("Weather Pluto", members[0].GetProperty("name").GetString());
        Assert.Equal("plex-9", members[1].GetProperty("id").GetString());
        Assert.False(document.RootElement.GetProperty("on").GetBoolean());
        Assert.Equal(12, document.RootElement.GetProperty("chno").GetInt32());
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

        public Exception? Throw { get; set; }

        public bool Hang { get; set; }

        public void When(string path, string body) => _bodies[path] = body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Hang)
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
            Calls.Add(new Call(request.Method.Method, path, body));
            var payload = _bodies.TryGetValue(path, out var found) ? found : "{}";
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }

    private readonly record struct Call(string Method, string Path, string? Body);
}
