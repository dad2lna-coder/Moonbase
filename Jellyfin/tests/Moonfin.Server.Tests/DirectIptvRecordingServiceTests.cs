using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public class DirectIptvRecordingServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-direct-rec-{Guid.NewGuid():N}");
    private readonly DateTimeOffset _now = new(2026, 10, 22, 20, 30, 0, TimeSpan.Zero);
    private readonly FakeCapture _capture = new();

    [Fact]
    public void Schedule_DuringTheWindow_StartsHttpCapture_AndDoesNotStoreTheUrl()
    {
        using var service = Create();
        const string secret = "http://secret.example/live.ts";

        var created = service.Schedule(
            "news",
            "Nightly",
            _now.AddMinutes(-10),
            _now.AddMinutes(30),
            secret,
            "http://secret.example/rec.ts");

        WaitForCapture();
        Assert.Equal("recording", created.Status);
        Assert.Equal("news", created.TvgId);
        var call = Assert.Single(_capture.Starts);
        Assert.Equal("http://secret.example/rec.ts", call.Url);
        var stored = File.ReadAllText(Path.Combine(_root, "jobs.json"));
        Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("rec.ts", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", stored, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Schedule_InTheFuture_IsScheduled_AndDoesNotCaptureYet()
    {
        using var service = Create();
        var created = service.Schedule(
            "news",
            "Later",
            _now.AddHours(2),
            _now.AddHours(3),
            "http://secret.example/live.ts",
            null);

        Assert.Equal("scheduled", created.Status);
        Assert.Empty(_capture.Starts);
        Assert.DoesNotContain(
            "secret.example",
            File.ReadAllText(Path.Combine(_root, "jobs.json")),
            StringComparison.Ordinal);
        var listed = service.List().Recordings;
        Assert.Single(listed);
        Assert.Equal("scheduled", listed[0].Status);
    }

    [Fact]
    public void Schedule_WhenCaptureCannotStart_StillKeepsTheJob()
    {
        _capture.Allow = false;
        using var service = Create();
        service.Schedule(
            "news",
            "Nightly",
            _now.AddMinutes(-5),
            _now.AddMinutes(20),
            "http://secret.example/live.ts",
            null);

        WaitForCapture();
        Assert.Equal(1, _capture.Attempts);
        Assert.Empty(_capture.Starts);
        var stored = File.ReadAllText(Path.Combine(_root, "jobs.json"));
        Assert.DoesNotContain("secret.example", stored, StringComparison.Ordinal);
        Assert.Equal("recording", Assert.Single(service.List().Recordings).Status);
    }

    [Fact]
    public void Schedule_IgnoresNonHttpUrls()
    {
        using var service = Create();
        service.Schedule(
            "news",
            "Nightly",
            _now.AddMinutes(-5),
            _now.AddMinutes(20),
            "file:///etc/passwd",
            "not a url");

        Assert.Equal(0, _capture.Attempts);
        Assert.Equal("recording", Assert.Single(service.List().Recordings).Status);
    }

    [Fact]
    public void Cancel_RemovesTheJob_AndStopsCapture()
    {
        using var service = Create();
        var created = service.Schedule(
            "news",
            "Nightly",
            _now.AddMinutes(-5),
            _now.AddMinutes(20),
            "http://secret.example/live.ts",
            null);
        WaitForCapture();

        Assert.True(service.Cancel(created.Id));
        Assert.Empty(service.List().Recordings);
        Assert.Contains(created.Id, _capture.Stopped);
        Assert.False(service.Cancel(created.Id));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DirectIptvRecordingService Create() =>
        new(_root, _capture, () => _now);

    private void WaitForCapture()
    {
        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until && _capture.Starts.Count == 0 && _capture.Attempts == 0)
        {
            Thread.Sleep(10);
        }
    }

    private sealed class FakeCapture : IDirectStreamCapture
    {
        public bool Allow { get; set; } = true;

        public int Attempts { get; private set; }

        public List<(string Id, string Url)> Starts { get; } = new();

        public List<string> Stopped { get; } = new();

        public bool TryStart(string id, string url, TimeSpan duration, string outputPath)
        {
            try
            {
                if (!Allow || !FfmpegDirectStreamCapture.IsHttp(url))
                {
                    return false;
                }

                Starts.Add((id, url));
                return true;
            }
            finally
            {
                Attempts++;
            }
        }

        public void Stop(string id) => Stopped.Add(id);

        public void StopAll()
        {
        }
    }
}
