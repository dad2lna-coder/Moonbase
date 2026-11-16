using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moonfin.Server.Services;

/// <summary>
/// Starts an ffmpeg copy of an http(s) stream. A missing ffmpeg, or a URL that
/// is not http(s), is a no. The URL is an argument, never written to disk.
/// </summary>
public interface IDirectStreamCapture
{
    bool TryStart(string id, string url, TimeSpan duration, string outputPath);

    void Stop(string id);

    void StopAll();
}

public sealed class FfmpegDirectStreamCapture : IDirectStreamCapture
{
    private readonly ConcurrentDictionary<string, Process> _processes = new(StringComparer.Ordinal);

    public bool TryStart(string id, string url, TimeSpan duration, string outputPath)
    {
        if (!IsHttp(url) || duration <= TimeSpan.Zero || string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var seconds = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds));
        try
        {
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var start = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-y");
            start.ArgumentList.Add("-i");
            start.ArgumentList.Add(url);
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("copy");
            start.ArgumentList.Add("-t");
            start.ArgumentList.Add(seconds.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(outputPath);

            var process = Process.Start(start);
            if (process == null)
            {
                return false;
            }

            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            _processes[id] = process;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Stop(string id)
    {
        if (!_processes.TryRemove(id, out var process))
        {
            return;
        }

        StopProcess(process);
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

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone.
        }

        process.Dispose();
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
}

public sealed class DirectRecordingListResponse
{
    [JsonPropertyName("recordings")]
    public IReadOnlyList<DirectRecordingView> Recordings { get; init; } = Array.Empty<DirectRecordingView>();
}

/// <summary>
/// Direct IPTV recordings for the signed-in user. The job file keeps the
/// guide identity only. Stream URLs stay in memory until capture starts, and
/// no API key is stored.
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
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredJob> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);

    public DirectIptvRecordingService(
        string directory,
        IDirectStreamCapture capture,
        Func<DateTimeOffset>? clock = null)
    {
        Directory.CreateDirectory(directory);
        _storePath = Path.Combine(directory, "jobs.json");
        _capture = capture;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Load();
    }

    public DirectRecordingView Schedule(
        string tvgId,
        string? title,
        DateTimeOffset start,
        DateTimeOffset end,
        string? streamUrl,
        string? recUrl)
    {
        var id = Guid.NewGuid().ToString("n");
        var job = new StoredJob
        {
            Id = id,
            TvgId = tvgId.Trim(),
            Title = title?.Trim() ?? "",
            Start = start.ToUniversalTime(),
            End = end.ToUniversalTime(),
        };
        lock (_gate)
        {
            _jobs[id] = job;
            SaveLocked();
        }

        // Prefer the recording hint. Neither URL is written into the job file.
        var url = FfmpegDirectStreamCapture.IsHttp(recUrl)
            ? recUrl!.Trim()
            : FfmpegDirectStreamCapture.IsHttp(streamUrl) ? streamUrl!.Trim() : null;
        Arm(id, url, job.Start, job.End);
        return ToView(job, _clock());
    }

    public DirectRecordingListResponse List()
    {
        var now = _clock();
        lock (_gate)
        {
            return new DirectRecordingListResponse
            {
                Recordings = _jobs.Values
                    .OrderBy(job => job.Start)
                    .Select(job => ToView(job, now))
                    .ToList(),
            };
        }
    }

    public bool Cancel(string id)
    {
        CancellationTokenSource? pending = null;
        lock (_gate)
        {
            if (!_jobs.Remove(id))
            {
                return false;
            }

            if (_pending.Remove(id, out var cts))
            {
                pending = cts;
            }

            SaveLocked();
        }

        pending?.Cancel();
        _capture.Stop(id);
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

    private void Arm(string id, string? url, DateTimeOffset start, DateTimeOffset end)
    {
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        var now = _clock();
        if (now >= end)
        {
            return;
        }

        var delay = now >= start ? TimeSpan.Zero : start - now;
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
                    return;
                }

                lock (_gate)
                {
                    if (!_jobs.ContainsKey(id))
                    {
                        return;
                    }
                }

                _capture.TryStart(id, url, remaining, OutputPath(id));
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

    private string OutputPath(string id) =>
        Path.Combine(Path.GetDirectoryName(_storePath)!, "files", id + ".ts");

    private void Load()
    {
        if (!File.Exists(_storePath))
        {
            return;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(_storePath), Json);
            if (stored?.Jobs == null)
            {
                return;
            }

            foreach (var job in stored.Jobs)
            {
                if (string.IsNullOrWhiteSpace(job.Id) || string.IsNullOrWhiteSpace(job.TvgId))
                {
                    continue;
                }

                _jobs[job.Id] = job;
            }
        }
        catch (Exception)
        {
            // A damaged job file shows an empty list rather than failing the request.
        }
    }

    private void SaveLocked()
    {
        var payload = JsonSerializer.Serialize(new StoredFile { Jobs = _jobs.Values.ToList() }, Json);
        AtomicFile.WriteAllText(_storePath, payload);
    }

    private static DirectRecordingView ToView(StoredJob job, DateTimeOffset now)
    {
        var status = now < job.Start ? "scheduled" : now < job.End ? "recording" : "completed";
        return new DirectRecordingView
        {
            Id = job.Id,
            TvgId = job.TvgId,
            Title = job.Title,
            Start = job.Start,
            End = job.End,
            Status = status,
        };
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

        public DateTimeOffset Start { get; set; }

        public DateTimeOffset End { get; set; }
    }
}
