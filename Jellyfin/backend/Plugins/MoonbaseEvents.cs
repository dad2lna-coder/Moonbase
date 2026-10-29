using System.Text.Json;
using Moonfin.Server.Services;

namespace Moonfin.Server.Plugins;

public interface IMoonbaseEvents
{
    /// <summary>
    /// Pushes an event onto the existing <c>/Moonfin/Settings/Stream</c> channel.
    /// Built-in types are refused. The wire type is <c>plugin:{id}:{type}</c>.
    /// A target user receives the event; a null user broadcasts it.
    /// </summary>
    void Emit(string pluginId, string type, object? payload = null, Guid? userId = null);
}

public static class MoonbaseEventTypes
{
    private static readonly HashSet<string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        "settingsUpdated",
        "themesChanged",
        "messagesChanged",
    };

    public static bool IsBuiltIn(string? type) =>
        !string.IsNullOrWhiteSpace(type) && BuiltIn.Contains(type.Trim());

    /// <summary>Returns <c>plugin:{id}:{type}</c>, or null when the plugin must not emit this event.</summary>
    public static string? WireType(string? pluginId, string? type)
    {
        if (!PluginManifest.IsValidId(pluginId) || string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        var trimmed = type.Trim();
        if (IsBuiltIn(trimmed) || trimmed.IndexOfAny([':', '\r', '\n']) >= 0)
        {
            return null;
        }

        return "plugin:" + pluginId + ":" + trimmed;
    }
}

public sealed class MoonbaseEvents : IMoonbaseEvents
{
    private readonly MoonfinSettingsService _settings;

    public MoonbaseEvents(MoonfinSettingsService settings)
    {
        _settings = settings;
    }

    public void Emit(string pluginId, string type, object? payload = null, Guid? userId = null)
    {
        var wire = MoonbaseEventTypes.WireType(pluginId, type);
        if (wire == null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(new
        {
            type = wire,
            pluginId,
            payload,
            at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        _settings.BroadcastPluginEvent(json, userId);
    }
}