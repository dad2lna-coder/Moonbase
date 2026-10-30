using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Plugins;

namespace Moonfin.Server.Api;

/// <summary>
/// Moonbase plugin registry, client files, per-user data, admin config, and the allowlisted proxy.
/// </summary>
[ApiController]
[Route("Moonfin")]
public class MoonfinPluginsController : ControllerBase
{
    private const int MaxDataBytes = 256 * 1024;

    private const string ClientPagePolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'; frame-ancestors 'self'; object-src 'none'; base-uri 'self'";

    private static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html; charset=utf-8",
            [".htm"] = "text/html; charset=utf-8",
            [".js"] = "application/javascript",
            [".mjs"] = "text/javascript",
            [".css"] = "text/css",
            [".json"] = "application/json",
            [".map"] = "application/json",
            [".svg"] = "image/svg+xml",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".ico"] = "image/x-icon",
            [".txt"] = "text/plain; charset=utf-8",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
            [".ttf"] = "font/ttf",
        };

    private readonly MoonfinPluginRegistry _registry;
    private readonly MoonbaseModuleCatalog _catalog;

    public MoonfinPluginsController(MoonfinPluginRegistry registry, MoonbaseModuleCatalog catalog)
    {
        _registry = registry;
        _catalog = catalog;
    }

    [HttpGet("Plugins")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PluginListResponse> List()
    {
        var statuses = _registry.List();
        return Ok(new PluginListResponse
        {
            HostApi = MoonbasePlugins.HostApi,
            Plugins = statuses.Select(ToItem).ToList(),
            Capabilities = _registry.PingFlags(),
        });
    }

    // The plugin WebView sends Authorization only on the first page load, so client files are
    // anonymous. Anything outside that plugin's client folder stays unreadable. A root-level
    // client.html or client.js is the one exception: that file only, never the plugin root.
    [HttpGet("Plugins/{id}/files/{**path}")]
    [AllowAnonymous]
    public IActionResult PluginFile(string id, string path)
    {
        if (!RequestPathAllowsFiles(Request.Path.Value) || !TryPublicPlugin(id, out var manifest) || manifest == null)
        {
            return MissingFile();
        }

        var pluginDir = Path.Combine(_registry.Root, manifest.Id);
        if (!IsUnderClientFolder(manifest, path)
            || !TrySafeFile(pluginDir, path, out var full)
            || !IsRealFileInside(pluginDir, full))
        {
            return MissingFile();
        }

        var extension = Path.GetExtension(full);
        if (!ContentTypes.TryGetValue(extension, out var type) || !IsResolvedInsideClient(manifest, pluginDir, full))
        {
            return MissingFile();
        }

        var etag = FileEtag(new FileInfo(full));
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["ETag"] = etag;
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            Response.Headers["Content-Security-Policy"] = ClientPagePolicy;
        }

        if (NoneMatch(etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return PhysicalFile(full, type);
    }

    [HttpGet("Plugins/{id}/Data/{key}")]
    [Authorize]
    public IActionResult GetData(string id, string key)
    {
        if (!TryActive(id, out var error))
        {
            return error!;
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "User not authenticated" });
        }

        string path;
        try
        {
            path = _registry.DataPath(id, userId.Value, key);
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { error = "Invalid data key." });
        }

        if (!System.IO.File.Exists(path))
        {
            return NotFound(new { error = "No value stored." });
        }

        return Content(System.IO.File.ReadAllText(path), "application/json; charset=utf-8");
    }

    [HttpPut("Plugins/{id}/Data/{key}")]
    [Authorize]
    public async Task<IActionResult> PutData(string id, string key, CancellationToken cancellationToken)
    {
        if (!TryActive(id, out var error))
        {
            return error!;
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "User not authenticated" });
        }

        string path;
        try
        {
            path = _registry.DataPath(id, userId.Value, key);
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { error = "Invalid data key." });
        }

        var body = await ReadBody(cancellationToken).ConfigureAwait(false);
        if (body == null)
        {
            return BadRequest(new { error = "Value is too large." });
        }

        try
        {
            using var _ = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Value must be JSON." });
        }

        if (!PluginDataQuota.TryAccept(Path.GetDirectoryName(path)!, key, Encoding.UTF8.GetByteCount(body), out var quotaError))
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = quotaError });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await System.IO.File.WriteAllTextAsync(path, body, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpDelete("Plugins/{id}/Data/{key}")]
    [Authorize]
    public IActionResult DeleteData(string id, string key)
    {
        if (!TryActive(id, out var error))
        {
            return error!;
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "User not authenticated" });
        }

        string path;
        try
        {
            path = _registry.DataPath(id, userId.Value, key);
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { error = "Invalid data key." });
        }

        if (System.IO.File.Exists(path))
        {
            System.IO.File.Delete(path);
        }

        return NoContent();
    }

    [HttpPost("Plugins/{id}/Proxy")]
    [Authorize]
    public async Task<IActionResult> Proxy(string id, CancellationToken cancellationToken)
    {
        if (!TryActive(id, out var error))
        {
            return error!;
        }

        var status = _registry.Find(id);
        if (status == null || !PluginRouteAccess.CanProxy(status.Enabled, status.Active, status.Manifest.ProxyAccess, this.IsAdminFromClaims()))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Plugin proxy is limited to administrators." });
        }

        var body = await ReadBody(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return BadRequest(new { error = "Proxy request is required." });
        }

        PluginProxyRequestBody? request;
        try
        {
            request = JsonSerializer.Deserialize<PluginProxyRequestBody>(body, PluginJson.Manifest);
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Proxy request must be JSON." });
        }

        var result = await PluginProxy.SendAsync(_registry.ReadConfig(id), request ?? new PluginProxyRequestBody(), cancellationToken)
            .ConfigureAwait(false);
        // MOFIN-PLUGIN-HOOK: every proxy response is nosniff and sandboxed. Images are raw bytes.
        PluginProxy.ApplyResponseHeaders(Response.Headers);
        if (result.RawBody != null)
        {
            Response.StatusCode = result.StatusCode;
            return File(result.RawBody, result.ContentType);
        }

        return new ContentResult
        {
            StatusCode = result.StatusCode,
            ContentType = result.ContentType,
            Content = result.Body,
        };
    }

    [HttpGet("Plugins/{id}/{**path}")]
    [HttpPost("Plugins/{id}/{**path}")]
    [HttpPut("Plugins/{id}/{**path}")]
    [HttpDelete("Plugins/{id}/{**path}")]
    [Authorize]
    public async Task<IActionResult> Dispatch(string id, string? path, CancellationToken cancellationToken)
    {
        path ??= string.Empty;
        var head = path.Replace('\\', '/').Trim('/');
        var first = head.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (first.Equals("files", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Data", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Proxy", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = "Not found." });
        }

        var status = _registry.Find(id);
        if (status == null || !status.Enabled)
        {
            return NotFound(new { error = "Unknown plugin." });
        }

        if (!status.Active)
        {
            return Conflict(new { error = "Plugin is dormant.", dormantReason = status.DormantReason });
        }

        if (!_catalog.TryGetRoute(status.Manifest.Id, Request.Method, head, out var handler) || handler == null)
        {
            return NotFound(new { error = "Not found." });
        }

        var body = await ReadBody(cancellationToken).ConfigureAwait(false);
        var result = await handler(new PluginRouteRequest
        {
            Method = Request.Method,
            Path = head,
            Body = body,
            UserId = this.GetUserIdFromClaims(),
            IsAdmin = this.IsAdminFromClaims(),
            Services = HttpContext.RequestServices,
            CancellationToken = cancellationToken,
        }).ConfigureAwait(false);

        return new ContentResult
        {
            StatusCode = result.StatusCode,
            ContentType = result.ContentType,
            Content = result.Body,
        };
    }

    [HttpPost("Admin/Plugins/Install")]
    [Authorize(Policy = "RequiresElevation")]
    [RequestSizeLimit(32L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 32L * 1024 * 1024)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Install(IFormFile? file, CancellationToken cancellationToken)
    {
        Stream? stream = file?.OpenReadStream();
        var ownsStream = stream != null;
        if (stream == null)
        {
            if (Request.ContentLength == 0)
            {
                return BadRequest(new { error = "Upload a plugin zip." });
            }

            stream = Request.Body;
        }

        try
        {
            var result = await PluginZipInstaller.InstallAsync(stream, _registry.Root, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!result.Ok)
            {
                return BadRequest(new { error = result.Error });
            }

            _registry.Invalidate();
            var status = _registry.Find(result.Id!);
            return Ok(new
            {
                id = result.Id,
                restartRequired = result.RestartRequired,
                enabled = status?.Enabled ?? true,
                active = status?.Active ?? false,
                dormantReason = status?.DormantReason,
            });
        }
        catch (Exception)
        {
            return BadRequest(new { error = "The plugin could not be installed." });
        }
        finally
        {
            if (ownsStream)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    [HttpPost("Admin/Plugins/{id}/Enable")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult Enable(string id) => SetEnabled(id, true);

    [HttpPost("Admin/Plugins/{id}/Disable")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult Disable(string id) => SetEnabled(id, false);

    [HttpDelete("Admin/Plugins/{id}")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult Delete(string id)
    {
        if (!_registry.TryDelete(id, out var error))
        {
            return NotFound(new { error });
        }

        return Ok(new { deleted = true, id });
    }

    [HttpGet("Admin/Plugins/{id}/Config")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult GetConfig(string id)
    {
        if (_registry.Find(id) == null)
        {
            return NotFound(new { error = "Unknown plugin." });
        }

        return Content(PluginConfigStore.ToClientJson(_registry.ReadConfig(id)), "application/json; charset=utf-8");
    }

    [HttpPut("Admin/Plugins/{id}/Config")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> PutConfig(string id, CancellationToken cancellationToken)
    {
        if (_registry.Find(id) == null)
        {
            return NotFound(new { error = "Unknown plugin." });
        }

        var body = await ReadBody(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return BadRequest(new { error = "Config must be a JSON object." });
        }

        string merged;
        try
        {
            merged = PluginConfigStore.Merge(_registry.ReadConfig(id), body);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            return BadRequest(new { error = "Config must be a JSON object." });
        }

        _registry.WriteConfig(id, merged);
        return Content(PluginConfigStore.ToClientJson(merged), "application/json; charset=utf-8");
    }

    private ActionResult SetEnabled(string id, bool enabled)
    {
        var status = _registry.SetEnabled(id, enabled, HttpContext.RequestServices);
        if (status == null)
        {
            return NotFound(new { error = "Unknown plugin." });
        }

        return Ok(ToItem(status));
    }

    private bool TryPublicPlugin(string id, out PluginManifest? manifest)
    {
        manifest = null;
        if (!PluginManifest.IsValidId(id))
        {
            return false;
        }

        var status = _registry.Find(id);
        if (status is not { Enabled: true, Active: true })
        {
            return false;
        }

        manifest = status.Manifest;
        return true;
    }

    private IActionResult MissingFile() => NotFound(new { error = "File not found." });

    private bool NoneMatch(string etag)
    {
        if (!Request.Headers.TryGetValue("If-None-Match", out var header))
        {
            return false;
        }

        var value = header.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var part in value.Split(','))
        {
            var token = part.Trim();
            if (token == "*")
            {
                return true;
            }

            if (token.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            {
                token = token[2..].Trim();
            }

            if (string.Equals(token, etag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string FileEtag(FileInfo info) =>
        "\"" + info.Length.ToString("x") + "-" + info.LastWriteTimeUtc.Ticks.ToString("x") + "\"";

    private static bool RequestPathAllowsFiles(string? path)
    {
        if (string.IsNullOrEmpty(path) || !TryUnescape(path, out var unescaped))
        {
            return false;
        }

        return PathTextIsSafe(path) && PathTextIsSafe(unescaped);
    }

    private static bool TryPublicRelative(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || !TryUnescape(relative, out var unescaped))
        {
            return false;
        }

        return RelativeTextIsSafe(relative) && RelativeTextIsSafe(unescaped);
    }

    private static bool TryUnescape(string value, out string unescaped)
    {
        try
        {
            unescaped = Uri.UnescapeDataString(value);
            return true;
        }
        catch (UriFormatException)
        {
            unescaped = string.Empty;
            return false;
        }
    }

    private static bool PathTextIsSafe(string value)
    {
        if (ContainsEncodedDotOrSlash(value) || value.Contains('\\', StringComparison.Ordinal) || value.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var segment in value.Split('/'))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            if (segment[0] == '.'
                || segment[^1] == '.'
                || segment[^1] == ' '
                || segment.Contains(':', StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RelativeTextIsSafe(string relative)
    {
        if (relative.StartsWith('/')
            || relative.StartsWith('\\')
            || Path.IsPathRooted(relative)
            || ContainsEncodedDotOrSlash(relative)
            || relative.Contains('\\', StringComparison.Ordinal)
            || relative.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = relative.Split('/');
        if (segments.Length == 0)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0
                || segment[0] == '.'
                || segment[^1] == '.'
                || segment[^1] == ' '
                || segment.Contains(':', StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsEncodedDotOrSlash(string value) =>
        value.Contains("%2e", StringComparison.OrdinalIgnoreCase)
        || value.Contains("%2f", StringComparison.OrdinalIgnoreCase)
        || value.Contains("%5c", StringComparison.OrdinalIgnoreCase)
        || value.Contains("%252e", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderClientFolder(PluginManifest manifest, string? relative)
    {
        if (!TryPublicRelative(relative))
        {
            return false;
        }

        var path = relative!.Replace('\\', '/').Trim('/');
        if (IsNamedRootClientFile(manifest, path))
        {
            return true;
        }

        foreach (var folder in ClientFolders(manifest))
        {
            if (path.StartsWith(folder + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsResolvedInsideClient(PluginManifest manifest, string pluginDir, string full)
    {
        var destination = Path.GetFullPath(full);
        if (IsResolvedRootEntry(manifest, pluginDir, destination))
        {
            return true;
        }

        foreach (var folder in ClientFolders(manifest))
        {
            var root = Path.GetFullPath(Path.Combine(pluginDir, folder.Replace('/', Path.DirectorySeparatorChar)));
            if (IsInsideRoot(root, destination))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> ClientFolders(PluginManifest manifest)
    {
        var folders = new List<string>();
        AddClientFolder(folders, manifest.Client?.Html);
        AddClientFolder(folders, manifest.Client?.Js);
        if (folders.Count == 0 && !HasRootClientFile(manifest))
        {
            folders.Add("client");
        }

        return folders;
    }

    private static bool HasRootClientFile(PluginManifest manifest) =>
        IsRootClientFile(manifest.Client?.Html) || IsRootClientFile(manifest.Client?.Js);

    private static bool IsRootClientFile(string? entry) =>
        !string.IsNullOrEmpty(entry) && entry.IndexOf('/') < 0;

    private static bool IsNamedRootClientFile(PluginManifest manifest, string path) =>
        IsRootClientFile(path)
        && (string.Equals(path, manifest.Client?.Html, StringComparison.Ordinal)
            || string.Equals(path, manifest.Client?.Js, StringComparison.Ordinal));

    private static bool IsResolvedRootEntry(PluginManifest manifest, string pluginDir, string destination)
    {
        foreach (var entry in new[] { manifest.Client?.Html, manifest.Client?.Js })
        {
            if (entry == null || !IsRootClientFile(entry))
            {
                continue;
            }

            var expected = Path.GetFullPath(Path.Combine(pluginDir, entry));
            if (string.Equals(destination, expected, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddClientFolder(List<string> folders, string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return;
        }

        var relative = entry.Replace('\\', '/').Trim().TrimStart('/');
        var slash = relative.LastIndexOf('/');
        if (slash < 0)
        {
            return;
        }

        var folder = relative[..slash];
        if (!PluginManifest.IsAllowedClientFolder(folder) || folders.Contains(folder, StringComparer.Ordinal))
        {
            return;
        }

        folders.Add(folder);
    }

    private static bool IsRealFileInside(string root, string destination)
    {
        try
        {
            var rootFull = Path.GetFullPath(root);
            var full = Path.GetFullPath(destination);
            if (!IsInsideRoot(rootFull, full) || !System.IO.File.Exists(full))
            {
                return false;
            }

            if (System.IO.File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            var foundRoot = false;
            for (var current = new DirectoryInfo(Path.GetDirectoryName(full)!); current != null; current = current.Parent)
            {
                if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return false;
                }

                if (string.Equals(current.FullName, rootFull, StringComparison.Ordinal))
                {
                    foundRoot = true;
                    break;
                }
            }

            return foundRoot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsInsideRoot(string root, string destination)
    {
        var rootSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return destination.StartsWith(rootSep, StringComparison.Ordinal);
    }

    private bool TryActive(string id, out IActionResult? error)
    {
        var status = _registry.Find(id);
        if (status == null || !status.Enabled)
        {
            error = NotFound(new { error = "Unknown plugin." });
            return false;
        }

        if (!PluginRouteAccess.CanUseData(status.Enabled, status.Active))
        {
            error = Conflict(new { error = "Plugin is dormant.", dormantReason = status.DormantReason });
            return false;
        }

        error = null;
        return true;
    }

    private async Task<string?> ReadBody(CancellationToken cancellationToken)
    {
        if (Request.ContentLength.GetValueOrDefault() > MaxDataBytes)
        {
            return null;
        }

        using var reader = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        var buffer = new char[MaxDataBytes + 1];
        var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (read > MaxDataBytes)
        {
            return null;
        }

        return new string(buffer, 0, read);
    }

    private static bool TrySafeFile(string pluginDir, string? relative, out string full)
    {
        full = string.Empty;
        if (!TryPublicRelative(relative))
        {
            return false;
        }

        var path = relative!.Replace('\\', '/').TrimStart('/');
        if (path.Length == 0
            || path.Contains("..", StringComparison.Ordinal)
            || path.Contains(':', StringComparison.Ordinal)
            || path.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        if (PluginManifest.IsBlockedClientFileName(name))
        {
            return false;
        }

        if (path.StartsWith("data/", StringComparison.OrdinalIgnoreCase) || path.Equals("data", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("server/", StringComparison.OrdinalIgnoreCase) || path.Equals("server", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var root = Path.GetFullPath(pluginDir);
        var destination = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInsideRoot(root, destination))
        {
            return false;
        }

        full = destination;
        return true;
    }

    private static PluginListItem ToItem(PluginCapabilityResolver.PluginStatus status) =>
        new()
        {
            Id = status.Manifest.Id,
            Name = status.Manifest.Name,
            Version = status.Manifest.Version,
            Description = status.Manifest.Description,
            Enabled = status.Enabled,
            Active = status.Active,
            DormantReason = status.DormantReason,
            Provides = status.Manifest.Provides,
            Requires = status.Manifest.Requires,
            Slots = status.Manifest.Slots,
            ClientEntryUrl = status.Manifest.ClientEntryUrl(),
            ProxyAccess = status.Manifest.ProxyAccess,
        };
}

public sealed class PluginListResponse
{
    [JsonPropertyName("hostApi")]
    public int HostApi { get; set; }

    [JsonPropertyName("plugins")]
    public List<PluginListItem> Plugins { get; set; } = new();

    [JsonPropertyName("capabilities")]
    public Dictionary<string, bool> Capabilities { get; set; } = new();
}

public sealed class PluginListItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("dormantReason")]
    public string? DormantReason { get; set; }

    [JsonPropertyName("provides")]
    public IReadOnlyList<string> Provides { get; set; } = Array.Empty<string>();

    [JsonPropertyName("requires")]
    public IReadOnlyList<string> Requires { get; set; } = Array.Empty<string>();

    [JsonPropertyName("slots")]
    public IReadOnlyList<string> Slots { get; set; } = Array.Empty<string>();

    [JsonPropertyName("clientEntryUrl")]
    public string? ClientEntryUrl { get; set; }

    [JsonPropertyName("proxyAccess")]
    public string ProxyAccess { get; set; } = "admin";
}

/// <summary>
/// Kestrel collapses <c>..</c> and <c>%2e</c> before routing, which would turn a files request
/// into the authorized catch-all. Reject those raw targets before authentication runs.
/// </summary>
public sealed class PluginClientFileGuard : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use(RejectTraversal);
            next(app);
        };

    internal static async Task RejectTraversal(HttpContext context, RequestDelegate next)
    {
        if (IsRawFileTraversal(context.Features.Get<IHttpRequestFeature>()?.RawTarget))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"error\":\"File not found.\"}", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    internal static bool IsRawFileTraversal(string? rawTarget)
    {
        if (string.IsNullOrEmpty(rawTarget))
        {
            return false;
        }

        var path = rawTarget;
        var query = path.IndexOf('?');
        if (query >= 0)
        {
            path = path[..query];
        }

        var hash = path.IndexOf('#');
        if (hash >= 0)
        {
            path = path[..hash];
        }

        if (!path.Contains("/Moonfin/Plugins/", StringComparison.OrdinalIgnoreCase)
            || !path.Contains("/files/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Contains("..", StringComparison.Ordinal)
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains("%2e", StringComparison.OrdinalIgnoreCase)
            || path.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || path.Contains("%5c", StringComparison.OrdinalIgnoreCase)
            || path.Contains("%252e", StringComparison.OrdinalIgnoreCase)
            || RawSegmentEndsWithDotOrSpace(path);
    }

    private static bool RawSegmentEndsWithDotOrSpace(string path)
    {
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            if (segment[^1] == '.'
                || segment[^1] == ' '
                || segment.EndsWith("%20", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Data and proxy are only for an active plugin. Proxy defaults to administrators.</summary>
public static class PluginRouteAccess
{
    public static bool CanUseData(bool enabled, bool active) => enabled && active;

    public static bool CanProxy(bool enabled, bool active, string? proxyAccess, bool isAdmin)
    {
        if (!CanUseData(enabled, active))
        {
            return false;
        }

        return isAdmin || string.Equals(proxyAccess, "user", StringComparison.Ordinal);
    }
}
