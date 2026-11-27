using System.Globalization;
using System.Text.Json;
using Moonfin.Server.Api;
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

        Assert.Equal("recording", created.Status);
        Assert.Equal("news", created.TvgId);
        var call = Assert.Single(_capture.Starts);
        Assert.Equal("http://secret.example/rec.ts", call.Url);
        Assert.EndsWith(created.Id + ".partial.ts", call.Output);
        var stored = File.ReadAllText(Path.Combine(_root, "jobs.json"));
        Assert.Contains("http://secret.example/rec.ts", stored, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
        Assert.Contains("\"tempPath\"", stored, StringComparison.Ordinal);
        Assert.Contains("\"plannedPath\"", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(service.List()), StringComparison.Ordinal);
        Assert.DoesNotContain("rec.ts", JsonSerializer.Serialize(service.List()), StringComparison.Ordinal);
        Assert.Equal("recording", Assert.Single(service.List().Recordings).Status);
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
        var stored = File.ReadAllText(Path.Combine(_root, "jobs.json"));
        Assert.Contains("http://secret.example/live.ts", stored, StringComparison.Ordinal);
        Assert.Contains("\"plannedPath\"", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret.example", JsonSerializer.Serialize(service.List()), StringComparison.Ordinal);
        var listed = service.List().Recordings;
        Assert.Single(listed);
        Assert.Equal("scheduled", listed[0].Status);
    }

    [Fact]
    public void Schedule_WhenCaptureCannotStart_KeepsTheJobAsFailed()
    {
        _capture.Allow = false;
        _capture.Failure = "ffmpeg did not start: not found";
        using var service = Create();
        service.Schedule(
            "news",
            "Nightly",
            _now.AddMinutes(-5),
            _now.AddMinutes(20),
            "http://secret.example/live.ts",
            null);

        Assert.Equal(1, _capture.Attempts);
        Assert.Empty(_capture.Starts);
        var stored = File.ReadAllText(Path.Combine(_root, "jobs.json"));
        Assert.Contains("http://secret.example/live.ts", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", stored, StringComparison.OrdinalIgnoreCase);
        var job = Assert.Single(service.List().Recordings);
        Assert.Equal("failed", job.Status);
        Assert.Equal("ffmpeg did not start: not found", job.FailureReason);
        Assert.Null(job.Path);
        Assert.DoesNotContain("secret.example", JsonSerializer.Serialize(service.List()), StringComparison.Ordinal);
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
        var job = Assert.Single(service.List().Recordings);
        Assert.Equal("failed", job.Status);
        Assert.Equal("No http(s) stream URL was provided.", job.FailureReason);
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

        Assert.True(service.Cancel(created.Id));
        Assert.Empty(service.List().Recordings);
        Assert.Contains(created.Id, _capture.Stopped);
        Assert.False(service.Cancel(created.Id));
    }

    [Fact]
    public void FinishedMovie_IsMovedIntoTheMoviesFolder_AndReportsTheLibraryItem()
    {
        var movies = Path.Combine(_root, "movies");
        var library = new FakeLibrary();
        using var service = Create(library, () => new DirectRecordingRoots(movies, Path.Combine(_root, "shows")));
        var created = service.Schedule(
            "hbo",
            "Inception",
            _now.AddMinutes(-5),
            _now.AddMinutes(20),
            "http://secret.example/live.ts",
            null,
            new DirectRecordingProgram { Title = "Inception", Year = 2010, IsMovie = true });

        _capture.WriteAndFinish(created.Id, DirectRecordingFiles.MinimumBytes + 1);

        var expected = Path.Combine(movies, "Inception (2010)", "Inception (2010).ts");
        var listed = Assert.Single(service.List().Recordings);
        Assert.Equal("completed", listed.Status);
        Assert.Null(listed.FailureReason);
        Assert.Equal(expected, listed.Path);
        Assert.True(new FileInfo(expected).Length > DirectRecordingFiles.MinimumBytes);
        Assert.False(File.Exists(Path.Combine(_root, "files", created.Id + ".partial.ts")));
        Assert.Equal(Path.GetDirectoryName(expected), Assert.Single(library.Scanned));
        Assert.Null(listed.ItemId);

        library.Items[expected] = "6b1e1c0a-1111-2222-3333-444444444444";
        Assert.Equal("6b1e1c0a-1111-2222-3333-444444444444", Assert.Single(service.List().Recordings).ItemId);
        Assert.True(library.Lookups >= 2);
    }

    [Fact]
    public void FileAtOrUnderOneMegabyte_IsFailed_AndNotCompletedBecauseTheWindowIsOpen()
    {
        using var service = Create();
        var created = service.Schedule(
            "news",
            "Nightly",
            _now.AddMinutes(-5),
            _now.AddMinutes(20),
            "http://secret.example/live.ts",
            null);

        _capture.WriteAndFinish(created.Id, DirectRecordingFiles.MinimumBytes, exitCode: 1, stderr: "connection reset");

        var listed = Assert.Single(service.List().Recordings);
        Assert.Equal("failed", listed.Status);
        Assert.Contains("not larger than 1 MB", listed.FailureReason);
        Assert.Contains("connection reset", listed.FailureReason);
        Assert.Null(listed.Path);
        Assert.Null(listed.ItemId);
    }

    [Fact]
    public void FinishedShow_UsesSeasonEpisodeNames()
    {
        var shows = Path.Combine(_root, "shows");
        var library = new FakeLibrary();
        using var service = Create(library, () => new DirectRecordingRoots(null, shows));
        var created = service.Schedule(
            "net",
            "Ignored",
            _now.AddMinutes(-5),
            _now.AddMinutes(20),
            "http://secret.example/live.ts",
            null,
            new DirectRecordingProgram
            {
                Series = "The Office",
                EpisodeTitle = "Pilot",
                Season = 1,
                Episode = 2,
            });

        _capture.WriteAndFinish(created.Id, DirectRecordingFiles.MinimumBytes + 50);

        var expected = Path.Combine(shows, "The Office", "Season 01", "The Office - S01E02 - Pilot.ts");
        var listed = Assert.Single(service.List().Recordings);
        Assert.Equal("completed", listed.Status);
        Assert.Equal(expected, listed.Path);
        Assert.Equal(Path.GetDirectoryName(expected), Assert.Single(library.Scanned));
    }

    [Fact]
    public void Cancel_LeavesAFinishedLibraryFileInPlace()
    {
        var movies = Path.Combine(_root, "movies");
        using var service = Create(paths: () => new DirectRecordingRoots(movies, null));
        var created = service.Schedule(
            "hbo",
            "Inception",
            _now.AddMinutes(-5),
            _now.AddMinutes(20),
            "http://secret.example/live.ts",
            null,
            new DirectRecordingProgram { Title = "Inception", Year = 2010, IsMovie = true });
        _capture.WriteAndFinish(created.Id, DirectRecordingFiles.MinimumBytes + 1);
        var path = Assert.Single(service.List().Recordings).Path;
        Assert.False(string.IsNullOrWhiteSpace(path));

        Assert.True(service.Cancel(created.Id));

        Assert.Empty(service.List().Recordings);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Reload_DoesNotMarkCompletedJustBecauseTheWindowPassed()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "jobs.json"),
            """
            {"jobs":[{"id":"abc","tvgId":"news","title":"Nightly","status":"scheduled","start":"2026-10-22T18:00:00+00:00","end":"2026-10-22T19:00:00+00:00"}]}
            """);

        using var service = Create();
        var job = Assert.Single(service.List().Recordings);
        Assert.Equal("failed", job.Status);
        Assert.Equal("missed (server was down)", job.FailureReason);
        Assert.Null(job.Path);
    }

    [Fact]
    public void Reload_OfARecordingWithARealFile_CompletesIntoTheLibrary()
    {
        var movies = Path.Combine(_root, "movies");
        var temp = Path.Combine(_root, "files", "abc.partial.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(DirectRecordingFiles.MinimumBytes + 8);
        }

        File.WriteAllText(
            Path.Combine(_root, "jobs.json"),
            "{\"jobs\":[{\"id\":\"abc\",\"tvgId\":\"hbo\",\"title\":\"Inception\",\"year\":2010,\"isMovie\":true,\"start\":\"2026-10-22T18:00:00+00:00\",\"end\":\"2026-10-22T20:00:00+00:00\",\"status\":\"recording\",\"tempPath\":\""
            + temp.Replace("\\", "\\\\", StringComparison.Ordinal)
            + "\"}]}");

        var library = new FakeLibrary();
        using var service = Create(library, () => new DirectRecordingRoots(movies, null));
        var job = Assert.Single(service.List().Recordings);
        var expected = Path.Combine(movies, "Inception (2010)", "Inception (2010).ts");
        Assert.Equal("completed", job.Status);
        Assert.Equal(expected, job.Path);
        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(temp));
        Assert.Equal(Path.GetDirectoryName(expected), Assert.Single(library.Scanned));
    }

    [Fact]
    public void Reload_InProgress_CapturesTheTimeLeft()
    {
        var end = _now.AddMinutes(30);
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "jobs.json"),
            "{\"jobs\":[{\"id\":\"abc\",\"tvgId\":\"news\",\"title\":\"Nightly\",\"series\":\"Nightly\",\"status\":\"scheduled\",\"streamUrl\":\"http://secret.example/live.ts\",\"tempPath\":\""
            + Path.Combine(_root, "files", "abc.partial.ts").Replace("\\", "\\\\", StringComparison.Ordinal)
            + "\",\"plannedPath\":\""
            + Path.Combine(_root, "shows", "Nightly", "Nightly.ts").Replace("\\", "\\\\", StringComparison.Ordinal)
            + "\",\"start\":\""
            + _now.AddMinutes(-10).ToString("o", CultureInfo.InvariantCulture)
            + "\",\"end\":\""
            + end.ToString("o", CultureInfo.InvariantCulture)
            + "\"}]}");

        using var service = Create();
        var job = Assert.Single(service.List().Recordings);
        var call = Assert.Single(_capture.Starts);
        Assert.Equal("recording", job.Status);
        Assert.Equal("http://secret.example/live.ts", call.Url);
        Assert.Equal(TimeSpan.FromMinutes(30), call.Duration);
        Assert.Null(job.Path);
        Assert.DoesNotContain("secret.example", JsonSerializer.Serialize(service.List()), StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_RecordingStillOpen_DoesNotFinishThePartial()
    {
        var temp = Path.Combine(_root, "files", "abc.partial.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(DirectRecordingFiles.MinimumBytes + 8);
        }

        File.WriteAllText(
            Path.Combine(_root, "jobs.json"),
            "{\"jobs\":[{\"id\":\"abc\",\"tvgId\":\"hbo\",\"title\":\"Inception\",\"year\":2010,\"isMovie\":true,\"status\":\"recording\",\"streamUrl\":\"http://secret.example/live.ts\",\"tempPath\":\""
            + temp.Replace("\\", "\\\\", StringComparison.Ordinal)
            + "\",\"start\":\"2026-10-22T20:00:00+00:00\",\"end\":\"2026-10-22T22:00:00+00:00\"}]}");

        var movies = Path.Combine(_root, "movies");
        using var service = Create(paths: () => new DirectRecordingRoots(movies, null));
        var job = Assert.Single(service.List().Recordings);
        Assert.Equal("recording", job.Status);
        Assert.Null(job.Path);
        Assert.True(File.Exists(temp));
        Assert.False(File.Exists(Path.Combine(movies, "Inception (2010)", "Inception (2010).ts")));
        Assert.Equal(TimeSpan.FromMinutes(90), Assert.Single(_capture.Starts).Duration);
    }

    [Fact]
    public async Task Reload_FutureJob_RearmsAndCapturesWhenTheWindowOpens()
    {
        var start = _now.AddMilliseconds(200);
        var end = _now.AddHours(1);
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "jobs.json"),
            "{\"jobs\":[{\"id\":\"abc\",\"tvgId\":\"news\",\"title\":\"Later\",\"status\":\"scheduled\",\"streamUrl\":\"http://secret.example/live.ts\",\"start\":\""
            + start.ToString("o", CultureInfo.InvariantCulture)
            + "\",\"end\":\""
            + end.ToString("o", CultureInfo.InvariantCulture)
            + "\"}]}");

        using var service = Create();
        Assert.Equal("scheduled", Assert.Single(service.List().Recordings).Status);
        Assert.Empty(_capture.Starts);

        await Task.Delay(2000);

        var job = Assert.Single(service.List().Recordings);
        Assert.Equal("recording", job.Status);
        Assert.Equal("http://secret.example/live.ts", Assert.Single(_capture.Starts).Url);
        Assert.Equal(end - _now, _capture.Starts[0].Duration);
    }

    [Fact]
    public void FfmpegFailure_RedactsStreamUrls_AndKeepsThePlaylistError()
    {
        using var service = Create();
        var created = service.Schedule(
            "news",
            "Nightly",
            _now.AddMinutes(-5),
            _now.AddMinutes(20),
            "http://secret.example/live.ts",
            null);

        const string token = "signed-stream-token";
        var stream = "http://192.168.1.119:3004/mofin/free/r?token=" + token;
        _capture.WriteAndFinish(
            created.Id,
            0,
            exitCode: 234,
            stderr: "URL " + stream + " is not in allowed_segment_extensions\nparse_playlist error Invalid data found when processing input");

        var job = Assert.Single(service.List().Recordings);
        Assert.Equal("failed", job.Status);
        Assert.Contains("234", job.FailureReason, StringComparison.Ordinal);
        Assert.Contains("allowed_segment_extensions", job.FailureReason, StringComparison.Ordinal);
        Assert.Contains("parse_playlist", job.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain(token, job.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain(stream, job.FailureReason, StringComparison.Ordinal);
        var stored = File.ReadAllText(Path.Combine(_root, "jobs.json"));
        Assert.DoesNotContain(token, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("mofin/free/r", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_OpenRecording_DoesNotClobberThePartial_AndJoinsMpegTsParts()
    {
        var first = Path.Combine(_root, "files", "abc.partial.ts");
        WriteMpegTs(first, 2800);
        var firstLength = new FileInfo(first).Length;
        var movies = Path.Combine(_root, "movies");
        File.WriteAllText(
            Path.Combine(_root, "jobs.json"),
            "{\"jobs\":[{\"id\":\"abc\",\"tvgId\":\"hbo\",\"title\":\"Inception\",\"year\":2010,\"isMovie\":true,\"status\":\"recording\",\"streamUrl\":\"http://secret.example/live.ts\",\"tempPath\":\""
            + first.Replace("\\", "\\\\", StringComparison.Ordinal)
            + "\",\"start\":\"2026-10-22T20:00:00+00:00\",\"end\":\"2026-10-22T22:00:00+00:00\"}]}");

        using var service = Create(paths: () => new DirectRecordingRoots(movies, null));
        var call = Assert.Single(_capture.Starts);
        Assert.Equal("abc-2.partial.ts", Path.GetFileName(call.Output));
        Assert.Equal(firstLength, new FileInfo(first).Length);
        Assert.NotEqual(Path.GetFullPath(first), Path.GetFullPath(call.Output));

        WriteMpegTs(call.Output, 2800);
        _capture.Finish("abc");

        var expected = Path.Combine(movies, "Inception (2010)", "Inception (2010).ts");
        var listed = Assert.Single(service.List().Recordings);
        Assert.Equal("completed", listed.Status);
        Assert.Equal(expected, listed.Path);
        Assert.Equal(firstLength * 2, new FileInfo(expected).Length);
        Assert.Equal(0x47, File.ReadAllBytes(expected)[0]);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(call.Output));
    }

    [Fact]
    public void Reload_OpenRecording_SkipsEveryPartialThatAlreadyHasData()
    {
        var first = Path.Combine(_root, "files", "abc.partial.ts");
        var second = Path.Combine(_root, "files", "abc-2.partial.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        File.WriteAllBytes(first, [0x47, 1, 2, 3]);
        File.WriteAllBytes(second, [0x47, 4, 5, 6, 7]);
        File.WriteAllText(
            Path.Combine(_root, "jobs.json"),
            "{\"jobs\":[{\"id\":\"abc\",\"tvgId\":\"news\",\"title\":\"Nightly\",\"status\":\"recording\",\"streamUrl\":\"http://secret.example/live.ts\",\"tempPath\":\""
            + second.Replace("\\", "\\\\", StringComparison.Ordinal)
            + "\",\"start\":\"2026-10-22T20:00:00+00:00\",\"end\":\"2026-10-22T22:00:00+00:00\"}]}");

        using var service = Create();
        var call = Assert.Single(_capture.Starts);
        Assert.Equal("abc-3.partial.ts", Path.GetFileName(call.Output));
        Assert.Equal(4, new FileInfo(first).Length);
        Assert.Equal(5, new FileInfo(second).Length);
        Assert.Equal("recording", Assert.Single(service.List().Recordings).Status);
        Assert.False(File.Exists(call.Output));
    }

    private static void WriteMpegTs(string path, int packets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var packet = new byte[188];
        packet[0] = 0x47;
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        for (var i = 0; i < packets; i++)
        {
            stream.Write(packet);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DirectIptvRecordingService Create(
        IDirectRecordingLibrary? library = null,
        Func<DirectRecordingRoots>? paths = null) =>
        new(_root, _capture, () => _now, paths, library);

    private sealed class FakeCapture : IDirectStreamCapture
    {
        private readonly Dictionary<string, Action<DirectCaptureExit>> _exits = new(StringComparer.Ordinal);

        public bool Allow { get; set; } = true;

        public string? Failure { get; set; }

        public int Attempts { get; private set; }

        public List<(string Id, string Url, string Output, TimeSpan Duration)> Starts { get; } = new();

        public List<string> Stopped { get; } = new();

        public DirectCaptureStart TryStart(
            string id,
            string url,
            TimeSpan duration,
            string outputPath,
            Action<DirectCaptureExit>? onExit)
        {
            Attempts++;
            if (!Allow || !FfmpegDirectStreamCapture.IsHttp(url))
            {
                return new DirectCaptureStart(false, Failure ?? "capture did not start");
            }

            Starts.Add((id, url, outputPath, duration));
            if (onExit != null)
            {
                _exits[id] = onExit;
            }

            return new DirectCaptureStart(true, null);
        }

        public void WriteAndFinish(string id, long bytes, int exitCode = 0, string stderr = "")
        {
            var start = Assert.Single(Starts, call => call.Id == id);
            Directory.CreateDirectory(Path.GetDirectoryName(start.Output)!);
            using (var stream = new FileStream(start.Output, FileMode.Create, FileAccess.Write))
            {
                stream.SetLength(bytes);
            }

            _exits[id](new DirectCaptureExit(exitCode, stderr));
        }

        public void Finish(string id, int exitCode = 0, string stderr = "")
        {
            _exits[id](new DirectCaptureExit(exitCode, stderr));
        }

        public void Stop(string id) => Stopped.Add(id);

        public void StopAll()
        {
        }
    }

    private sealed class FakeLibrary : IDirectRecordingLibrary
    {
        public List<string> Scanned { get; } = new();

        public int Lookups { get; private set; }

        public Dictionary<string, string> Items { get; } = new(StringComparer.Ordinal);

        public void ScanFolder(string folderPath) => Scanned.Add(folderPath);

        public string? FindItemId(string filePath)
        {
            Lookups++;
            return Items.TryGetValue(filePath, out var id) ? id : null;
        }
    }
}

public class DirectRecordingFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-direct-names-{Guid.NewGuid():N}");
    private readonly DateTimeOffset _start = new(2024, 5, 6, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Movie_IncludesYear_AndOmitsItWhenUnknown()
    {
        var movies = Path.Combine(_root, "movies");
        var withYear = DirectRecordingFiles.BuildDestination(
            Fallback(),
            movies,
            Path.Combine(_root, "shows"),
            new DirectRecordingProgram { Title = "Inception", Year = 2010, IsMovie = true },
            _start);
        var withoutYear = DirectRecordingFiles.BuildDestination(
            Fallback(),
            movies,
            null,
            new DirectRecordingProgram { Title = "Inception", IsMovie = true },
            _start);

        Assert.Equal(Path.Combine(movies, "Inception (2010)", "Inception (2010).ts"), withYear);
        Assert.Equal(Path.Combine(movies, "Inception", "Inception.ts"), withoutYear);
    }

    [Fact]
    public void MovieCategory_SortsAsAMovie_WithoutTheFlag()
    {
        var movies = Path.Combine(_root, "movies");
        var path = DirectRecordingFiles.BuildDestination(
            Fallback(),
            movies,
            Path.Combine(_root, "shows"),
            new DirectRecordingProgram { Title = "Heat", Year = 1995, IsMovie = false, Category = "Movie / Drama" },
            _start);

        Assert.Equal(Path.Combine(movies, "Heat (1995)", "Heat (1995).ts"), path);
    }

    [Fact]
    public void Show_WithoutNumbers_UsesTheAirDate()
    {
        var shows = Path.Combine(_root, "shows");
        var dated = DirectRecordingFiles.BuildDestination(
            Fallback(),
            Path.Combine(_root, "movies"),
            shows,
            new DirectRecordingProgram
            {
                Series = "News",
                EpisodeTitle = "Nightly",
                OriginalAirDate = new DateTime(2024, 5, 6),
            },
            _start);
        var fromStart = DirectRecordingFiles.BuildDestination(
            Fallback(),
            null,
            shows,
            new DirectRecordingProgram { Title = "News" },
            _start);

        Assert.Equal(Path.Combine(shows, "News", "News - 2024-05-06 - Nightly.ts"), dated);
        var localDay = DirectRecordingFiles.AirDate(_start, null, TimeZoneInfo.Local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal(Path.Combine(shows, "News", "News - " + localDay + ".ts"), fromStart);
    }

    [Fact]
    public void Show_WithoutAirDate_UsesTheZoneItIsGiven_NotUtc()
    {
        var start = new DateTimeOffset(2024, 5, 7, 1, 30, 0, TimeSpan.Zero);
        var chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var localEvening = DirectRecordingFiles.AirDate(start, null, chicago);
        Assert.Equal(new DateTime(2024, 5, 6), localEvening);

        var kept = DirectRecordingFiles.AirDate(start, new DateTime(1999, 1, 2), chicago);
        Assert.Equal(new DateTime(1999, 1, 2), kept);

        var shows = Path.Combine(_root, "shows");
        var path = DirectRecordingFiles.BuildDestination(
            Fallback(),
            null,
            shows,
            new DirectRecordingProgram { Title = "News" },
            start);
        var day = DirectRecordingFiles.AirDate(start, null, TimeZoneInfo.Local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal(Path.Combine(shows, "News", "News - " + day + ".ts"), path);
        if (day != "2024-05-07")
        {
            Assert.DoesNotContain("2024-05-07", path, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProgramOf_WithoutAirDate_UsesTheServerLocalStart()
    {
        var start = new DateTimeOffset(2024, 5, 7, 1, 30, 0, TimeSpan.Zero);
        var program = DirectRecordingPrograms.ProgramOf(new DirectRecordingBody { Title = "News" }, start);
        Assert.Equal(DirectRecordingFiles.AirDate(start, null, TimeZoneInfo.Local), program.OriginalAirDate);

        var supplied = DirectRecordingPrograms.ProgramOf(
            new DirectRecordingBody { OriginalAirDate = new DateTime(1999, 1, 2), Title = "News" },
            start);
        Assert.Equal(new DateTime(1999, 1, 2), supplied.OriginalAirDate);
    }

    [Fact]
    public void IllegalCharacters_AreStripped_AndACopyGetsASuffix()
    {
        var shows = Path.Combine(_root, "shows");
        var program = new DirectRecordingProgram
        {
            Series = "A:B/C",
            EpisodeTitle = "Hi?",
            Season = 1,
            Episode = 2,
        };
        var first = DirectRecordingFiles.BuildDestination(Fallback(), null, shows, program, _start);
        var expected = Path.Combine(shows, "A B C", "Season 01", "A B C - S01E02 - Hi.ts");
        Assert.Equal(expected, first);
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        File.WriteAllBytes(first, [1]);

        var second = DirectRecordingFiles.BuildDestination(Fallback(), null, shows, program, _start);
        Assert.Equal(Path.Combine(shows, "A B C", "Season 01", "A B C - S01E02 - Hi (2).ts"), second);
    }

    [Fact]
    public void EmptyLibraryPaths_StayInThePluginFolder()
    {
        var fallback = Fallback();
        var path = DirectRecordingFiles.BuildDestination(
            fallback,
            "  ",
            null,
            new DirectRecordingProgram { Title = "Inception", Year = 2010, IsMovie = true },
            _start);

        Assert.Equal(fallback, path);
    }

    [Theory]
    [InlineData("/opt/jellyfin/ffmpeg", true, "/opt/jellyfin/ffmpeg")]
    [InlineData("/missing/ffmpeg", true, "/usr/lib/jellyfin-ffmpeg/ffmpeg")]
    [InlineData(null, false, "ffmpeg")]
    [InlineData("ffmpeg", false, "ffmpeg")]
    public void ResolveEncoderPath_UsesConfigured_ThenJellyfin_ThenBare(string? configured, bool jellyfinExists, string expected)
    {
        var resolved = FfmpegDirectStreamCapture.ResolveEncoderPath(
            configured,
            path => jellyfinExists && path == FfmpegDirectStreamCapture.JellyfinEncoderPath || path == "/opt/jellyfin/ffmpeg");

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void CaptureArguments_AllowExtensionlessHlsSegmentsBeforeInput()
    {
        var args = FfmpegDirectStreamCapture.BuildArguments(
            "http://example.test/mofin/free/s/1.m3u8",
            5,
            "out.partial.ts").ToList();
        var input = args.IndexOf("-i");
        Assert.True(input > 0);
        Assert.Equal(
            new[]
            {
                "-allowed_extensions",
                "ALL",
                "-allowed_segment_extensions",
                "ALL",
                "-extension_picky",
                "0",
            },
            args.Skip(args.IndexOf("-allowed_extensions")).Take(6));
        Assert.True(args.IndexOf("-allowed_extensions") < input);
        Assert.True(args.IndexOf("-allowed_segment_extensions") < input);
        Assert.True(args.IndexOf("-extension_picky") < input);
        Assert.Equal("0", args[args.IndexOf("-extension_picky") + 1]);
        Assert.Equal("http://example.test/mofin/free/s/1.m3u8", args[input + 1]);
        Assert.Equal("out.partial.ts", args[^1]);
    }

    [Fact]
    public void StderrTail_DropsSignedUrls_AndKeepsThePlaylistError()
    {
        var token = new string('a', 5000);
        var tail = FfmpegDirectStreamCapture.TailOf(new[]
        {
            "URL http://example.test/mofin/free/r?token=" + token + " is not in allowed_segment_extensions",
            "parse_playlist error Invalid data found when processing input",
        });

        Assert.Contains("allowed_segment_extensions", tail, StringComparison.Ordinal);
        Assert.Contains("parse_playlist", tail, StringComparison.Ordinal);
        Assert.DoesNotContain(token, tail, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", tail, StringComparison.Ordinal);
        Assert.True(tail.Length <= 2000);

        var query = FfmpegDirectStreamCapture.RedactStreamSecrets(
            "leftover ?token=signed-stream-token&sig=abc is not in allowed_segment_extensions");
        Assert.Contains("allowed_segment_extensions", query, StringComparison.Ordinal);
        Assert.DoesNotContain("signed-stream-token", query, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=abc", query, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Fallback() => Path.Combine(_root, "files", "job.ts");
}
