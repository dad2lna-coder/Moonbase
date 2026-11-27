using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Starts an ffmpeg copy of an http(s) stream. A missing ffmpeg, or a URL that
/// is not http(s), is a no. The URL is an argument, never written to disk.
/// </summary>
public interface IDirectStreamCapture
{
    DirectCaptureStart TryStart(
        string id,
        string url,
        TimeSpan duration,
        string outputPath,
        Action<DirectCaptureExit>? onExit);

    void Stop(string id);

    void StopAll();
}

public readonly record struct DirectCaptureStart(bool Started, string? Failure);

public readonly record struct DirectCaptureExit(int ExitCode, string StderrTail);

public sealed class FfmpegDirectStreamCapture : IDirectStreamCapture
{
    public const string JellyfinEncoderPath = "/usr/lib/jellyfin-ffmpeg/ffmpeg";

    private const int StderrTailLimit = 2000;

    private static readonly Regex UrlPattern = new(
        @"https?://[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A signed URL can be long enough that only its query string remains in a trimmed line.
    private static readonly Regex QueryPattern = new(
        @"(?:\?|&)(?:[A-Za-z0-9_.~%+\-]+=[^\s""'<>&]*)+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILogger<FfmpegDirectStreamCapture> _logger;
    private readonly IMediaEncoder? _encoder;
    private readonly ConcurrentDictionary<string, Process> _processes = new(StringComparer.Ordinal);

    public FfmpegDirectStreamCapture(ILogger<FfmpegDirectStreamCapture> logger, IMediaEncoder encoder)
    {
        _logger = logger;
        _encoder = encoder;
    }

    public DirectCaptureStart TryStart(
        string id,
        string url,
        TimeSpan duration,
        string outputPath,
        Action<DirectCaptureExit>? onExit)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return new DirectCaptureStart(false, "Recording id is missing.");
        }

        if (!IsHttp(url))
        {
            return new DirectCaptureStart(false, "No http(s) stream URL was provided.");
        }

        if (duration <= TimeSpan.Zero)
        {
            return new DirectCaptureStart(false, "The recording window has no time left.");
        }

        var encoder = ResolveEncoderPath(ReadEncoderPath());
        var seconds = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds));
        Process? process = null;
        try
        {
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var start = new ProcessStartInfo
            {
                FileName = encoder,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            foreach (var argument in BuildArguments(url, seconds, outputPath))
            {
                start.ArgumentList.Add(argument);
            }

            _logger.LogInformation(
                "Direct IPTV recording {RecordingId} starting {Encoder} for {Seconds}s into {Output}",
                id,
                encoder,
                seconds,
                outputPath);

            process = Process.Start(start);
            if (process == null)
            {
                const string reason = "ffmpeg did not start.";
                _logger.LogError("Direct IPTV recording {RecordingId} {Reason} Encoder {Encoder}", id, reason, encoder);
                return new DirectCaptureStart(false, reason);
            }

            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, eventArgs) => AppendTail(stderr, eventArgs.Data);
            process.OutputDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            var published = 0;
            void Publish()
            {
                if (Interlocked.Exchange(ref published, 1) != 0)
                {
                    return;
                }

                int code;
                try
                {
                    // WaitForExit flushes the redirected stderr even when the process has already exited.
                    process.WaitForExit();
                    code = process.ExitCode;
                }
                catch (Exception ex)
                {
                    code = -1;
                    _logger.LogError(ex, "Direct IPTV recording {RecordingId} ffmpeg ended without an exit code", id);
                }

                var tail = Tail(stderr);
                if (code == 0)
                {
                    _logger.LogInformation(
                        "Direct IPTV recording {RecordingId} ffmpeg exited {ExitCode}. {Stderr}",
                        id,
                        code,
                        tail);
                }
                else
                {
                    _logger.LogError(
                        "Direct IPTV recording {RecordingId} ffmpeg exited {ExitCode}. {Stderr}",
                        id,
                        code,
                        tail);
                }

                _processes.TryRemove(id, out _);
                try
                {
                    onExit?.Invoke(new DirectCaptureExit(code, tail));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Direct IPTV recording {RecordingId} failed while finishing", id);
                }

                try
                {
                    process.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Direct IPTV recording {RecordingId} ffmpeg process was already disposed", id);
                }
            }

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Publish();
            _processes[id] = process;
            if (process.HasExited)
            {
                Publish();
            }

            return new DirectCaptureStart(true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Direct IPTV recording {RecordingId} could not start {Encoder}", id, encoder);
            try
            {
                process?.Dispose();
            }
            catch (Exception disposeEx)
            {
                _logger.LogDebug(disposeEx, "Direct IPTV recording {RecordingId} ffmpeg process was already disposed", id);
            }

            return new DirectCaptureStart(false, "ffmpeg did not start: " + ex.Message);
        }
    }

    public void Stop(string id)
    {
        if (!_processes.TryRemove(id, out var process))
        {
            return;
        }

        StopProcess(process, _logger, id);
    }

    public void StopAll()
    {
        foreach (var id in _processes.Keys)
        {
            Stop(id);
        }
    }

    public static bool IsHttp(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Jellyfin's configured encoder, then the container path, then a bare ffmpeg.
    /// An absolute configured path that is not on disk is skipped.
    /// </summary>
    public static string ResolveEncoderPath(string? configured, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        if (!string.IsNullOrWhiteSpace(configured)
            && (!Path.IsPathRooted(configured) || exists(configured)))
        {
            return configured;
        }

        if (exists(JellyfinEncoderPath))
        {
            return JellyfinEncoderPath;
        }

        return "ffmpeg";
    }

    /// <summary>
    /// Copy arguments for one capture. The three HLS demuxer options have to
    /// come before -i. Free TV variant playlists and segments are extensionless,
    /// and ffmpeg 8 rejects them without all three (allowed_extensions alone
    /// still dies on the webvtt extension check).
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(string url, int seconds, string outputPath)
    {
        return new[]
        {
            "-nostdin",
            "-hide_banner",
            "-loglevel",
            "warning",
            "-allowed_extensions",
            "ALL",
            "-allowed_segment_extensions",
            "ALL",
            "-extension_picky",
            "0",
            "-y",
            "-i",
            url,
            "-c",
            "copy",
            "-t",
            seconds.ToString(CultureInfo.InvariantCulture),
            outputPath,
        };
    }

    /// <summary>
    /// Drops stream URLs and query strings from ffmpeg stderr before it is
    /// logged or stored. A signed segment URL is long enough to push the real
    /// error out of a 2000-character tail and to leave the token in the log.
    /// </summary>
    internal static string RedactStreamSecrets(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var redacted = UrlPattern.Replace(text, "[url]");
        return QueryPattern.Replace(redacted, "[query]");
    }

    internal static string TailOf(IEnumerable<string> lines)
    {
        var stderr = new StringBuilder();
        foreach (var line in lines)
        {
            AppendTail(stderr, line);
        }

        return Tail(stderr);
    }

    private string? ReadEncoderPath()
    {
        if (_encoder == null)
        {
            return null;
        }

        try
        {
            return _encoder.EncoderPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read Jellyfin's configured ffmpeg path");
            return null;
        }
    }

    private static void AppendTail(StringBuilder stderr, string? line)
    {
        if (line == null)
        {
            return;
        }

        // Redact before the line can fill the buffer. Otherwise a signed URL
        // is what survives the 2000-character tail, and the playlist error is gone.
        line = RedactStreamSecrets(line);
        lock (stderr)
        {
            stderr.AppendLine(line);
            if (stderr.Length > 8000)
            {
                stderr.Remove(0, stderr.Length - 4000);
            }
        }
    }

    private static string Tail(StringBuilder stderr)
    {
        lock (stderr)
        {
            var text = RedactStreamSecrets(stderr.ToString().Trim());
            return text.Length <= StderrTailLimit ? text : text[..StderrTailLimit];
        }
    }

    private static void StopProcess(Process process, ILogger logger, string id)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Direct IPTV recording {RecordingId} ffmpeg had already exited", id);
        }
    }
}

