using System.Net.Mime;
using System.Security.Claims;
using System.Threading;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LiveNow.Api;

/// <summary>
/// API surface for the Live Now plugin. All endpoints require a normal
/// authenticated Jellyfin user (reuses Jellyfin's auth pipeline).
/// </summary>
[ApiController]
[Authorize]
[Route("LiveNow")]
[Produces(MediaTypeNames.Application.Json)]
public class LiveNowController : ControllerBase
{
    private readonly ISessionManager _sessionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveNowController"/> class.
    /// </summary>
    /// <param name="sessionManager">Injected session manager.</param>
    public LiveNowController(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// Returns the plugin settings the Live Now web page needs at load time
    /// (currently just the page refresh interval).
    /// </summary>
    /// <returns>Plugin settings the web page needs.</returns>
    [HttpGet("Config")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<LiveNowConfigDto> GetConfig()
    {
        var cfg = Plugin.Instance?.Configuration;
        var seconds = cfg?.RefreshSeconds ?? 15;
        if (seconds < 5)
        {
            seconds = 5;
        }

        return Ok(new LiveNowConfigDto { RefreshSeconds = seconds });
    }

    /// <summary>
    /// Returns the set of Live TV channels that are currently "warm".
    /// </summary>
    /// <returns>The list of warm channels, sorted by viewer count descending.</returns>
    [HttpGet("Channels")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<WarmChannelDto>> GetWarmChannels()
    {
        // Group active live-TV sessions by channel. For a live-TV NowPlayingItem
        // the BaseItemDto *is* the channel: its Id is the channel id and its Name
        // is the channel name (ChannelId/ChannelName come back null for live TV).
        var groups = new Dictionary<Guid, WarmChannelAccumulator>();

        foreach (var session in _sessionManager.Sessions)
        {
            var item = session.NowPlayingItem;
            if (item is null || item.Type != BaseItemKind.TvChannel)
            {
                continue;
            }

            var channelId = item.Id;
            if (channelId == Guid.Empty)
            {
                continue;
            }

            if (!groups.TryGetValue(channelId, out var acc))
            {
                acc = new WarmChannelAccumulator
                {
                    ChannelId = channelId,
                    ChannelName = item.Name ?? item.ChannelName ?? "Unknown channel",
                    CurrentProgramName = item.CurrentProgram?.Name,
                    ImageUrl = BuildPrimaryImageUrl(item),
                };
                groups[channelId] = acc;
            }

            acc.ViewerCount++;
        }

        var result = groups.Values
            .Select(a => new WarmChannelDto
            {
                ChannelId = a.ChannelId.ToString("N"),
                ChannelName = a.ChannelName,
                CurrentProgramName = a.CurrentProgramName,
                ViewerCount = a.ViewerCount,
                ImageUrl = a.ImageUrl,
            })
            .OrderByDescending(c => c.ViewerCount)
            .ThenBy(c => c.ChannelName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(result);
    }

    /// <summary>
    /// Tune the calling user's own client session into a channel by issuing a
    /// PlayNow command server-side. The session is resolved from the
    /// authenticated user plus the supplied device id, and we verify it belongs
    /// to the caller — so a user can only ever start playback on their own
    /// session. Returns 200 on success, 404 if no matching live session exists
    /// (the page then falls back to the channel detail deep-link).
    /// </summary>
    /// <param name="channelId">The live TV channel id to play.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the tune attempt.</returns>
    [HttpPost("Tune")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Tune(
        [FromQuery] Guid channelId,
        CancellationToken cancellationToken)
    {
        if (channelId == Guid.Empty)
        {
            return BadRequest("channelId is required.");
        }

        var userId = GetCallerUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        // The auth pipeline already tells us which device made this request, so
        // we target exactly the caller's own session — a user can never play to
        // someone else's. Fall back to any of the caller's remote-controllable
        // sessions if the device claim is missing.
        var deviceId = User.FindFirstValue("Jellyfin-DeviceId");

        var session = _sessionManager.Sessions
            .Where(s => s.UserId == userId && s.SupportsRemoteControl)
            .Where(s => string.IsNullOrEmpty(deviceId)
                || string.Equals(s.DeviceId, deviceId, StringComparison.Ordinal))
            .OrderByDescending(s => s.LastActivityDate)
            .FirstOrDefault();

        if (session is null)
        {
            // No live session to control — let the page fall back to the
            // channel detail deep-link.
            return NotFound("No active session to play on.");
        }

        var request = new PlayRequest
        {
            ItemIds = new[] { channelId },
            PlayCommand = PlayCommand.PlayNow,
            ControllingUserId = userId,
        };

        await _sessionManager
            .SendPlayCommand(session.Id, session.Id, request, cancellationToken)
            .ConfigureAwait(false);

        return Ok();
    }

    /// <summary>
    /// Resolves the authenticated caller's user id from the request claims.
    /// Jellyfin stores it in the "Jellyfin-UserId" claim as a GUID string.
    /// </summary>
    private Guid GetCallerUserId()
    {
        var raw = User.FindFirstValue("Jellyfin-UserId");
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }

    /// <summary>
    /// Builds a relative primary-image URL for the channel, if it has a logo.
    /// Relative so it works behind the /jellyfin base path on any host.
    /// </summary>
    private static string? BuildPrimaryImageUrl(BaseItemDto item)
    {
        if (item.ImageTags is not null
            && item.ImageTags.TryGetValue(ImageType.Primary, out var tag)
            && !string.IsNullOrEmpty(tag))
        {
            return $"Items/{item.Id:N}/Images/Primary?tag={tag}";
        }

        return null;
    }

    private sealed class WarmChannelAccumulator
    {
        public Guid ChannelId { get; set; }

        public string ChannelName { get; set; } = string.Empty;

        public string? CurrentProgramName { get; set; }

        public string? ImageUrl { get; set; }

        public int ViewerCount { get; set; }
    }
}

/// <summary>
/// Settings the Live Now web page reads at load time.
/// </summary>
public class LiveNowConfigDto
{
    /// <summary>Gets or sets how often (seconds) the page should refresh.</summary>
    public int RefreshSeconds { get; set; } = 15;
}

/// <summary>
/// A Live TV channel that is currently being watched.
/// </summary>
public class WarmChannelDto
{
    /// <summary>Gets or sets the channel id (32-char "N" GUID).</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel display name.</summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>Gets or sets the name of the program currently airing, if known.</summary>
    public string? CurrentProgramName { get; set; }

    /// <summary>Gets or sets the number of active viewers on this channel.</summary>
    public int ViewerCount { get; set; }

    /// <summary>Gets or sets a relative primary-image URL for the channel logo, if any.</summary>
    public string? ImageUrl { get; set; }
}
