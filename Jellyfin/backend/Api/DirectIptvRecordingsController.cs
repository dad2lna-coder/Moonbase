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

    /// <summary>
    /// Lists jobs. itemId is resolved from the saved path on every call until Jellyfin has the file.
    /// </summary>
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

        return Ok(_service.Schedule(
            body.TvgId!.Trim(),
            body.Title,
            start,
            end,
            body.StreamUrl,
            body.RecUrl,
            DirectRecordingPrograms.ProgramOf(body, start)));
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

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("series")]
    public string? Series { get; set; }

    [JsonPropertyName("seriesName")]
    public string? SeriesName { get; set; }

    [JsonPropertyName("episodeTitle")]
    public string? EpisodeTitle { get; set; }

    [JsonPropertyName("season")]
    public int? Season { get; set; }

    [JsonPropertyName("seasonNumber")]
    public int? SeasonNumber { get; set; }

    [JsonPropertyName("episode")]
    public int? Episode { get; set; }

    [JsonPropertyName("episodeNumber")]
    public int? EpisodeNumber { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("productionYear")]
    public int? ProductionYear { get; set; }

    [JsonPropertyName("isMovie")]
    public bool? IsMovie { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("categories")]
    public List<string>? Categories { get; set; }

    [JsonPropertyName("originalAirDate")]
    public DateTime? OriginalAirDate { get; set; }

    [JsonPropertyName("premiereDate")]
    public DateTime? PremiereDate { get; set; }

    [JsonPropertyName("program")]
    public DirectRecordingBody? Program { get; set; }
}

internal static class DirectRecordingPrograms
{
    public static DirectRecordingProgram ProgramOf(DirectRecordingBody body, DateTimeOffset start)
    {
        var nested = body.Program;
        var suppliedAirDate = body.OriginalAirDate ?? body.PremiereDate ?? nested?.OriginalAirDate ?? nested?.PremiereDate;
        return new DirectRecordingProgram
        {
            Title = First(body.Title, body.Name, nested?.Title, nested?.Name, body.Series, body.SeriesName),
            Series = First(body.Series, body.SeriesName, nested?.Series, nested?.SeriesName, body.Title, nested?.Title),
            EpisodeTitle = First(body.EpisodeTitle, nested?.EpisodeTitle),
            Season = body.Season ?? body.SeasonNumber ?? nested?.Season ?? nested?.SeasonNumber,
            Episode = body.Episode ?? body.EpisodeNumber ?? nested?.Episode ?? nested?.EpisodeNumber,
            Year = body.Year ?? body.ProductionYear ?? nested?.Year ?? nested?.ProductionYear,
            IsMovie = body.IsMovie ?? nested?.IsMovie,
            Category = CategoryOf(body) ?? CategoryOf(nested),
            // No air date on the request: the server's local date of the start,
            // not the UTC date, so an evening show keeps the evening's date.
            OriginalAirDate = suppliedAirDate ?? DirectRecordingFiles.AirDate(start, null, TimeZoneInfo.Local),
        };
    }

    private static string? CategoryOf(DirectRecordingBody? body)
    {
        if (body == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(body.Category))
        {
            return body.Category.Trim();
        }

        if (body.Categories == null)
        {
            return null;
        }

        var parts = body.Categories.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part.Trim()).ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string? First(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