public sealed class DirectRecordingProgram
{
    public string? Title { get; init; }

    public string? Series { get; init; }

    public string? EpisodeTitle { get; init; }

    public int? Season { get; init; }

    public int? Episode { get; init; }

    public int? Year { get; init; }

    public bool? IsMovie { get; init; }

    public string? Category { get; init; }

    public DateTime? OriginalAirDate { get; init; }
}

public readonly record struct DirectRecordingRoots(string? Movies, string? Shows);

public interface IDirectRecordingLibrary
{
    void ScanFolder(string folderPath);

    string? FindItemId(string filePath);
}

/// <summary>
/// Asks Jellyfin to notice one new recording folder, then looks the file up by path.
/// </summary>
public sealed class JellyfinDirectRecordingLibrary : IDirectRecordingLibrary
{
    private readonly ILibraryMonitor _monitor;
    private readonly ILibraryManager _library;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<JellyfinDirectRecordingLibrary> _logger;

    public JellyfinDirectRecordingLibrary(
        ILibraryMonitor monitor,
        ILibraryManager library,
        IFileSystem fileSystem,
        ILogger<JellyfinDirectRecordingLibrary> logger)
    {
        _monitor = monitor;
        _library = library;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public void ScanFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        // Validation walks the parent of a brand-new series or movie folder.
        // That must not block the capture thread, and it must not sit on
        // GetAwaiter().GetResult().
        _ = Task.Run(() => ScanFolderAsync(folderPath));
    }

