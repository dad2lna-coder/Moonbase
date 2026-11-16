using System.Net.Mime;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Direct IPTV (M3U) recordings. Authentication is the signed-in Jellyfin
/// session. Live TV management is the recording permission. No API key is
/// accepted or stored.
/// </summary>
[ApiController]
[Route("Moonfin/LiveTv/Direct")]
[Authorize(Policy = "LiveTvManagement")]
[Produces(MediaTypeNames.Application.Json)]
public class DirectIptvRecordingsController : ControllerBase
{
    private readonly DirectIptvRecordingService _service;

    public DirectIptvRecordingsController(DirectIptvRecordingService service)
    {
        _service = service;
    }

    [HttpGet("Recordings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<DirectRecordingListResponse> List() => Ok(_service.List());

    [HttpPost("Recordings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<DirectRecordingView> Schedule([FromBody] DirectRecordingBody? body)
    {
        if (body == null
            || body.Start is not DateTimeOffset start
            || body.End is not DateTimeOffset end
            || end <= start
            || string.IsNullOrWhiteSpace(body.TvgId))
        {
            return BadRequest("tvgId, start, and end are required.");
        }

        return Ok(_service.Schedule(body.TvgId!.Trim(), body.Title, start, end, body.StreamUrl, body.RecUrl));
    }

    [HttpDelete("Recordings/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Cancel([FromRoute] string? id)
    {
        var decoded = Uri.UnescapeDataString(id ?? "");
        if (decoded.Length == 0)
        {
            return NotFound();
        }

        return _service.Cancel(decoded) ? NoContent() : NotFound();
    }
}

public sealed class DirectRecordingBody
{
    [JsonPropertyName("tvgId")]
    public string? TvgId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("start")]
    public DateTimeOffset? Start { get; set; }

    [JsonPropertyName("end")]
    public DateTimeOffset? End { get; set; }

    [JsonPropertyName("streamUrl")]
    public string? StreamUrl { get; set; }

    [JsonPropertyName("recUrl")]
    public string? RecUrl { get; set; }
}
