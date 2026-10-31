using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Models;
using Moonfin.Server.Plugins;
using Moonfin.Server.Services;

namespace Moonfin.Plugins.RomManager;

public sealed class RomManagerModule : IMoonbaseModule
{
    private readonly RomChannel _channel;

    public RomManagerModule()
        : this(new RomChannel())
    {
    }

    public RomManagerModule(HttpMessageHandler handler)
        : this(new RomChannel(handler))
    {
    }

    internal RomManagerModule(RomChannel channel)
    {
        _channel = channel;
    }

    public string Id => "rom-manager";

    public string Version => "0.2.0";

    public IReadOnlyCollection<string> Provides { get; } = Array.Empty<string>();

    public IReadOnlyCollection<string> Requires { get; } = Array.Empty<string>();

    public Func<DateTimeOffset> Clock
    {
        get => _channel.Now;
        set => _channel.Now = value;
    }

    public void RegisterServices(IServiceCollection services)
    {
    }

    // Disable then enable calls OnStop and OnStart on this same instance.
    // Disposing the channel would make every later route fail.
    public void OnStop()
    {
        _channel.Invalidate();
    }

    public void MapRoutes(IMoonbaseRouteTable routes)
    {
        routes.MapPost("systems", Systems);
        routes.MapPost("games", Games);
        routes.MapPost("game", Game);
        routes.MapPost("launch", Launch);
        routes.MapPost("covers", Covers);
        routes.MapPost("duplicates", Duplicates);
        routes.MapPost("rescan", Rescan);
        routes.MapPost("trash-list", TrashList);
        routes.MapPost("deps", Deps);
        routes.MapPost("trash", Trash);
        routes.MapPost("restore", Restore);
    }

    public Task<PluginRouteResult> Systems(PluginRouteRequest request) => Guard(request, () => SystemsCore(request));

    public Task<PluginRouteResult> Games(PluginRouteRequest request) => Guard(request, () => GamesCore(request));

    public Task<PluginRouteResult> Game(PluginRouteRequest request) => Guard(request, () => GameCore(request));

    public Task<PluginRouteResult> Launch(PluginRouteRequest request) => Guard(request, () => LaunchCore(request));

    public Task<PluginRouteResult> Covers(PluginRouteRequest request) => Guard(request, () => CoversCore(request));

    public Task<PluginRouteResult> Duplicates(PluginRouteRequest request) => Guard(request, () => DuplicatesCore(request));

    public Task<PluginRouteResult> Rescan(PluginRouteRequest request) => Guard(request, () => RescanCore(request));

    public Task<PluginRouteResult> TrashList(PluginRouteRequest request) => Guard(request, () => TrashListCore(request));

    public Task<PluginRouteResult> Deps(PluginRouteRequest request) => Guard(request, () => DepsCore(request));

    public Task<PluginRouteResult> Trash(PluginRouteRequest request) => Guard(request, () => TrashCore(request));

    public Task<PluginRouteResult> Restore(PluginRouteRequest request) => Guard(request, () => RestoreCore(request));

    private async Task<PluginRouteResult> SystemsCore(PluginRouteRequest request)
    {
        var snapshot = await _channel.GetIndexAsync(request.Services, request.CancellationToken).ConfigureAwait(false);
        var rows = RomQuery.Systems(snapshot.Games);
        long bytes = 0;
        var games = 0;
        var roms = 0;
        foreach (var row in rows)
        {
            games += row.Games;
            roms += row.Roms;
            bytes += row.Bytes;
        }

        string? status = null;
        if (!snapshot.Indexing || snapshot.Items.Count > 0)
        {
            status = await _channel.GetStatusAsync(request.Services, request.CancellationToken).ConfigureAwait(false);
        }

        return PluginRouteResult.Json(new
        {
            ok = true,
            isAdmin = request.IsAdmin,
            indexing = snapshot.Indexing,
            message = snapshot.Indexing ? RomChannel.IndexingMessage : null,
            indexedAt = snapshot.Items.Count == 0 && snapshot.Indexing ? null : snapshot.FetchedAt.ToString("O"),
            meta = AllowFields(snapshot.MetaJson),
            status = AllowFields(status),
            systems = rows.Select(row => new
            {
                system = row.System,
                games = row.Games,
                roms = row.Roms,
                bytes = row.Bytes,
            }),
            totals = new { games, roms, bytes },
        });
    }

