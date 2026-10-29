using System.Text.Json;
using Moonfin.Server.Services;

namespace Moonfin.Server.Plugins;

public interface IMoonbaseEvents
{
    /// <summary>
    /// Pushes an event onto the existing <c>/Moonfin/Settings/Stream</c> channel.
    /// Allowed types: settingsUpdated, messagesChanged, pluginState, custom.
    /// </summary>
    void Emit(string pluginId, string type, object? payload = null);
}

public sealed class MoonbaseEvents : IMoonbaseEvents
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "settingsUpdated",
        "messagesChanged",
        "pluginState",
        "custom",
    };

    private readonly MoonfinSettingsService _settings;

    public MoonbaseEvents(MoonfinSettingsService settings)
    {
        _settings = settings;
    }

    public void Emit(string pluginId, string type, object? payload = null)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(type) || !Allowed.Contains(type))
        {
            return;
        }

        var json = JsonSerializer.Serialize(new
        {
            type,
            pluginId,
            payload,
            at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        _settings.BroadcastPluginEvent(json);
    }
}
