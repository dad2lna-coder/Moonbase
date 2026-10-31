using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Plugins;

public sealed class PluginZipLimits
{
    public long MaxZipBytes { get; init; } = 32 * 1024 * 1024;

    public long MaxEntryBytes { get; init; } = 8 * 1024 * 1024;

    public long MaxTotalBytes { get; init; } = 64 * 1024 * 1024;

    public int MaxEntries { get; init; } = 400;
}

public sealed class PluginInstallResult
{
    public bool Ok { get; init; }

    public string? Error { get; init; }

    public string? Id { get; init; }

    public bool RestartRequired { get; init; }

    public static PluginInstallResult Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>
/// Unpacks a plugin zip into <c>{data}/moonfin-plugins/{id}</c>. Rejects the whole archive
/// when any entry escapes the destination. Updates keep config.json and data/.
/// Server assemblies from the zip (<c>server/*.dll</c>) are stored as <c>server/*.dll.mbmod</c>.
/// </summary>
public static class PluginZipInstaller
{
    public static bool EntryIsSafe(string? entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            return false;
        }

        if (entryName.Contains('\0', StringComparison.Ordinal) || entryName.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var name = entryName.Replace('\\', '/');
        if (name.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        if (name.Length >= 2 && char.IsLetter(name[0]) && name[1] == ':')
        {
            return false;
        }

        foreach (var part in name.Split('/'))
        {
            if (part == "..")
            {
                return false;
            }
        }

        return true;
    }

    public static async Task<PluginInstallResult> InstallAsync(
        Stream zip,
        string pluginsRoot,
        PluginZipLimits? limits = null,
        CancellationToken cancellationToken = default,
        ILogger? logger = null)
    {
        limits ??= new PluginZipLimits();
        if (zip == null)
        {
            return PluginInstallResult.Fail("No zip was uploaded.");
        }

        try
        {
            if (!zip.CanSeek)
            {
                var buffered = new MemoryStream();
                var limited = new BoundedReadStream(zip, limits.MaxZipBytes);
                await limited.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
                if (limited.Exceeded)
                {
                    return PluginInstallResult.Fail("Zip is too large.");
                }

                buffered.Position = 0;
                zip = buffered;
            }

            return Install(zip, pluginsRoot, limits, logger);
        }
        catch (Exception)
        {
            return PluginInstallResult.Fail("The plugin could not be installed.");
        }
    }

    public static PluginInstallResult Install(Stream zip, string pluginsRoot, PluginZipLimits? limits = null, ILogger? logger = null)
    {
        limits ??= new PluginZipLimits();
        if (zip == null)
        {
            return PluginInstallResult.Fail("No zip was uploaded.");
        }

        if (!zip.CanSeek)
        {
            var buffered = new MemoryStream();
            var copied = CopyBounded(zip, buffered, limits.MaxZipBytes + 1);
            if (copied > limits.MaxZipBytes)
            {
                return PluginInstallResult.Fail("Zip is too large.");
            }

            buffered.Position = 0;
            zip = buffered;
        }
        else if (zip.Length > limits.MaxZipBytes)
        {
            return PluginInstallResult.Fail("Zip is too large.");
        }

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            return PluginInstallResult.Fail("The upload is not a zip archive.");
        }

        using (archive)
        {
            if (archive.Entries.Count == 0)
            {
                return PluginInstallResult.Fail("The zip is empty.");
            }

            if (archive.Entries.Count > limits.MaxEntries)
            {
                return PluginInstallResult.Fail("The zip has too many files.");
            }

            long declared = 0;
            foreach (var entry in archive.Entries)
            {
                if (!EntryIsSafe(entry.FullName))
                {
                    return PluginInstallResult.Fail("The zip contains an unsafe path.");
                }

                if (entry.Length < 0 || entry.Length > limits.MaxEntryBytes)
                {
                    return PluginInstallResult.Fail("A file in the zip is too large.");
                }

                declared += entry.Length;
                if (declared > limits.MaxTotalBytes)
                {
                    return PluginInstallResult.Fail("The unzipped plugin is too large.");
                }
            }

            var prefix = FindPrefix(archive.Entries);
            if (prefix == null)
            {
                return PluginInstallResult.Fail("The zip must contain plugin.json.");
            }

            var staging = Path.Combine(pluginsRoot, ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                var rootFull = Path.GetFullPath(staging);
                var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
                long written = 0;
                foreach (var entry in archive.Entries)
                {
                    var key = entry.FullName.Replace('\\', '/');
                    if (!key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return FailCleanup(staging, "The zip contains an unexpected path.");
                    }

                    var relative = key[prefix.Length..];
                    if (relative.Length == 0 || relative.EndsWith("/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!EntryIsSafe(relative))
                    {
                        return FailCleanup(staging, "The zip contains an unsafe path.");
                    }

                    var destination = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!destination.StartsWith(rootWithSep, StringComparison.Ordinal))
                    {
                        return FailCleanup(staging, "The zip contains an unsafe path.");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var input = entry.Open();
                    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var count = CopyBounded(input, output, limits.MaxEntryBytes + 1);
                    if (count > limits.MaxEntryBytes)
                    {
                        return FailCleanup(staging, "A file in the zip is too large.");
                    }

                    written += count;
                    if (written > limits.MaxTotalBytes)
                    {
                        return FailCleanup(staging, "The unzipped plugin is too large.");
                    }
                }

                var manifestPath = Directory.EnumerateFiles(staging)
                    .FirstOrDefault(file => Path.GetFileName(file).Equals("plugin.json", StringComparison.OrdinalIgnoreCase));
                if (manifestPath == null)
                {
                    return FailCleanup(staging, "The zip must contain plugin.json.");
                }

                var canonicalManifest = Path.Combine(staging, "plugin.json");
                if (!string.Equals(manifestPath, canonicalManifest, StringComparison.Ordinal))
                {
                    File.Move(manifestPath, canonicalManifest, overwrite: true);
                    manifestPath = canonicalManifest;
                }

                var manifest = PluginManifest.TryParse(File.ReadAllText(manifestPath), out var error);
                if (manifest == null)
                {
                    return FailCleanup(staging, error ?? "plugin.json is invalid.");
                }

                Directory.CreateDirectory(pluginsRoot);
                var destinationDir = Path.Combine(pluginsRoot, manifest.Id);
                var destinationFull = Path.GetFullPath(destinationDir);
                var root = Path.GetFullPath(pluginsRoot);
                var rootSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
                if (!destinationFull.StartsWith(rootSep, StringComparison.Ordinal))
                {
                    return FailCleanup(staging, "Plugin id is not a safe folder name.");
                }

                // Rename before the folder is moved into place so a new install never
                // lands a .dll where Jellyfin's plugin scan will pick it up.
                ModuleAssemblyStore.HideServerAssemblies(
                    staging,
                    logger == null ? null : message => logger.LogWarning("Moonbase plugin: {Note}", message));

                if (Directory.Exists(destinationFull))
                {
                    PreserveUserData(destinationFull, staging);
                    var backup = destinationFull + ".replacing-" + Guid.NewGuid().ToString("N");
                    Directory.Move(destinationFull, backup);
                    try
                    {
                        Directory.Move(staging, destinationFull);
                        TryDelete(backup);
                    }
                    catch
                    {
                        if (!Directory.Exists(destinationFull) && Directory.Exists(backup))
                        {
                            Directory.Move(backup, destinationFull);
                        }

                        TryDelete(staging);
                        return PluginInstallResult.Fail("The existing plugin could not be replaced. Restart Jellyfin and try again.");
                    }
                }
                else
                {
                    Directory.Move(staging, destinationFull);
                }

                return new PluginInstallResult
                {
                    Ok = true,
                    Id = manifest.Id,
                    RestartRequired = !string.IsNullOrEmpty(manifest.Server),
                };
            }
            catch (Exception)
            {
                TryDelete(staging);
                return PluginInstallResult.Fail("The plugin could not be installed.");
            }
        }
    }

    private static string? FindPrefix(IReadOnlyCollection<ZipArchiveEntry> entries)
    {
        var names = entries
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .Where(name => name.Length > 0 && !name.EndsWith("/", StringComparison.Ordinal))
            .ToList();
        if (names.Any(name => name.Equals("plugin.json", StringComparison.OrdinalIgnoreCase)))
        {
            return string.Empty;
        }

        string? top = null;
        foreach (var name in names)
        {
            var slash = name.IndexOf('/');
            if (slash <= 0)
            {
                return null;
            }

            var first = name[..slash];
            if (top == null)
            {
                top = first;
            }
            else if (!string.Equals(top, first, StringComparison.Ordinal))
            {
                return null;
            }
        }

        if (top == null)
        {
            return null;
        }

        var candidate = top + "/plugin.json";
        return names.Any(name => name.Equals(candidate, StringComparison.OrdinalIgnoreCase)) ? top + "/" : null;
    }

    private static void PreserveUserData(string previous, string staging)
    {
        var config = Path.Combine(previous, "config.json");
        if (File.Exists(config))
        {
            File.Copy(config, Path.Combine(staging, "config.json"), overwrite: true);
        }

        var data = Path.Combine(previous, "data");
        if (!Directory.Exists(data))
        {
            return;
        }

        CopyDirectory(data, Path.Combine(staging, "data"));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private static long CopyBounded(Stream input, Stream output, long maxInclusive)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maxInclusive)
            {
                return total;
            }

            output.Write(buffer, 0, read);
        }

        return total;
    }

    private static PluginInstallResult FailCleanup(string staging, string error)
    {
        TryDelete(staging);
        return PluginInstallResult.Fail(error);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // The caller surfaces a restart hint when a locked file survives.
        }
    }

    /// <summary>Stops the async copy once the upload passes the zip size cap.</summary>
    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _max;
        private long _total;

        public BoundedReadStream(Stream inner, long max)
        {
            _inner = inner;
            _max = max;
        }

        public bool Exceeded { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Exceeded)
            {
                return 0;
            }

            var read = _inner.Read(buffer, offset, count);
            return Count(read);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Exceeded)
            {
                return 0;
            }

            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return Count(read);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            if (read <= 0)
            {
                return 0;
            }

            _total += read;
            if (_total > _max)
            {
                Exceeded = true;
                return 0;
            }

            return read;
        }
    }
}