    private async Task<PluginRouteResult> GamesCore(PluginRouteRequest request)
    {
        var body = Body(request);
        var snapshot = await _channel.GetIndexAsync(request.Services, request.CancellationToken).ConfigureAwait(false);
        var keys = StringList(body, "keys");
        var filtered = RomQuery.Filter(snapshot.Games, Str(body, "q"), Str(body, "system"), keys, Str(body, "sort") ?? "title");
        var (offset, limit) = Page(body, 48);
        var page = RomQuery.Page(filtered, offset, limit);
        return PluginRouteResult.Json(new
        {
            ok = true,
            indexing = snapshot.Indexing,
            message = snapshot.Indexing ? RomChannel.IndexingMessage : null,
            total = page.Total,
            items = page.Page.Select(Card),
        });
    }

    private async Task<PluginRouteResult> GameCore(PluginRouteRequest request)
    {
        var body = Body(request);
        var key = Str(body, "key");
        if (string.IsNullOrWhiteSpace(key))
        {
            return Fail("key is required");
        }

        var snapshot = await _channel.GetIndexAsync(request.Services, request.CancellationToken).ConfigureAwait(false);
        if (!snapshot.ByKey.TryGetValue(key, out var game))
        {
            if (snapshot.Indexing && snapshot.Items.Count == 0)
            {
                return PluginRouteResult.Json(new { ok = false, error = RomChannel.IndexingMessage, indexing = true });
            }

            return Fail("Game not found");
        }

        string? details = null;
        if (RomPaths.TryValidate(game.CoverPath, out _))
        {
            details = await _channel.GetGameDetailsAsync(request.Services, game.CoverPath, request.CancellationToken).ConfigureAwait(false);
        }

        var detail = DetailFields(details);
        var related = RomQuery.Related(game, snapshot.Games, 12);
        return PluginRouteResult.Json(new
        {
            ok = true,
            indexing = snapshot.Indexing,
            game = new
            {
                key = game.Key,
                title = game.Title,
                system = game.Platform,
                year = detail.Year ?? game.Year,
                genre = detail.Genre ?? game.Genre,
                publisher = detail.Publisher ?? game.Publisher,
                developer = detail.Developer ?? game.Developer,
                versionsCount = game.Members.Count,
                sizeBytes = game.SizeBytes,
                coverPath = game.CoverPath,
                lb = game.Lb,
                cover = game.Cover,
                description = detail.Description,
                rating = detail.Rating,
                ratingCount = detail.RatingCount,
                maxPlayers = detail.MaxPlayers,
                esrb = detail.Esrb,
                lbUrl = detail.LbUrl,
                versions = game.Members.Select(VersionView),
                related = related.Select(Card),
            },
        });
    }

