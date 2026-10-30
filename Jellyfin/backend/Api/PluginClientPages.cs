using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Moonfin.Server.Api;

/// <summary>
/// Client-page policy and the script injected so a sandboxed web iframe can talk to MoFin.
/// </summary>
public static class PluginClientPages
{
    internal const string BridgeResourceName = "Moonfin.Server.Web.plugin-host-bridge.js";

    private static readonly Regex HeadOpenTag = new(
        "<head(?:\\s[^>]*)?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static readonly byte[] BridgeBytes;

    internal static readonly string BridgeVersion;

    internal static readonly string BridgeEtag;

    static PluginClientPages()
    {
        BridgeBytes = LoadBridge(out var version);
        BridgeVersion = version;
        BridgeEtag = "\"" + version + "\"";
    }

    /// <summary>
    /// Inserts <paramref name="scriptTag"/> once, immediately after the first head open tag.
    /// Pages with no head get the tag at the start. A header element is not a head.
    /// </summary>
    internal static string InjectBridge(string html, string scriptTag)
    {
        var match = HeadOpenTag.Match(html);
        if (!match.Success)
        {
            return scriptTag + html;
        }

        var index = match.Index + match.Length;
        return string.Concat(html.AsSpan(0, index), scriptTag, html.AsSpan(index));
    }

    internal static string ScriptTag(string? pathBase)
    {
        var prefix = pathBase ?? string.Empty;
        if (prefix.EndsWith('/'))
        {
            prefix = prefix.TrimEnd('/');
        }

        var src = prefix + "/Moonfin/PluginHost/bridge.js";
        return "<script src=\"" + WebUtility.HtmlEncode(src) + "\"></script>";
    }

    internal static string Policy(string? serverOrigin, IEnumerable<string>? frameAncestors)
    {
        var self = "'self'";
        if (PluginConfiguration.TryNormalizeOrigin(serverOrigin, out var origin))
        {
            self += " " + origin;
        }

        var frame = "'self'";
        var ancestors = PluginConfiguration.NormalizeFrameAncestors(frameAncestors);
        if (ancestors.Count > 0)
        {
            frame += " " + string.Join(' ', ancestors);
        }

        return "default-src " + self
            + "; script-src " + self
            + "; style-src " + self + " 'unsafe-inline'"
            + "; img-src " + self + " data: blob:"
            + "; connect-src " + self
            + "; frame-ancestors " + frame
            + "; object-src 'none'; base-uri 'self'";
    }

    private static byte[] LoadBridge(out string version)
    {
        using var stream = typeof(PluginClientPages).Assembly.GetManifestResourceStream(BridgeResourceName);
        if (stream == null)
        {
            version = "0";
            return Array.Empty<byte>();
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        version = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return bytes;
    }
}

/// <summary>Where plugin frame-ancestor origins come from. Tests supply their own.</summary>
internal interface IPluginFrameAncestorProvider
{
    IReadOnlyList<string>? Raw { get; }
}

internal sealed class PluginFrameAncestorProvider : IPluginFrameAncestorProvider
{
    public IReadOnlyList<string>? Raw => MoonfinPlugin.Instance?.Configuration?.PluginFrameAncestors;
}
