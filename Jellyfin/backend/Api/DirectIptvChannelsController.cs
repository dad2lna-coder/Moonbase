using System.Globalization;
using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Dispatcharr channel feeds for Direct IPTV. Authentication is the signed-in
/// Jellyfin session. Live TV management is the permission. The Dispatcharr API
/// key stays on the server and is never returned.
/// </summary>
[ApiController]
[Route("Moonfin/LiveTv/Direct")]
[Authorize(Policy = "LiveTvManagement")]
[Produces(MediaTypeNames.Application.Json)]
public class DirectIptvChannelsController : ControllerBase
{
    private readonly DispatcharrClient _dispatcharr;

    public DirectIptvChannelsController(DispatcharrClient dispatcharr)
    {
        _dispatcharr = dispatcharr;
    }

    [HttpGet("Channels/{dispatcharrChannelId}/Feeds")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<DirectChannelFeed>>> Feeds(
        [FromRoute] string? dispatcharrChannelId,
        CancellationToken cancellationToken)
    {
        var result = await _dispatcharr.GetFeedsAsync(dispatcharrChannelId ?? "", cancellationToken).ConfigureAwait(false);
        return FromCall(result);
    }

    [HttpPost("Channels/{id}/Feeds/Primary")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> SetPrimary(
        [FromRoute] string? id,
        [FromBody] PrimaryFeedBody? body,
        CancellationToken cancellationToken)
    {
        if (!TryStreamId(body, out var streamId))
        {
            return BadRequest("streamId is required.");
        }

        var result = await _dispatcharr.SetPrimaryAsync(id ?? "", streamId, cancellationToken).ConfigureAwait(false);
        if (result.Status == DispatcharrCallStatus.Ok)
        {
            return NoContent();
        }

        return FromCall(result);
    }

    /// <summary>Maps a lineup row to the Dispatcharr channel id.</summary>
    [HttpGet("Channels/Lookup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DirectChannelLookup>> Lookup(
        [FromQuery] string? uuid,
        [FromQuery] string? number,
        [FromQuery] string? tvgId,
        CancellationToken cancellationToken)
    {
        var result = await _dispatcharr.LookupAsync(uuid, number, tvgId, cancellationToken).ConfigureAwait(false);
        if (result.Status == DispatcharrCallStatus.Ok && result.Value is long id)
        {
            return Ok(new DirectChannelLookup { Id = id });
        }

        return FromCall(result);
    }

    private ActionResult FromCall<T>(DispatcharrCall<T> result)
    {
        if (result.Status == DispatcharrCallStatus.Ok)
        {
            return Ok(result.Value);
        }

        var error = result.Error ?? "Dispatcharr could not complete the request.";
        return result.Status switch
        {
            DispatcharrCallStatus.BadRequest => BadRequest(error),
            DispatcharrCallStatus.NotFound => NotFound(error),
            DispatcharrCallStatus.NotConfigured => StatusCode(StatusCodes.Status503ServiceUnavailable, error),
            _ => StatusCode(StatusCodes.Status502BadGateway, error),
        };
    }

    private static bool TryStreamId(PrimaryFeedBody? body, out string streamId)
    {
        streamId = "";
        if (body == null)
        {
            return false;
        }

        var value = body.StreamId;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number > 0)
        {
            streamId = number.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            streamId = parsed.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }
}

public sealed class PrimaryFeedBody
{
    [JsonPropertyName("streamId")]
    public JsonElement StreamId { get; set; }
}

public sealed class DirectChannelLookup
{
    [JsonPropertyName("id")]
    public long Id { get; init; }
}