    private async Task ScanFolderAsync(string folderPath)
    {
        var validated = false;
        try
        {
            var cursor = folderPath;
            Folder? folder = null;
            for (var depth = 0; depth < 6 && !string.IsNullOrEmpty(cursor); depth++)
            {
                if (_library.FindByPath(cursor, true) is Folder found)
                {
                    folder = found;
                    break;
                }

                var parent = Path.GetDirectoryName(cursor);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, cursor, StringComparison.Ordinal))
                {
                    break;
                }

                cursor = parent;
            }

            if (folder != null && !string.IsNullOrWhiteSpace(folder.Path))
            {
                var sameFolder = string.Equals(
                    Path.GetFullPath(folder.Path),
                    Path.GetFullPath(folderPath),
                    StringComparison.OrdinalIgnoreCase);
                // A library root found only because the series or movie folder is new
                // used to be validated one level deep, which never reached the file
                // (or the season folder under it). Always walk that parent recursively.
                var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem));
                await folder.ValidateChildren(
                    new Progress<double>(),
                    options,
                    recursive: true,
                    allowRemoveRoot: false,
                    CancellationToken.None).ConfigureAwait(false);
                validated = true;
                _logger.LogInformation(
                    "Direct IPTV recording validated {Path} from {Folder} (same folder {SameFolder})",
                    folderPath,
                    folder.Path,
                    sameFolder);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Direct IPTV recording library validation failed for {Path}", folderPath);
        }

        if (!validated)
        {
            try
            {
                _library.QueueLibraryScan();
                _logger.LogInformation("Direct IPTV recording queued a library scan for {Path}", folderPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Direct IPTV recording library scan could not be queued for {Path}", folderPath);
            }
        }

        try
        {
            _monitor.ReportFileSystemChanged(folderPath);
            _logger.LogInformation("Direct IPTV recording scanned {Path}", folderPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Direct IPTV recording library scan failed for {Path}", folderPath);
        }
    }

    public string? FindItemId(string filePath)
    {
        try
        {
            var item = _library.FindByPath(filePath, false);
            if (item == null)
            {
                var parent = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(parent)
                    && _library.FindByPath(parent, true) is Movie movie)
                {
                    item = movie;
                }
            }

            return item?.Id.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not look up a library item at {Path}", filePath);
            return null;
        }
    }
}

public static class DirectRecordingFiles
{
    public const long MinimumBytes = 1024L * 1024L;

    public static bool IsMovie(bool? isMovie, string? category) =>
        isMovie == true || IsMovieCategory(category);

