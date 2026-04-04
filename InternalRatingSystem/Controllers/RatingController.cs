using System;
using System.Net.Mime;
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
    /// REST API controller for the Internal Rating System.
    /// All endpoints require Jellyfin authentication.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("Plugins/InternalRating")]
    [Produces(MediaTypeNames.Application.Json)]
    public class RatingController : ControllerBase
    {
        private readonly RatingRepository _repository;
        private readonly IUserManager _userManager;
        private readonly ILogger<RatingController> _logger;

        public RatingController(
            RatingRepository repository,
            IUserManager userManager,
            ILogger<RatingController> logger)
        {
            _repository  = repository;
            _userManager = userManager;
            _logger      = logger;
        }

        // ---------------------------------------------------------------
        // GET /Plugins/InternalRating/Ratings/{itemId}
        // Returns average rating + all individual user ratings for an item.
        // ---------------------------------------------------------------
        [HttpGet("Ratings/{itemId}")]
        [ProducesResponseType(typeof(RatingsResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult<RatingsResponse>> GetRatings([FromRoute] string itemId)
        {
            var result = await _repository.GetRatingsAsync(itemId).ConfigureAwait(false);
            return Ok(result);
        }

        // ---------------------------------------------------------------
        // POST /Plugins/InternalRating/Ratings/{itemId}
        // Submit (or update) the authenticated user's rating for an item.
        // Body: { "stars": 4 }
        // ---------------------------------------------------------------
        [HttpPost("Ratings/{itemId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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

            _logger.LogInformation("User {UserName} rated item {ItemId}: {Stars} stars", userName, itemId, request.Stars);
            return Ok();
        }

        // ---------------------------------------------------------------
        // DELETE /Plugins/InternalRating/Ratings/{itemId}
        // Remove the authenticated user's rating for an item.
        // ---------------------------------------------------------------
        [HttpDelete("Ratings/{itemId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> DeleteRating([FromRoute] string itemId)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            await _repository.DeleteRatingAsync(itemId, userId.Value.ToString("N"))
                .ConfigureAwait(false);

            return Ok();
        }

        // ---------------------------------------------------------------
        // GET /Plugins/InternalRating/Stats
        // Returns server-wide rating statistics.
        // ---------------------------------------------------------------
        [HttpGet("Stats")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult GetStats()
        {
            var (totalItems, totalRatings) = _repository.GetStats();
            return Ok(new { totalItems, totalRatings });
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        private Guid? GetCurrentUserId()
        {
            // Jellyfin stores the user ID in the "Jellyfin-UserId" claim
            var value = User.FindFirst("Jellyfin-UserId")?.Value
                ?? User.FindFirst("uid")?.Value
                ?? User.FindFirst("sub")?.Value;

            return Guid.TryParse(value, out var id) ? id : null;
        }

        private string GetCurrentUserName(Guid userId)
        {
            try
            {
                var user = _userManager.GetUserById(userId);
                return user?.Username ?? "Unknown";
            }
            catch
            {
                return User.Identity?.Name ?? "Unknown";
            }
        }
    }
}
