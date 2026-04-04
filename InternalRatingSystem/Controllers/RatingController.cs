using System;
using System.IO;
using System.Net.Mime;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Data;
using Jellyfin.Plugin.InternalRating.Models;
using MediaBrowser.Controller.Library;
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
        private readonly IUserManager _userManager;
        private readonly ILogger<RatingController> _logger;

        public RatingController(IUserManager userManager, ILogger<RatingController> logger)
        {
            // Repository is held on the Plugin singleton – no extra DI registration needed
            _repository  = Plugin.Instance!.Repository;
            _userManager = userManager;
            _logger      = logger;
        }

        // GET /Plugins/StarTrack/Ratings/{itemId}
        [HttpGet("Ratings/{itemId}")]
        [ProducesResponseType(typeof(RatingsResponse), StatusCodes.Status200OK)]
        public async Task<ActionResult<RatingsResponse>> GetRatings([FromRoute] string itemId)
        {
            var result = await _repository.GetRatingsAsync(itemId).ConfigureAwait(false);
            return Ok(result);
        }

        // POST /Plugins/StarTrack/Ratings/{itemId}   body: { "stars": 4 }
        [HttpPost("Ratings/{itemId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> SubmitRating(
            [FromRoute] string itemId,
            [FromBody]  SubmitRatingRequest request)
        {
            if (request.Stars is < 1 or > 5)
                return BadRequest("Stars must be between 1 and 5.");

            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            var userName = GetCurrentUserName(userId.Value);
            await _repository.SaveRatingAsync(itemId, userId.Value.ToString("N"), userName, request.Stars)
                .ConfigureAwait(false);

            _logger.LogInformation("[StarTrack] {User} rated {Item}: {Stars}★", userName, itemId, request.Stars);
            return Ok();
        }

        // DELETE /Plugins/StarTrack/Ratings/{itemId}
        [HttpDelete("Ratings/{itemId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> DeleteRating([FromRoute] string itemId)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            await _repository.DeleteRatingAsync(itemId, userId.Value.ToString("N"))
                .ConfigureAwait(false);
            return Ok();
        }

        // GET /Plugins/StarTrack/Widget  – serves the embedded widget.js (no auth needed)
        [HttpGet("Widget")]
        [AllowAnonymous]
        [Produces("application/javascript")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetWidget()
        {
            var asm        = Assembly.GetExecutingAssembly();
            const string res = "Jellyfin.Plugin.InternalRating.Web.widget.js";
            var stream     = asm.GetManifestResourceStream(res);
            if (stream == null)
                return NotFound();

            return File(stream, "application/javascript; charset=utf-8");
        }

        // GET /Plugins/StarTrack/Stats
        [HttpGet("Stats")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult GetStats()
        {
            var (totalItems, totalRatings) = _repository.GetStats();
            return Ok(new { totalItems, totalRatings });
        }

        // ------------------------------------------------------------------ //
        // Helpers
        // ------------------------------------------------------------------ //

        private Guid? GetCurrentUserId()
        {
            var value = User.FindFirst("Jellyfin-UserId")?.Value
                ?? User.FindFirst("uid")?.Value
                ?? User.FindFirst("sub")?.Value;

            return Guid.TryParse(value, out var id) ? id : null;
        }

        private string GetCurrentUserName(Guid userId)
        {
            try { return _userManager.GetUserById(userId)?.Username ?? "Unknown"; }
            catch { return User.Identity?.Name ?? "Unknown"; }
        }
    }
}