    /// <summary>
    /// The day written into a show filename. A supplied air date wins. Otherwise
    /// the start is converted to the server's local zone, not left as UTC, so an
    /// evening show does not roll onto the next date.
    /// </summary>
    public static DateTime AirDate(DateTimeOffset start, DateTime? originalAirDate, TimeZoneInfo zone)
    {
        if (originalAirDate.HasValue)
        {
            return originalAirDate.Value;
        }

        var local = TimeZoneInfo.ConvertTime(start, zone);
        return DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);
    }

    public static string BuildDestination(
        string pluginFilePath,
        string? moviesRoot,
        string? showsRoot,
        DirectRecordingProgram program,
        DateTimeOffset start)
    {
        var movie = IsMovie(program.IsMovie, program.Category);
        var root = movie ? BlankToNull(moviesRoot) : BlankToNull(showsRoot);
        if (root == null)
        {
            return pluginFilePath;
        }

        var planned = movie
            ? MoviePath(root, program)
            : ShowPath(root, program, start);
        return Unique(planned);
    }

    public static string Clean(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (character < 32 || "<>:\"/\\|?*".Contains(character) || Array.IndexOf(invalid, character) >= 0)
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(character);
            }
        }

        var cleaned = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    private static string MoviePath(string root, DirectRecordingProgram program)
    {
        var title = Clean(program.Title, "Recording");
        if (program.Year is int year && year > 0 && !title.EndsWith("(" + year.ToString(CultureInfo.InvariantCulture) + ")", StringComparison.Ordinal))
        {
            title = title + " (" + year.ToString(CultureInfo.InvariantCulture) + ")";
        }

        return Path.Combine(root, title, title + ".ts");
    }

    private static string ShowPath(string root, DirectRecordingProgram program, DateTimeOffset start)
    {
        var series = Clean(First(program.Series, program.Title), "Series");
        var episodeTitle = Clean(program.EpisodeTitle, "");
        if (program.Season is int season && program.Episode is int episode)
        {
            var ss = season.ToString("00", CultureInfo.InvariantCulture);
            var ee = episode.ToString("00", CultureInfo.InvariantCulture);
            var name = string.IsNullOrEmpty(episodeTitle)
                ? series + " - S" + ss + "E" + ee
                : series + " - S" + ss + "E" + ee + " - " + episodeTitle;
            return Path.Combine(root, series, "Season " + ss, name + ".ts");
        }

        var aired = AirDate(start, program.OriginalAirDate, TimeZoneInfo.Local);
        var day = aired.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dated = string.IsNullOrEmpty(episodeTitle)
            ? series + " - " + day
            : series + " - " + day + " - " + episodeTitle;
        return Path.Combine(root, series, dated + ".ts");
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, name + " (" + index.ToString(CultureInfo.InvariantCulture) + ")" + extension);
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Too many recordings already use " + path);
    }

    private static string? BlankToNull(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Trim();

    private static string? First(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsMovieCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return false;
        }

        foreach (var part in category.Split([',', '/', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("movie", StringComparison.OrdinalIgnoreCase)
                || part.Equals("movies", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class DirectRecordingView
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("tvgId")]
    public string TvgId { get; init; } = "";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("start")]
    public DateTimeOffset Start { get; init; }

    [JsonPropertyName("end")]
    public DateTimeOffset End { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "scheduled";

    [JsonPropertyName("failureReason")]
    public string? FailureReason { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("itemId")]
    public string? ItemId { get; init; }
}

public sealed class DirectRecordingListResponse
{
    [JsonPropertyName("recordings")]
    public IReadOnlyList<DirectRecordingView> Recordings { get; init; } = Array.Empty<DirectRecordingView>();
}

/// <summary>
/// Direct IPTV recordings for the signed-in user. Each job stores the guide
/// fields, the start and end, the http(s) stream URL, and the temp and library
/// paths so a restart can arm or resume the capture. The URL is not returned
/// by the recordings API. No API key is stored.
/// </summary>
public sealed class DirectIptvRecordingService : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _storePath;
    private readonly IDirectStreamCapture _capture;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<DirectRecordingRoots>? _roots;
    private readonly IDirectRecordingLibrary? _library;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredJob> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);

    public DirectIptvRecordingService(
        string directory,
        IDirectStreamCapture capture,
        Func<DateTimeOffset>? clock = null,
        Func<DirectRecordingRoots>? libraryPaths = null,
        IDirectRecordingLibrary? library = null,
        ILogger? logger = null)
    {
        Directory.CreateDirectory(directory);
        _storePath = Path.Combine(directory, "jobs.json");
        _capture = capture;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _roots = libraryPaths;
        _library = library;
        _logger = logger;
        Load();
    }

    public DirectRecordingView Schedule(
        string tvgId,
        string? title,
        DateTimeOffset start,
        DateTimeOffset end,
        string? streamUrl,
        string? recUrl,
        DirectRecordingProgram? program = null)
    {
        var id = Guid.NewGuid().ToString("n");
        var guide = program ?? new DirectRecordingProgram();
        var url = FfmpegDirectStreamCapture.IsHttp(recUrl)
            ? recUrl!.Trim()
            : FfmpegDirectStreamCapture.IsHttp(streamUrl) ? streamUrl!.Trim() : null;
        var job = new StoredJob
        {
            Id = id,
            TvgId = tvgId.Trim(),
            Title = First(guide.Title, title) ?? "",
            Series = First(guide.Series, guide.Title, title),
            EpisodeTitle = BlankToNull(guide.EpisodeTitle),
            Season = guide.Season,
            Episode = guide.Episode,
            Year = guide.Year,
            IsMovie = guide.IsMovie,
            Category = BlankToNull(guide.Category),
            OriginalAirDate = guide.OriginalAirDate ?? DirectRecordingFiles.AirDate(start, null, TimeZoneInfo.Local),
            Start = start.ToUniversalTime(),
            End = end.ToUniversalTime(),
            Status = "scheduled",
            StreamUrl = url,
            TempPath = TempPath(id),
        };
        lock (_gate)
        {
            try
            {
                job.PlannedPath = Destination(job);
            }
            catch (Exception ex)
            {
                job.Status = "failed";
                job.Error = "Could not choose a library path: " + ex.Message;
            }

            _jobs[id] = job;
            SaveLocked();
            if (string.Equals(job.Status, "failed", StringComparison.Ordinal))
            {
                return ToView(job);
            }
        }

        var now = _clock();
        if (url == null)
        {
            MarkFailed(id, "No http(s) stream URL was provided.");
        }
        else if (now >= job.End)
        {
            MarkFailed(id, "The recording window had already ended.");
        }
        else if (now >= job.Start)
        {
            BeginCapture(id, url, job.End - now);
        }
        else
        {
            Arm(id, url, job.Start, job.End);
        }

        lock (_gate)
        {
            return ToView(_jobs[id]);
        }
    }

    public DirectRecordingListResponse List()
    {
        List<string> scans;
        lock (_gate)
        {
            scans = CloseExpiredLocked();
        }

        foreach (var folder in scans)
        {
            _library?.ScanFolder(folder);
        }

        lock (_gate)
        {
            return new DirectRecordingListResponse
            {
                Recordings = _jobs.Values
                    .OrderBy(job => job.Start)
                    .Select(ToView)
                    .ToList(),
            };
        }
    }

    public bool Cancel(string id)
    {
        CancellationTokenSource? pending = null;
        List<string> partials = new();
        string status;
        lock (_gate)
        {
            if (!_jobs.Remove(id, out var job))
            {
                return false;
            }

            status = job.Status;
            partials = CapturePartPaths(job, job.TempPath);
            if (_pending.Remove(id, out var cts))
            {
                pending = cts;
            }

            SaveLocked();
        }

        pending?.Cancel();
        _capture.Stop(id);
        // A finished file belongs to the library. A partial capture does not.
        if (!string.Equals(status, "completed", StringComparison.Ordinal))
        {
            foreach (var path in partials)
            {
                TryDelete(path);
            }
        }

        return true;
    }

    public void Dispose()
    {
        List<CancellationTokenSource> pending;
        lock (_gate)
        {
            pending = _pending.Values.ToList();
            _pending.Clear();
        }

        foreach (var cts in pending)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The capture task already finished and disposed its token.
            }
        }

        _capture.StopAll();
    }

    private void Arm(string id, string url, DateTimeOffset start, DateTimeOffset end)
    {
        var now = _clock();
        var delay = start - now;
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _pending[id] = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                }

                var remaining = end - _clock();
                if (remaining <= TimeSpan.Zero || cts.IsCancellationRequested)
                {
                    if (!cts.IsCancellationRequested)
                    {
                        MarkFailed(id, "The recording window ended before ffmpeg started.");
                    }

                    return;
                }

                lock (_gate)
                {
                    if (!_jobs.ContainsKey(id))
                    {
                        return;
                    }
                }

                BeginCapture(id, url, remaining);
            }
            catch (OperationCanceledException)
            {
                // Cancelled before the window opened.
            }
            finally
            {
                lock (_gate)
                {
                    _pending.Remove(id);
                }

                cts.Dispose();
            }
        });
    }

    private void BeginCapture(string id, string url, TimeSpan duration)
    {
        string temp;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job))
            {
                return;
            }

            if (string.Equals(job.Status, "completed", StringComparison.Ordinal)
                || string.Equals(job.Status, "failed", StringComparison.Ordinal))
            {
                return;
            }

            temp = AllocateTempPath(job);
            job.TempPath = temp;
            job.StreamUrl = url;
            job.Status = "recording";
            job.Error = null;
            SaveLocked();
        }

        DirectCaptureStart started;
        try
        {
            started = _capture.TryStart(id, url, duration, temp, exit => Finish(id, temp, exit));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Direct IPTV recording {RecordingId} could not start", id);
            MarkFailed(id, "ffmpeg did not start: " + ex.Message);
            return;
        }

        if (!started.Started)
        {
            MarkFailed(id, started.Failure ?? "ffmpeg did not start.");
        }
    }

    private void Finish(string id, string tempPath, DirectCaptureExit exit)
    {
        string? folder = null;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job))
            {
                TryDelete(tempPath);
                return;
            }

            if (!string.Equals(job.Status, "recording", StringComparison.Ordinal))
            {
                return;
            }

            folder = AcceptLocked(job, tempPath, FailureText(exit, FileLength(tempPath)));
        }

        if (folder != null)
        {
            _library?.ScanFolder(folder);
        }
    }

    private string? AcceptLocked(StoredJob job, string tempPath, string failure)
    {
        var chosen = SelectRecordingFile(job, tempPath);
        if (chosen == null)
        {
            job.Status = "failed";
            job.Error = failure;
            job.OutputPath = null;
            SaveLocked();
            return null;
        }

        try
        {
            var destination = Destination(job);
            if (!SamePath(chosen, destination))
            {
                MoveFile(chosen, destination);
            }

            job.Status = "completed";
            job.Error = null;
            job.OutputPath = destination;
            job.PlannedPath ??= destination;
            SaveLocked();
            DeleteCaptureParts(job, tempPath, destination);
            return Path.GetDirectoryName(destination);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Direct IPTV recording {RecordingId} could not be moved into the library", job.Id);
            job.Status = "failed";
            job.Error = "Could not move the recording into the library: " + ex.Message;
            job.OutputPath = null;
            SaveLocked();
            return null;
        }
    }

    private void MarkFailed(string id, string reason)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job))
            {
                return;
            }

            if (string.Equals(job.Status, "completed", StringComparison.Ordinal))
            {
                return;
            }

            job.Status = "failed";
            job.Error = reason;
            SaveLocked();
        }
    }

    private string Destination(StoredJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.PlannedPath))
        {
            return job.PlannedPath;
        }

        var roots = _roots?.Invoke() ?? new DirectRecordingRoots(null, null);
        return DirectRecordingFiles.BuildDestination(
            PluginFile(job.Id),
            roots.Movies,
            roots.Shows,
            new DirectRecordingProgram
            {
                Title = job.Title,
                Series = job.Series,
                EpisodeTitle = job.EpisodeTitle,
                Season = job.Season,
                Episode = job.Episode,
                Year = job.Year,
                IsMovie = job.IsMovie,
                Category = job.Category,
                OriginalAirDate = job.OriginalAirDate,
            },
            job.Start);
    }

    private string FilesDir()
    {
        var dir = Path.Combine(Path.GetDirectoryName(_storePath)!, "files");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string TempPath(string id) => Path.Combine(FilesDir(), id + ".partial.ts");

    private string PluginFile(string id) => Path.Combine(FilesDir(), id + ".ts");

    private void Load()
    {
        if (!File.Exists(_storePath))
        {
            return;
        }

        var scans = new List<string>();
        var capture = new List<PendingCapture>();
        var arm = new List<PendingArm>();
        try
        {
            var stored = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(_storePath), Json);
            if (stored?.Jobs == null)
            {
                return;
            }

            var changed = false;
            foreach (var job in stored.Jobs)
            {
                if (string.IsNullOrWhiteSpace(job.Id) || string.IsNullOrWhiteSpace(job.TvgId))
                {
                    continue;
                }

                _jobs[job.Id] = job;
                if (Resume(job, scans, capture, arm))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                SaveLocked();
            }
        }
        catch (Exception ex)
        {
            // A damaged job file shows an empty list rather than failing the request.
            _logger?.LogError(ex, "Direct IPTV recording jobs could not be read from {Path}", _storePath);
        }

        foreach (var folder in scans)
        {
            _library?.ScanFolder(folder);
        }

        foreach (var item in capture)
        {
            BeginCapture(item.Id, item.Url, item.Remaining);
        }

        foreach (var item in arm)
        {
            Arm(item.Id, item.Url, item.Start, item.End);
        }
    }

    /// <summary>
    /// A future job is armed again. A window that is open starts capturing for
    /// the time left. A window that already ended with no usable file is failed
    /// as missed, so nothing stays scheduled after its end.
    /// </summary>
    private bool Resume(StoredJob job, List<string> scans, List<PendingCapture> capture, List<PendingArm> arm)
    {
        if (string.Equals(job.Status, "completed", StringComparison.Ordinal)
            || string.Equals(job.Status, "failed", StringComparison.Ordinal))
        {
            return false;
        }

        var now = _clock();
        if (now >= job.End)
        {
            return FinishMissedLocked(job, scans);
        }

        var url = UsableUrl(job.StreamUrl);
        if (url == null)
        {
            job.Status = "failed";
            job.Error = "No http(s) stream URL was provided.";
            return true;
        }

        if (string.IsNullOrWhiteSpace(job.TempPath))
        {
            job.TempPath = TempPath(job.Id);
        }

        if (now >= job.Start)
        {
            capture.Add(new PendingCapture(job.Id, url, job.End - now));
            return !string.Equals(job.Status, "recording", StringComparison.Ordinal);
        }

        var changed = !string.Equals(job.Status, "scheduled", StringComparison.Ordinal);
        job.Status = "scheduled";
        job.Error = null;
        arm.Add(new PendingArm(job.Id, url, job.Start, job.End));
        return changed;
    }

    private List<string> CloseExpiredLocked()
    {
        var scans = new List<string>();
        var now = _clock();
        var changed = false;
        foreach (var job in _jobs.Values)
        {
            if (!string.Equals(job.Status, "scheduled", StringComparison.Ordinal) || now < job.End)
            {
                continue;
            }

            if (FinishMissedLocked(job, scans))
            {
                changed = true;
            }
        }

        if (changed)
        {
            SaveLocked();
        }

        return scans;
    }

    private bool FinishMissedLocked(StoredJob job, List<string> scans)
    {
        var temp = string.IsNullOrWhiteSpace(job.TempPath) ? TempPath(job.Id) : job.TempPath;
        var folder = AcceptLocked(job, temp, "missed (server was down)");
        if (folder != null)
        {
            scans.Add(folder);
        }

        return true;
    }

    private void SaveLocked()
    {
        var payload = JsonSerializer.Serialize(new StoredFile { Jobs = _jobs.Values.ToList() }, Json);
        AtomicFile.WriteAllText(_storePath, payload);
    }

    private DirectRecordingView ToView(StoredJob job)
    {
        // Look the file up on every request. A miss is not stored, so the next
        // GET tries again until Jellyfin has picked the recording up.
        string? itemId = null;
        if (string.Equals(job.Status, "completed", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(job.OutputPath))
        {
            itemId = _library?.FindItemId(job.OutputPath);
        }

        return new DirectRecordingView
        {
            Id = job.Id,
            TvgId = job.TvgId,
            Title = job.Title,
            Start = job.Start,
            End = job.End,
            Status = string.IsNullOrWhiteSpace(job.Status) ? "scheduled" : job.Status,
            FailureReason = string.Equals(job.Status, "failed", StringComparison.Ordinal) ? job.Error : null,
            Path = string.Equals(job.Status, "completed", StringComparison.Ordinal) ? job.OutputPath : null,
            ItemId = itemId,
        };
    }

    private static string FailureText(DirectCaptureExit exit, long bytes)
    {
        var tail = FfmpegDirectStreamCapture.RedactStreamSecrets(exit.StderrTail).Trim();
        var reason = bytes <= 0
            ? "ffmpeg exited " + exit.ExitCode.ToString(CultureInfo.InvariantCulture) + " and did not write a file."
            : "ffmpeg exited " + exit.ExitCode.ToString(CultureInfo.InvariantCulture) + " and the file is " + bytes.ToString(CultureInfo.InvariantCulture) + " bytes, not larger than 1 MB.";
        if (tail.Length == 0)
        {
            return reason.Length <= 2000 ? reason : reason[..2000];
        }

        var room = 2000 - reason.Length - 1;
        if (room <= 0)
        {
            return reason.Length <= 2000 ? reason : reason[..2000];
        }

        if (tail.Length > room)
        {
            tail = tail[..room];
        }

        return reason + " " + tail;
    }

    private string AllocateTempPath(StoredJob job)
    {
        var current = string.IsNullOrWhiteSpace(job.TempPath) ? TempPath(job.Id) : job.TempPath;
        if (FileLength(current) <= 0)
        {
            return current;
        }

        // -y on the path ffmpeg already filled would delete that attempt.
        var dir = Path.GetDirectoryName(current);
        if (string.IsNullOrEmpty(dir))
        {
            dir = FilesDir();
        }

        for (var n = 2; n < 10000; n++)
        {
            var candidate = Path.Combine(dir, job.Id + "-" + n.ToString(CultureInfo.InvariantCulture) + ".partial.ts");
            if (FileLength(candidate) <= 0)
            {
                return candidate;
            }
        }

        return Path.Combine(dir, job.Id + "-" + Guid.NewGuid().ToString("n") + ".partial.ts");
    }

    /// <summary>
    /// The file to move into the library: mpegts parts joined in order when
    /// every included part starts on a sync byte, otherwise the largest file
    /// over the 1 MB minimum.
    /// </summary>
    private string? SelectRecordingFile(StoredJob job, string tempPath)
    {
        var parts = CapturePartPaths(job, tempPath)
            .Select(path => (Path: path, Length: FileLength(path)))
            .Where(part => part.Length > 0)
            .ToList();
        if (parts.Count == 0)
        {
            return null;
        }

        if (parts.Count >= 2)
        {
            var safe = parts.Where(part => IsMpegTs(part.Path)).Select(part => part.Path).ToList();
            if (safe.Count >= 2)
            {
                var joined = Path.Combine(FilesDir(), job.Id + ".joined.ts");
                if (TryConcatMpegTs(safe, joined) && FileLength(joined) > DirectRecordingFiles.MinimumBytes)
                {
                    return joined;
                }

                TryDelete(joined);
            }
        }

        var best = parts
            .OrderByDescending(part => part.Length)
            .ThenBy(part => part.Path, StringComparer.Ordinal)
            .First();
        return best.Length > DirectRecordingFiles.MinimumBytes ? best.Path : null;
    }

    private List<string> CapturePartPaths(StoredJob job, string? extra)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return;
            }

            if (!seen.Add(full) || PartialIndex(job.Id, full) == int.MaxValue)
            {
                return;
            }

            list.Add(path);
        }

        Add(extra);
        Add(job.TempPath);
        Add(TempPath(job.Id));

        var dirs = new HashSet<string>(StringComparer.Ordinal);
        void AddDir(string? dir)
        {
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            try
            {
                dirs.Add(Path.GetFullPath(dir));
            }
            catch (Exception)
            {
                // A path the OS will not open is not a capture directory.
            }
        }

        AddDir(FilesDir());
        AddDir(Path.GetDirectoryName(job.TempPath));
        AddDir(Path.GetDirectoryName(extra));
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, job.Id + "*.partial.ts");
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var file in files)
            {
                Add(file);
            }
        }

        list.Sort((a, b) =>
        {
            var order = PartialIndex(job.Id, a).CompareTo(PartialIndex(job.Id, b));
            return order != 0 ? order : string.Compare(a, b, StringComparison.Ordinal);
        });
        return list;
    }

    private void DeleteCaptureParts(StoredJob job, string? extra, string? keep)
    {
        foreach (var path in CapturePartPaths(job, extra))
        {
            if (keep != null && SamePath(path, keep))
            {
                continue;
            }

            TryDelete(path);
        }

        var joined = Path.Combine(FilesDir(), job.Id + ".joined.ts");
        if (keep == null || !SamePath(joined, keep))
        {
            TryDelete(joined);
        }
    }

    private static int PartialIndex(string id, string path)
    {
        var name = Path.GetFileName(path);
        if (string.Equals(name, id + ".partial.ts", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        var prefix = id + "-";
        const string suffix = ".partial.ts";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            var middle = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
            if (int.TryParse(middle, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 2)
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static bool IsMpegTs(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < 188)
            {
                return false;
            }

            return stream.ReadByte() == 0x47;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryConcatMpegTs(IReadOnlyList<string> sources, string destination)
    {
        FileStream? output = null;
        try
        {
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[188 * 64];
            foreach (var source in sources)
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var usable = input.Length - (input.Length % 188);
                if (usable < 188)
                {
                    continue;
                }

                if (input.ReadByte() != 0x47)
                {
                    output.Dispose();
                    output = null;
                    TryDelete(destination);
                    return false;
                }

                input.Position = 0;
                long left = usable;
                while (left > 0)
                {
                    var want = (int)Math.Min(buffer.Length, left);
                    var read = input.Read(buffer, 0, want);
                    if (read <= 0)
                    {
                        break;
                    }

                    output.Write(buffer, 0, read);
                    left -= read;
                }
            }

            output.Dispose();
            output = null;
            if (FileLength(destination) <= 0)
            {
                TryDelete(destination);
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            try
            {
                output?.Dispose();
            }
            catch (Exception)
            {
                // The failed join is removed below.
            }

            TryDelete(destination);
            return false;
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }
    }

    private static string? UsableUrl(string? url) =>
        FfmpegDirectStreamCapture.IsHttp(url) ? url!.Trim() : null;

    private readonly record struct PendingCapture(string Id, string Url, TimeSpan Remaining);

    private readonly record struct PendingArm(string Id, string Url, DateTimeOffset Start, DateTimeOffset End);

    private static long FileLength(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return 0;
        }

        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void MoveFile(string source, string destination)
    {
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            File.Move(source, destination);
        }
        catch (IOException)
        {
            File.Copy(source, destination, overwrite: false);
            File.Delete(source);
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // A locked partial can wait. The next capture must not overwrite it.
        }
    }

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? First(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private sealed class StoredFile
    {
        public List<StoredJob> Jobs { get; set; } = new();
    }

    private sealed class StoredJob
    {
        public string Id { get; set; } = "";

        public string TvgId { get; set; } = "";

        public string Title { get; set; } = "";

        public string? Series { get; set; }

        public string? EpisodeTitle { get; set; }

        public int? Season { get; set; }

        public int? Episode { get; set; }

        public int? Year { get; set; }

        public bool? IsMovie { get; set; }

        public string? Category { get; set; }

        public DateTime? OriginalAirDate { get; set; }

        public string? StreamUrl { get; set; }

        public string? PlannedPath { get; set; }

        public DateTimeOffset Start { get; set; }

        public DateTimeOffset End { get; set; }

        public string Status { get; set; } = "";

        public string? Error { get; set; }

        public string? OutputPath { get; set; }

        public string? TempPath { get; set; }
    }
}