    private async Task<PluginRouteResult> LaunchCore(PluginRouteRequest request)
    {
        var body = Body(request);
        var key = Str(body, "key");
        var path = Str(body, "p");
        if (string.IsNullOrWhiteSpace(key))
        {
            return Fail("unknown key");
        }

        if (!RomPaths.TryValidate(path, out var trash) || string.IsNullOrEmpty(path))
        {
            return Fail("invalid path");
        }

        if (trash)
        {
            return Fail("trash path");
        }

        var snapshot = await _channel.GetIndexAsync(request.Services, request.CancellationToken).ConfigureAwait(false);
        if (!snapshot.ByKey.TryGetValue(key, out var game))
        {
            return Fail("unknown key");
        }

        RomEntry? member = null;
        foreach (var candidate in game.Members)
        {
            if (string.Equals(candidate.Path, path, StringComparison.Ordinal))
            {
                member = candidate;
                break;
            }
        }

        if (member == null)
        {
            return Fail("not a member");
        }

        var fileName = RomTags.FileName(member.RomFile, member.Path);
        if (string.IsNullOrEmpty(fileName)
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || fileName.Contains("..", StringComparison.Ordinal))
        {
            return Fail("invalid path");
        }

        var romRoot = RomRoot(request.Services);
        var absolute = Path.Combine(romRoot, path.Replace('/', Path.DirectorySeparatorChar), fileName);
        if (!IsContained(romRoot, absolute))
        {
            return Fail("invalid path");
        }

        var games = request.Services?.GetService(typeof(GamesService)) as GamesService;
        var located = games?.LocateRomFile(absolute);
        var title = game.Title;
        var system = located == null || string.IsNullOrWhiteSpace(located.SystemName) ? game.Platform : located.SystemName;
        var core = located?.Core;
        if (IsPsp(system, core))
        {
            return LaunchAnswer(located, title, system, false, "PSP is too heavy for iPhone");
        }

        if (IsUnemulated(system))
        {
            return LaunchAnswer(located, title, system, false, "No emulator for this system yet");
        }

        if (located == null)
        {
            return LaunchAnswer(null, title, game.Platform, false, "No game library covers this ROM.");
        }

        if (NeedsBios(located.Core, located.FileName, system) && !HasBios(romRoot, path.Split('/')[0]))
        {
            return LaunchAnswer(located, title, system, false, "BIOS is missing for this system.");
        }

        return LaunchAnswer(located, title, system, true, null);
    }

    private static PluginRouteResult LaunchAnswer(GameLaunchTarget? target, string title, string system, bool playable, string? reason)
    {
        return PluginRouteResult.Json(new
        {
            ok = true,
            libraryId = target?.LibraryId,
            gameId = target?.GameId,
            core = target?.Core,
            fileName = target?.FileName,
            title,
            system,
            playable,
            reason,
        });
    }

    private const string DefaultRomRoot = "/data/media/roms";

    private static string RomRoot(IServiceProvider? services)
    {
        var configured = ReadRomRoot(services);
        if (string.IsNullOrWhiteSpace(configured) || configured.Contains("..", StringComparison.Ordinal))
        {
            return DefaultRomRoot;
        }

        try
        {
            return Path.GetFullPath(configured);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return DefaultRomRoot;
        }
    }

