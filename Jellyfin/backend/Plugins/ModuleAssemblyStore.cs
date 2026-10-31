namespace Moonfin.Server.Plugins;

/// <summary>
/// Jellyfin scans every <c>*.dll</c> under the plugins directory. Server modules live in that
/// tree, so they are stored as <c>*.dll.mbmod</c> and any leftover <c>server/*.dll</c> is renamed
/// before load. The plugins folder itself stays put.
/// </summary>
internal static class ModuleAssemblyStore
{
    internal static void HideServerAssemblies(string pluginDir, Action<string>? note = null)
    {
        if (string.IsNullOrWhiteSpace(pluginDir))
        {
            return;
        }

        var serverDir = Path.Combine(pluginDir, "server");
        if (!Directory.Exists(serverDir))
        {
            return;
        }

        string[] files;
        try
        {
            // "*.dll" on Windows also matches "*.dll.mbmod" (3-character wildcard quirk).
            files = Directory.GetFiles(serverDir);
        }
        catch (Exception ex)
        {
            note?.Invoke(Path.GetFileName(pluginDir) + " server folder could not be listed (" + ex.GetType().Name + ").");
            return;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (!IsBareDll(name))
            {
                continue;
            }

            var dest = Path.Combine(serverDir, name + ".mbmod");
            try
            {
                if (File.Exists(dest))
                {
                    File.Delete(file);
                }
                else
                {
                    File.Move(file, dest);
                }
            }
            catch (Exception ex)
            {
                note?.Invoke(Path.GetFileName(pluginDir) + " could not hide " + name + " (" + ex.GetType().Name + ").");
            }
        }
    }

    internal static bool IsBareDll(string fileName) =>
        fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        && !fileName.EndsWith(MoonbasePlugins.ModuleFileSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>Foo.dll.mbmod</c> is the assembly file <c>Foo.dll</c>.</summary>
    internal static string LogicalAssemblyFileName(string fileName)
    {
        if (fileName.EndsWith(MoonbasePlugins.ModuleFileSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return fileName[..^".mbmod".Length];
        }

        return fileName;
    }
}
