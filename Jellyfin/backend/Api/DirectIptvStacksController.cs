using System.Net.Mime;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Free TV stack members for a Direct IPTV channel. Any signed-in user can
/// list them and choose which one plays first. Live TV management is not required.
/// </summary>
[ApiController]
[Authorize]
[Route("Moonfin/LiveTv/Direct")]
[Produces(MediaTypeNames.Application.Json)]
public class DirectIptvStacksController : ControllerBase
{
    private readonly FreeTvClient _freeTv;

    public DirectIptvStacksController(FreeTvClient freeTv)
    {
        _freeTv = freeTv;
    }

    [HttpGet("Stacks/{slug}/Feeds")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<IReadOnlyList<DirectStackFeed>>> Feeds(
        [FromRoute] string? slug,
        CancellationToken cancellationToken)
    {
        if (this.GetUserIdFromClaims() == null)
        {
            return Unauthorized();
        }

        var result = await _freeTv.GetFeedsAsync(slug ?? "", cancellationToken).ConfigureAwait(false);
        if (result.Status == FreeTvCallStatus.Ok)
        {
            return Ok(result.Value);
        }

        return FromCall(result);
    }

    [HttpPut("Stacks/{slug}/Primary")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult> SetPrimary(
        [FromRoute] string? slug,
        [FromBody] PrimaryMemberBody? body,
        CancellationToken cancellationToken)
    {
        if (this.GetUserIdFromClaims() == null)
        {
            return Unauthorized();
        }

        var result = await _freeTv.SetPrimaryAsync(slug ?? "", body?.MemberId, cancellationToken).ConfigureAwait(false);
        if (result.Status == FreeTvCallStatus.Ok)
        {
            return NoContent();
        }

        return FromCall(result);
    }

    private ActionResult FromCall<T>(FreeTvCall<T> result)
    {
        var error = result.Error ?? "Free TV could not complete the request.";
        return result.Status switch
        {
            FreeTvCallStatus.BadRequest => BadRequest(error),
            FreeTvCallStatus.NotFound => NotFound(error),
            _ => StatusCode(StatusCodes.Status502BadGateway, error),
        };
    }
}

public sealed class PrimaryMemberBody
{
    [JsonPropertyName("memberId")]
    public string? MemberId { get; set; }
}
