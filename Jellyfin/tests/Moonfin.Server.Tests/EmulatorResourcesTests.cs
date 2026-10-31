using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Api;
using Xunit;

namespace Moonfin.Server.Tests;

public class EmulatorResourcesTests
{
    private static readonly Assembly ServerAssembly = typeof(Moonfin.Server.Api.EmulatorController).Assembly;

    [Fact]
    public void PlayerShell_LoadsEmbeddedBridgeBeforeTheEmulatorLoader()
    {
        var player = ReadResource("Moonfin.Server.EmulatorJS.player.html");

        Assert.Contains("<script src=\"moonfin-bridge.js\"></script>", player, StringComparison.Ordinal);
        Assert.Contains("params.get('bios')", player, StringComparison.Ordinal);
        Assert.Contains("window.EJS_biosUrl = bios", player, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(player, "window.EJS_biosUrl = bios"));
        Assert.True(
            player.IndexOf("moonfin-bridge.js", StringComparison.Ordinal) < player.IndexOf("loader.js", StringComparison.Ordinal));
    }

    [Fact]
    public void PlayerShell_ArcadeBiosIsAnExternalZipBeforeTheLoader()
    {
        var player = ReadResource("Moonfin.Server.EmulatorJS.player.html");

        Assert.Contains("case 'arcade':", player, StringComparison.Ordinal);
        Assert.Contains("case 'mame':", player, StringComparison.Ordinal);
        Assert.Contains("case 'fbneo':", player, StringComparison.Ordinal);
        Assert.Contains("case 'mame2003':", player, StringComparison.Ordinal);
        Assert.Contains("case 'mame2003_plus':", player, StringComparison.Ordinal);
        Assert.Contains("params.get('biosName')", player, StringComparison.Ordinal);
        Assert.Contains("params.get('parent')", player, StringComparison.Ordinal);
        Assert.Contains("method: 'HEAD'", player, StringComparison.Ordinal);
        Assert.Contains("credentials: 'same-origin'", player, StringComparison.Ordinal);
        Assert.Contains("response.headers.get('Content-Disposition')", player, StringComparison.Ordinal);
        Assert.Contains("files['/' + biosFileName] = bios", player, StringComparison.Ordinal);
        Assert.Contains("files['/' + parentFileName] = parent", player, StringComparison.Ordinal);
        Assert.Contains("window.EJS_externalFiles = files", player, StringComparison.Ordinal);
        Assert.Contains("window.moonfinBeforeLoader", player, StringComparison.Ordinal);
        Assert.Contains("Object.defineProperty(window, 'EJS_GameManager'", player, StringComparison.Ordinal);
        Assert.Contains("new Uint8Array(", player, StringComparison.Ordinal);
        Assert.Contains("Could not download ", player, StringComparison.Ordinal);
        Assert.Contains("window.EJS_CacheLimit = 0", player, StringComparison.Ordinal);
        Assert.Contains("autosave_interval = 0", player, StringComparison.Ordinal);
        Assert.Contains("audio_latency = 32", player, StringComparison.Ordinal);
        Assert.Contains("rewind_enable = false", player, StringComparison.Ordinal);
        Assert.Contains("'save-save-interval': '0'", player, StringComparison.Ordinal);
        Assert.Contains("video_shader_enable = false", player, StringComparison.Ordinal);
        Assert.Contains("run_ahead_enabled = false", player, StringComparison.Ordinal);
        Assert.Contains("performance.memory", player, StringComparison.Ordinal);
        Assert.Contains("HEAPU8", player, StringComparison.Ordinal);
        Assert.Contains("params.get('debug')", player, StringComparison.Ordinal);
        Assert.Contains("moonfinDownloadBuffers", player, StringComparison.Ordinal);
        Assert.Contains("buffer.transfer(0)", player, StringComparison.Ordinal);
        Assert.Contains("if (ok === false) { return; }", player, StringComparison.Ordinal);
        Assert.True(
            player.IndexOf("moonfinBeforeLoader", StringComparison.Ordinal) < player.IndexOf("loader.js", StringComparison.Ordinal));
        Assert.DoesNotContain("EJS_gameParentUrl", player, StringComparison.Ordinal);
    }

    [Fact]
    public void MoonfinBridge_IsEmbeddedWithTheNativeHostEntryPoints()
    {
        var bridge = ReadResource("Moonfin.Server.EmulatorJS.moonfin-bridge.js");

        foreach (var entryPoint in new[]
        {
            "moonfinRegisterGamepads", "moonfinGamepadInput", "moonfinKeyboardInput", "moonfinControlInput",
            "moonfinGetState", "moonfinLoadState", "moonfinRestart", "moonfinFastForward", "moonfinPause",
            "moonfinGetSettings", "moonfinGetOptions", "moonfinSetOption", "moonfinOpenControls", "moonfinControlsOpen"
        })
        {
            Assert.Contains(entryPoint, bridge, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MoonfinBridge_UsesTheAnonymousEmbeddedAssetEndpoint()
    {
        var method = typeof(EmulatorController).GetMethod(nameof(EmulatorController.GetMoonfinBridge));
        Assert.NotNull(method);
        var route = Assert.IsType<HttpGetAttribute>(method!.GetCustomAttribute<HttpGetAttribute>());
        Assert.Equal("moonfin-bridge.js", route.Template);
        Assert.NotNull(method.GetCustomAttribute<AllowAnonymousAttribute>());

        var result = new EmulatorController().GetMoonfinBridge();
        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/javascript", content.ContentType);
        Assert.Contains("moonfinRegisterGamepads", content.Content, StringComparison.Ordinal);
    }

    private static string ReadResource(string name)
    {
        using var stream = ServerAssembly.GetManifestResourceStream(name);
        if (stream == null)
        {
            throw new InvalidOperationException($"Embedded resource '{name}' was not found.");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
