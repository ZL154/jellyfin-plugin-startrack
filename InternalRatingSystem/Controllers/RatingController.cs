using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Data;
using Jellyfin.Plugin.InternalRating.Models;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Controllers
{
    /// <summary>
    /// REST API controller for StarTrack.
    /// All endpoints require a valid Jellyfin session token.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("Plugins/StarTrack")]
    [Produces(MediaTypeNames.Application.Json)]
    public class RatingController : ControllerBase
    {
        private readonly RatingRepository _repository;
        private readonly IAuthorizationContext _authContext;
        private readonly ILogger<RatingController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RatingController"/> class.
        /// </summary>
        public RatingController(
            IAuthorizationContext authContext,
            ILogger<RatingController> logger)
        {
            _repository  = Plugin.Instance!.Repository;
            _authContext = authContext;
            _logger      = logger;
        }

        /// <summary>Gets all ratings for an item.</summary>
        // GET /Plugins/StarTrack/Ratings/{itemId}
        [HttpGet("Ratings/{itemId}")]
        [ProducesResponseType(typeof(RatingsResponse), StatusCodes.Status200OK)]
        public async Task<ActionResult<RatingsResponse>> GetRatings([FromRoute] string itemId)
        {
            var result = await _repository.GetRatingsAsync(itemId).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>Submits or updates the current user's rating for an item.</summary>
        // POST /Plugins/StarTrack/Ratings/{itemId}   body: { "stars": 4 }
        [HttpPost("Ratings/{itemId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> SubmitRating(
            [FromRoute] string itemId,
            [FromBody]  SubmitRatingRequest request)
        {
            if (request.Stars < 0.5 || request.Stars > 5)
                return BadRequest("Stars must be between 0.5 and 5.");

            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null)
            {
                _logger.LogWarning("[StarTrack] SubmitRating: could not resolve user ID. Claims: {Claims}",
                    string.Join(", ", User.Claims.Select(c => $"{c.Type}={c.Value}")));
                return Unauthorized();
            }

            var userName = GetCurrentUserName();
            await _repository.SaveRatingAsync(itemId, userId.Value.ToString("N"), userName, request.Stars, request.Review)
                .ConfigureAwait(false);

            _logger.LogInformation("[StarTrack] {User} rated {Item}: {Stars}★", userName, itemId, request.Stars);
            return Ok();
        }

        /// <summary>Removes the current user's rating for an item.</summary>
        // DELETE /Plugins/StarTrack/Ratings/{itemId}
        [HttpDelete("Ratings/{itemId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> DeleteRating([FromRoute] string itemId)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null)
                return Unauthorized();

            await _repository.DeleteRatingAsync(itemId, userId.Value.ToString("N"))
                .ConfigureAwait(false);
            return Ok();
        }

        /// <summary>Serves the embedded widget.js (no auth needed).</summary>
        // GET /Plugins/StarTrack/Widget
        [HttpGet("Widget")]
        [AllowAnonymous]
        [Produces("application/javascript")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetWidget()
        {
            var asm = Assembly.GetExecutingAssembly();
            const string res = "Jellyfin.Plugin.InternalRating.Web.widget.js";
            var stream = asm.GetManifestResourceStream(res);
            if (stream == null)
                return NotFound();

            return File(stream, "application/javascript; charset=utf-8");
        }

        /// <summary>Returns the most recently submitted ratings across all items.</summary>
        // GET /Plugins/StarTrack/Recent?limit=20
        [HttpGet("Recent")]
        [ProducesResponseType(typeof(List<RecentRatingDto>), StatusCodes.Status200OK)]
        public async Task<ActionResult<List<RecentRatingDto>>> GetRecent([FromQuery] int limit = 20)
        {
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;
            var result = await _repository.GetRecentAsync(limit).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>Returns server-wide rating statistics.</summary>
        // GET /Plugins/StarTrack/Stats
        [HttpGet("Stats")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult GetStats()
        {
            var (totalItems, totalRatings) = _repository.GetStats();
            return Ok(new { totalItems, totalRatings });
        }

        /// <summary>Returns auth info for the current user — useful for debugging save failures.</summary>
        // GET /Plugins/StarTrack/WhoAmI
        [HttpGet("WhoAmI")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> GetWhoAmI()
        {
            Guid? authCtxUserId = null;
            string authCtxError = "none";
            try
            {
                var info = await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
                authCtxUserId = info?.UserId;
            }
            catch (Exception ex) { authCtxError = ex.Message; }

            var claims = User.Claims.Select(c => new { c.Type, c.Value }).ToList();
            return Ok(new
            {
                isAuthenticated  = User.Identity?.IsAuthenticated,
                authCtxUserId,
                authCtxError,
                resolvedUserId   = await GetCurrentUserIdAsync().ConfigureAwait(false),
                resolvedUserName = GetCurrentUserName(),
                claims
            });
        }

        /// <summary>Diagnostic info — no auth needed.</summary>
        // GET /Plugins/StarTrack/Debug
        [HttpGet("Debug")]
        [AllowAnonymous]
        [Produces("text/plain")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult GetDebug()
        {
            var sb = new StringBuilder();
            var version = GetType().Assembly.GetName().Version?.ToString() ?? "unknown";
            sb.AppendLine($"StarTrack v{version} — Diagnostic Report");
            sb.AppendLine("======================================");
            sb.AppendLine($"Plugin loaded         : YES");
            sb.AppendLine($"WebPath               : {WebInjectionService.DiagWebPath}");
            sb.AppendLine($"Injection method      : HTTP middleware (IStartupFilter)");
            sb.AppendLine($"File fallback found   : {WebInjectionService.DiagIndexFound}");
            sb.AppendLine($"File fallback patched : {WebInjectionService.DiagIndexPatched}");
            sb.AppendLine($"File fallback path    : {WebInjectionService.DiagPatchedPath}");
            sb.AppendLine($"Last error            : {WebInjectionService.DiagLastError}");
            sb.AppendLine($"Widget endpoint       : /Plugins/StarTrack/Widget");
            sb.AppendLine($"WhoAmI endpoint       : /Plugins/StarTrack/WhoAmI (auth required)");
            return Content(sb.ToString(), "text/plain");
        }

        // ------------------------------------------------------------------ //
        // Helpers
        // ------------------------------------------------------------------ //

        private async Task<Guid?> GetCurrentUserIdAsync()
        {
            // Primary: use Jellyfin's IAuthorizationContext — most reliable and version-stable
            try
            {
                var info = await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
                if (info?.UserId != null && info.UserId != Guid.Empty)
                    return info.UserId;
            }
            catch { /* fall through to claim parsing */ }

            // Fallback: parse claims directly.
            // Jellyfin 10.9–10.11 uses "Jellyfin-UserId" (InternalClaimTypes.UserId) with ToString("N") value.
            var value = User.FindFirst("Jellyfin-UserId")?.Value
                ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("uid")?.Value
                ?? User.FindFirst("sub")?.Value;

            return Guid.TryParse(value, out var id) ? id : null;
        }

        private string GetCurrentUserName()
        {
            // Get username from claims — avoids calling IUserManager.GetUserById which
            // changed its signature between Jellyfin 10.9 and 10.11 (MissingMethodException).
            return User.FindFirst("Jellyfin-User")?.Value
                ?? User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
                ?? User.Identity?.Name
                ?? "Unknown";
        }
    }
}
