using Moonfin.Server.Api;

namespace Moonfin.Server.Plugins;

/// <summary>
/// Writes plugin capability flags onto ping. Does not touch any other ping field.
/// </summary>
public static class PluginPingMerge
{
    public static void Apply(MoonfinPingResponse ping, IReadOnlyDictionary<string, bool> flags)
    {
        ping.Plugins = new Dictionary<string, bool>(flags, StringComparer.Ordinal);
    }
}