    private static string? ReadRomRoot(IServiceProvider? services)
    {
        if (services?.GetService(typeof(MoonfinPluginRegistry)) is not MoonfinPluginRegistry registry)
        {
            return null;
        }

        string? json;
        try
        {
            json = registry.ReadConfig("rom-manager");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }

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

            if (doc.RootElement.TryGetProperty("settings", out var settings)
                && settings.ValueKind == JsonValueKind.Object
                && settings.TryGetProperty("romRoot", out var nested)
                && nested.ValueKind == JsonValueKind.String)
            {
                return nested.GetString();
            }

            if (doc.RootElement.TryGetProperty("romRoot", out var top) && top.ValueKind == JsonValueKind.String)
            {
                return top.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static bool IsContained(string root, string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var rootFull = Path.GetFullPath(root);
            var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return full.StartsWith(prefix, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static bool IsPsp(string? system, string? core)
    {
        if (string.Equals(core, "psp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = Fold(system);
        return name is "psp" or "playstationportable";
    }

    private static bool IsUnemulated(string? system)
    {
        var name = Fold(system);
        if (name.Length == 0)
        {
            return false;
        }

        if (name.Contains("coleco", StringComparison.Ordinal) || name.Contains("channelf", StringComparison.Ordinal))
        {
            return true;
        }

        return name is "msx" or "msx2" or "msx2plus" || name.StartsWith("msx", StringComparison.Ordinal);
    }

    private static bool NeedsBios(string? core, string? fileName, string? system)
    {
        var name = Fold(system);
        if ((fileName ?? string.Empty).EndsWith(".fds", StringComparison.OrdinalIgnoreCase)
            || name is "fds" or "famicomdisksystem")
        {
            return true;
        }

        return core is "psx" or "segaCD" or "segaSaturn" or "pcfx" or "3do" or "amiga";
    }

    private static bool HasBios(string romRoot, string systemFolder)
    {
        if (string.IsNullOrWhiteSpace(systemFolder) || systemFolder.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (DirectoryHasFile(Path.Combine(romRoot, ".bios", systemFolder)))
        {
            return true;
        }

        return HasBiosFile(Path.Combine(romRoot, systemFolder));
    }

    private static bool DirectoryHasFile(string directory)
    {
        try
        {
            return Directory.Exists(directory) && Directory.EnumerateFiles(directory).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasBiosFile(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return false;
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var ext = Path.GetExtension(file);
                if (ext.Equals(".bin", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".bios", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".rom", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".img", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".sys", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".bs", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    private async Task<PluginRouteResult> CoversCore(PluginRouteRequest request)
    {
        var body = Body(request);
        if (!TryProp(body, "items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return Fail("items is required");
        }

        if (items.GetArrayLength() > RomChannel.MaxCovers)
        {
            return Fail("too many covers");
        }

        var jobs = new List<(string Path, string? Lb, int Width, bool Trash)>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var path = Str(item, "p");
            if (!RomPaths.TryValidate(path, out var trash) || string.IsNullOrEmpty(path))
            {
                continue;
            }

            var width = (int)Long(item, "w");
            if (width != 400)
            {
                width = 200;
            }

            jobs.Add((path, Str(item, "lb"), width, trash));
        }

        var fetched = new byte[]?[jobs.Count];
        using var gate = new SemaphoreSlim(RomChannel.CoverParallelism);
        var tasks = new Task[jobs.Count];
        for (var i = 0; i < jobs.Count; i++)
        {
            var index = i;
            tasks[i] = FetchCover(index);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var covers = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = 0;
        for (var i = 0; i < jobs.Count; i++)
        {
            var bytes = fetched[i];
            if (bytes == null || bytes.Length == 0 || bytes.Length > RomChannel.MaxImageBytes || !RomChannel.IsJpeg(bytes))
            {
                continue;
            }

            var uriLength = RomChannel.DataUriLength(bytes.Length);
            if (uriLength > RomChannel.MaxDataUriChars || used + uriLength > RomChannel.MaxDataUriChars)
            {
                continue;
            }

            covers[jobs[i].Path] = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
            used += uriLength;
        }

        return PluginRouteResult.Json(new { ok = true, covers });

        async Task FetchCover(int index)
        {
            await gate.WaitAsync(request.CancellationToken).ConfigureAwait(false);
            try
            {
                var job = jobs[index];
                fetched[index] = await _channel.GetThumbAsync(request.Services, job.Path, job.Lb, job.Width, job.Trash, request.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (!request.CancellationToken.IsCancellationRequested)
            {
                fetched[index] = null;
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task<PluginRouteResult> DuplicatesCore(PluginRouteRequest request)
    {
        if (!request.IsAdmin)
        {
            return AdminOnly();
        }

        var body = Body(request);
        var snapshot = await _channel.GetIndexAsync(request.Services, request.CancellationToken).ConfigureAwait(false);
        var platform = Str(body, "system");
        var groups = RomQuery.IdenticalCopies(snapshot.Items, platform);
        var (offset, limit) = Page(body, 30);
        var page = RomQuery.Page(groups, offset, limit);
        return PluginRouteResult.Json(new
        {
            ok = true,
            indexing = snapshot.Indexing,
            total = page.Total,
            versionGroups = RomQuery.VersionGroupCount(snapshot.Items, platform),
            groups = page.Page.Select(group => new
            {
                id = group.Id,
                system = group.System,
                members = group.Members.Select(member => new
                {
                    p = member.Path,
                    title = string.IsNullOrWhiteSpace(member.Title) ? member.Path : member.Title,
                    file = member.RomFile,
                    system = member.Platform,
                    sizeBytes = member.Bytes,
                    ks = member.KeepCopy,
                    cover = member.Cover,
                }),
            }),
        });
    }

    private async Task<PluginRouteResult> RescanCore(PluginRouteRequest request)
    {
        if (!request.IsAdmin)
        {
            return AdminOnly();
        }

        Log(request, "rescan", null);
        var result = await _channel.PostAsync(request.Services, "/api/romman/rescan", "{}", request.CancellationToken).ConfigureAwait(false);
        if (result.Status is < 200 or > 299)
        {
            return Fail("Channel Manager returned " + result.Status + ".");
        }

        await _channel.Invalidate().ConfigureAwait(false);
        return PluginRouteResult.Json(new { ok = true });
    }

    private async Task<PluginRouteResult> TrashListCore(PluginRouteRequest request)
    {
        if (!request.IsAdmin)
        {
            return AdminOnly();
        }

        Log(request, "trash-list", null);
        var result = await _channel.GetAsync(request.Services, "/api/romman/trash", request.CancellationToken).ConfigureAwait(false);
        if (result.Status is < 200 or > 299)
        {
            return Fail("Channel Manager returned " + result.Status + ".");
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(result.Body) ? "{}" : result.Body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Fail("Channel Manager returned invalid JSON.");
        }

        var items = new List<object>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                items.Add(new
                {
                    p = Str(item, "p"),
                    system = Str(item, "s"),
                    title = Str(item, "t"),
                    sizeBytes = Long(item, "z"),
                    cover = Long(item, "c"),
                    conflict = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("conflict", out var conflict) && conflict.ValueKind == JsonValueKind.True,
                });
            }
        }

        return PluginRouteResult.Json(new
        {
            ok = true,
            items,
            bytes = root.ValueKind == JsonValueKind.Object ? Long(root, "bytes") : 0,
        });
    }

    private async Task<PluginRouteResult> DepsCore(PluginRouteRequest request)
    {
        if (!request.IsAdmin)
        {
            return AdminOnly();
        }

        if (!TryPaths(Body(request), requireIndex: false, out var paths, out var error))
        {
            return Fail(error!);
        }

        Log(request, "deps", paths);
        var result = await _channel.PostAsync(request.Services, "/api/romman/deps", PathsJson(paths, ack: null), request.CancellationToken)
            .ConfigureAwait(false);
        if (result.Status is < 200 or > 299)
        {
            return UpstreamFailure(result);
        }

        var warnings = ReadWarnings(result.Body);
        return PluginRouteResult.Json(new { ok = true, warnings });
    }

    private async Task<PluginRouteResult> TrashCore(PluginRouteRequest request)
    {
        if (!request.IsAdmin)
        {
            return AdminOnly();
        }

        var body = Body(request);
        if (!TryPaths(body, requireIndex: true, out var paths, out var error))
        {
            return Fail(error!);
        }

        var snapshot = await _channel.GetIndexAsync(request.Services, request.CancellationToken).ConfigureAwait(false);
        foreach (var path in paths)
        {
            if (!snapshot.Paths.Contains(path))
            {
                return Fail("unknown path");
            }
        }

        Log(request, "trash", paths);
        var ack = Bool(body, "ackDeps") || Bool(body, "ack_deps");
        var result = await _channel.PostAsync(
            request.Services,
            "/api/romman/delete",
            PathsJson(paths, ack),
            request.CancellationToken).ConfigureAwait(false);
        if (result.Status == 409)
        {
            return NeedsAck(result.Body);
        }

        if (result.Status is < 200 or > 299)
        {
            return UpstreamFailure(result);
        }

        await _channel.Invalidate().ConfigureAwait(false);
        return PluginRouteResult.Json(new { ok = true, moved = paths.Count });
    }

    private async Task<PluginRouteResult> RestoreCore(PluginRouteRequest request)
    {
        if (!request.IsAdmin)
        {
            return AdminOnly();
        }

        if (!TryPaths(Body(request), requireIndex: false, out var paths, out var error))
        {
            return Fail(error!);
        }

        Log(request, "restore", paths);
        var result = await _channel.PostAsync(request.Services, "/api/romman/restore", PathsJson(paths, ack: null), request.CancellationToken)
            .ConfigureAwait(false);
        if (result.Status is < 200 or > 299)
        {
            return UpstreamFailure(result);
        }

        await _channel.Invalidate().ConfigureAwait(false);
        return PluginRouteResult.Json(new { ok = true, restored = paths.Count });
    }

    private static PluginRouteResult NeedsAck(string body)
    {
        return PluginRouteResult.Json(new
        {
            ok = false,
            error = "needs_ack",
            needsAck = true,
            needs_ack = true,
            warnings = ReadWarnings(body),
        });
    }

    private static PluginRouteResult UpstreamFailure(RomChannel.ChannelResult result)
    {
        if (result.Status == 409)
        {
            return NeedsAck(result.Body);
        }

        return Fail("Channel Manager returned " + result.Status + ".");
    }

    private static List<JsonElement> ReadWarnings(string? body)
    {
        var warnings = new List<JsonElement>();
        if (string.IsNullOrWhiteSpace(body))
        {
            return warnings;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("warnings", out var value))
            {
                return warnings;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    warnings.Add(item.Clone());
                }
            }
            else if (value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined)
            {
                warnings.Add(value.Clone());
            }
        }
        catch (JsonException)
        {
            return warnings;
        }

        return warnings;
    }

    private static bool TryPaths(JsonElement body, bool requireIndex, out List<string> paths, out string? error)
    {
        _ = requireIndex;
        paths = new List<string>();
        error = null;
        if (!TryProp(body, "paths", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            error = "paths required";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 } path)
            {
                error = "invalid path";
                return false;
            }

            if (!RomPaths.TryValidate(path, out _))
            {
                error = "invalid path";
                return false;
            }

            if (!seen.Add(path))
            {
                continue;
            }

            paths.Add(path);
        }

        if (paths.Count == 0)
        {
            error = "paths required";
            return false;
        }

        if (paths.Count > RomChannel.MaxPaths)
        {
            error = "too many paths";
            return false;
        }

        return true;
    }

    private static string PathsJson(IReadOnlyList<string> paths, bool? ack)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("paths");
            writer.WriteStartArray();
            foreach (var path in paths)
            {
                writer.WriteStringValue(path);
            }

            writer.WriteEndArray();
            if (ack.HasValue)
            {
                writer.WriteBoolean("ack_deps", ack.Value);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static object Card(GameRecord game) => new
    {
        key = game.Key,
        title = game.Title,
        system = game.Platform,
        year = game.Year,
        genre = game.Genre,
        versions = game.Members.Count,
        sizeBytes = game.SizeBytes,
        added = game.Added,
        coverPath = game.CoverPath,
        lb = game.Lb,
        cover = game.Cover == 0 ? (int?)null : game.Cover,
    };

    private static object VersionView(RomEntry entry)
    {
        var file = RomTags.FileName(entry.RomFile, entry.Path);
        return new
        {
            p = entry.Path,
            file,
            ext = RomTags.ExtensionOf(file),
            region = RomTags.Region(entry.Tags),
            revision = RomTags.Revision(entry.Tags),
            tags = entry.Tags,
            sizeBytes = entry.Bytes,
            best = entry.BestVersion,
            cover = entry.Cover,
        };
    }

    private static Detail DetailFields(string? json)
    {
        var detail = new Detail();
        if (string.IsNullOrWhiteSpace(json))
        {
            return detail;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return detail;
            }

            detail.Description = Blank(Str(root, "description"));
            var year = Long(root, "year");
            detail.Year = year is >= 1950 and <= 2100 ? (int)year : null;
            detail.Genre = Blank(Str(root, "genre"));
            detail.Developer = Blank(Str(root, "developer"));
            detail.Publisher = Blank(Str(root, "publisher"));
            if (TryProp(root, "rating", out var rating) && rating.ValueKind == JsonValueKind.Number && rating.TryGetDouble(out var stars) && stars > 0)
            {
                detail.Rating = Math.Round(stars, 2);
            }

            var count = Long(root, "rating_count");
            if (count == 0)
            {
                count = Long(root, "ratingCount");
            }

            detail.RatingCount = count > 0 ? count : null;
            var players = Long(root, "max_players");
            if (players == 0)
            {
                players = Long(root, "maxPlayers");
            }

            detail.MaxPlayers = players > 0 ? players : null;
            detail.Esrb = Blank(Str(root, "esrb"));
            detail.LbUrl = Blank(Str(root, "lb_url")) ?? Blank(Str(root, "lbUrl"));
        }
        catch (JsonException)
        {
            return new Detail();
        }

        return detail;
    }

    private static readonly HashSet<string> PublicFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "counts", "count", "games", "roms", "items", "bytes", "files", "total", "size",
        "state", "status",
        "built", "builtAt", "built_at", "builtTime", "built_time", "indexedAt", "indexed_at", "generatedAt", "generated_at", "mtime",
    };

    private static Dictionary<string, object?>? AllowFields(string? json)
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

            var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!PublicFields.Contains(prop.Name))
                {
                    continue;
                }

                var value = PublicValue(prop.Value);
                if (value != null)
                {
                    bag[prop.Name] = value;
                }
            }

            return bag.Count == 0 ? null : bag;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object? PublicValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Number:
                return value.TryGetInt64(out var number) ? number : value.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Object:
                var nested = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var prop in value.EnumerateObject())
                {
                    if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        continue;
                    }

                    var inner = PublicValue(prop.Value);
                    if (inner != null)
                    {
                        nested[prop.Name] = inner;
                    }
                }

                return nested.Count == 0 ? null : nested;
            default:
                return null;
        }
    }

    private void Log(PluginRouteRequest request, string action, IReadOnlyList<string>? paths)
    {
        var factory = request.Services?.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        var logger = factory?.CreateLogger("Moonfin.Plugin.RomManager");
        logger?.LogInformation(
            "ROM Manager {Action} user {UserId} paths {Paths}",
            action,
            request.UserId?.ToString() ?? "unknown",
            paths == null ? string.Empty : string.Join(", ", paths));
    }

    private static async Task<PluginRouteResult> Guard(PluginRouteRequest request, Func<Task<PluginRouteResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ChannelException ex)
        {
            return Fail(ex.Message);
        }
        catch (JsonException)
        {
            return Fail("Channel Manager returned invalid JSON.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return Fail("Channel Manager is unreachable");
        }
        catch (Exception ex)
        {
            try
            {
                var factory = request.Services?.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
                factory?.CreateLogger("Moonfin.Plugin.RomManager").LogError(ex, "ROM Manager internal error");
            }
            catch (Exception)
            {
                // A logging failure must still answer the page.
            }

            return Fail("internal error");
        }
    }

    private static PluginRouteResult Fail(string error) => PluginRouteResult.Json(new { ok = false, error });

    private static PluginRouteResult AdminOnly() => Fail("admin only");

    private static JsonElement Body(PluginRouteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return EmptyBody();
        }

        try
        {
            using var doc = JsonDocument.Parse(request.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ChannelException("Request must be JSON.");
            }

            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ChannelException("Request must be JSON.");
        }
    }

    private static JsonElement EmptyBody()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private static (int Offset, int Limit) Page(JsonElement body, int fallback)
    {
        var offset = (int)Long(body, "offset");
        var limit = (int)Long(body, "limit");
        if (offset < 0)
        {
            offset = 0;
        }

        return (offset, RomQuery.ClampLimit(limit, fallback));
    }

    private static List<string>? StringList(JsonElement body, string name)
    {
        if (!TryProp(body, name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
            {
                list.Add(text);
            }
        }

        return list;
    }

    private static bool TryProp(JsonElement body, string name, out JsonElement value)
    {
        if (body.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in body.EnumerateObject())
            {
                if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? Str(JsonElement body, string name)
    {
        if (!TryProp(body, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static long Long(JsonElement body, string name)
    {
        if (!TryProp(body, name, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static bool Bool(JsonElement body, string name)
    {
        if (!TryProp(body, name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt64(out var number) && number != 0,
            JsonValueKind.String => value.GetString() is "1" or "true" or "True",
            _ => false,
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class Detail
    {
        public string? Description { get; set; }

        public int? Year { get; set; }

        public string? Genre { get; set; }

        public string? Developer { get; set; }

        public string? Publisher { get; set; }

        public double? Rating { get; set; }

        public long? RatingCount { get; set; }

        public long? MaxPlayers { get; set; }

        public string? Esrb { get; set; }

        public string? LbUrl { get; set; }
    }
}
