using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Data;
using Jellyfin.Plugin.InternalRating.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
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
        private readonly PrivacyRepository _privacy;
        private readonly IAuthorizationContext _authContext;
        private readonly ILogger<RatingController> _logger;
        private readonly IUserDataManager _userDataManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="RatingController"/> class.
        /// </summary>
        public RatingController(
            IAuthorizationContext authContext,
            ILogger<RatingController> logger,
            IUserDataManager userDataManager,
            ILibraryManager libraryManager,
            IUserManager userManager)
        {
            _repository      = Plugin.Instance!.Repository;
            _privacy         = Plugin.Instance!.Privacy;
            _authContext     = authContext;
            _logger          = logger;
            _userDataManager = userDataManager;
            _libraryManager  = libraryManager;
            _userManager     = userManager;
        }

        /// <summary>[v1.6.2] (#12, damientkyt) Mirror a StarTrack rating into Jellyfin's
        /// native per-user rating field when the admin opts in. StarTrack stars (0.5–5)
        /// map x2 onto Jellyfin's 0–10 scale; pass <paramref name="stars"/> = null to
        /// clear the native rating (on delete). Best-effort — never blocks the rating.</summary>
        private void MirrorNativeRating(Guid userId, string itemId, double? stars)
        {
            if (!(Plugin.Instance?.Configuration?.MirrorToNativeRating ?? false)) return;
            if (WriteNativeRating(userId, itemId, stars))
                _logger.LogInformation("[StarTrack] Mirrored rating to native field: item {Item} = {Rating}", itemId, stars.HasValue ? stars.Value * 2.0 : (double?)null);
        }

        /// <summary>[v1.6.4] Core write of a StarTrack rating into Jellyfin's native
        /// per-user rating field, WITHOUT the opt-in config gate. Used by the gated
        /// live mirror (<see cref="MirrorNativeRating"/>) and by the explicit admin
        /// backfill. Best-effort; returns true only when the native field was written.</summary>
        private bool WriteNativeRating(Guid userId, string itemId, double? stars)
        {
            try
            {
                if (!Guid.TryParse(itemId, out var itemGuid)) return false;
                var item = _libraryManager.GetItemById(itemGuid);
                if (item == null) return false;
                var user = _userManager.GetUserById(userId);
                if (user == null) return false;
                var data = _userDataManager.GetUserData(user, item);
                if (data == null) return false;
                data.Rating = stars.HasValue ? stars.Value * 2.0 : (double?)null;
                _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StarTrack] Failed to write native rating for item {Item}", itemId);
                return false;
            }
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
            // Reject non-Guid itemIds up front so an authenticated user can't
            // pollute ratings.json with arbitrary string keys (DoS / disk fill).
            if (!Guid.TryParse(itemId, out _))
                return BadRequest("Invalid item id.");
            if (request.Stars < 0.5 || request.Stars > 5)
                return BadRequest("Stars must be between 0.5 and 5.");
            var maxReview = Plugin.Instance?.Configuration?.MaxReviewLength ?? 10000;
            if (maxReview < 1) maxReview = 1;
            if (maxReview > 10000) maxReview = 10000;
            if ((request.Review?.Length ?? 0) > maxReview)
                return BadRequest($"Review too long (max {maxReview} characters).");

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

            MirrorNativeRating(userId.Value, itemId, request.Stars);

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

            MirrorNativeRating(userId.Value, itemId, null);
            return Ok();
        }

        /// <summary>[v1.6.4] (#12, damientkyt) One-shot admin backfill: writes every
        /// existing StarTrack rating into each user's native Jellyfin rating field.
        /// The live mirror only covers ratings made AFTER the toggle is enabled, so
        /// this catches up historical ratings. Runs regardless of the toggle (explicit
        /// admin action) and is idempotent — re-running just rewrites the same values.</summary>
        // POST /Plugins/StarTrack/BackfillNativeRatings
        [HttpPost("BackfillNativeRatings")]
        [Authorize(Policy = "RequiresElevation")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> BackfillNativeRatings()
        {
            int written = 0, skipped = 0;
            var userIds = await _repository.GetUserIdsWithRatingsAsync().ConfigureAwait(false);
            foreach (var uidStr in userIds)
            {
                if (!Guid.TryParse(uidStr, out var uid)) { continue; }
                var ratings = await _repository.GetUserRatingsAsync(uidStr, 10000).ConfigureAwait(false);
                foreach (var r in ratings)
                {
                    if (WriteNativeRating(uid, r.ItemId, r.Stars)) written++;
                    else skipped++;
                }
            }
            _logger.LogInformation("[StarTrack] Native-rating backfill complete: {Written} written, {Skipped} skipped", written, skipped);
            return Ok(new { written, skipped });
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

        /// <summary>Returns all ratings submitted by the current user, newest first.</summary>
        // GET /Plugins/StarTrack/MyRatings?limit=1000
        [HttpGet("MyRatings")]
        [ProducesResponseType(typeof(List<UserRatingEntry>), StatusCodes.Status200OK)]
        public async Task<ActionResult<List<UserRatingEntry>>> GetMyRatings([FromQuery] int limit = 10000)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            if (limit < 1) limit = 1;
            if (limit > 10000) limit = 10000;
            var result = await _repository.GetUserRatingsAsync(userId.Value.ToString("N"), limit).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>Returns the most recently submitted ratings across all items.</summary>
        // GET /Plugins/StarTrack/Recent?limit=20
        [HttpGet("Recent")]
        [ProducesResponseType(typeof(List<RecentRatingDto>), StatusCodes.Status200OK)]
        public async Task<ActionResult<List<RecentRatingDto>>> GetRecent([FromQuery] int limit = 20)
        {
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;

            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            var me = userId.Value.ToString("N");
            var hiddenActivity = await _privacy.GetActivityHiddenIdsAsync().ConfigureAwait(false);
            var hiddenMembers = await _privacy.GetHiddenUserIdsAsync().ConfigureAwait(false);

            var result = (await _repository.GetRecentAsync(500).ConfigureAwait(false))
                .Where(r =>
                    string.Equals(r.UserId, me, StringComparison.OrdinalIgnoreCase) ||
                    (!hiddenActivity.Contains(r.UserId) && !hiddenMembers.Contains(r.UserId)))
                .Take(limit)
                .ToList();
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

        /// <summary>Returns auth info for the current user — useful for debugging save failures. Admin-only.</summary>
        // GET /Plugins/StarTrack/WhoAmI
        [HttpGet("WhoAmI")]
        [Authorize(Policy = "RequiresElevation")]
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

        /// <summary>Diagnostic info — admin-only to avoid leaking host paths and last-error text.</summary>
        // GET /Plugins/StarTrack/Debug
        [HttpGet("Debug")]
        [Authorize(Policy = "RequiresElevation")]
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
