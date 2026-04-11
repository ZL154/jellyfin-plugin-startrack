using System;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Controllers
{
    /// <summary>
    /// Letterboxd sync endpoints. All routes are scoped to the current
    /// authenticated user — users can only view or modify their own settings,
    /// import their own CSV, and trigger sync for themselves.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("Plugins/StarTrack/Letterboxd")]
    [Produces(MediaTypeNames.Application.Json)]
    public class LetterboxdController : ControllerBase
    {
        private readonly LetterboxdSettingsRepository _settings;
        private readonly LetterboxdSyncService _sync;
        private readonly IAuthorizationContext _authContext;
        private readonly ILogger<LetterboxdController> _logger;

        public LetterboxdController(
            LetterboxdSyncService syncService,
            IAuthorizationContext authContext,
            ILogger<LetterboxdController> logger)
        {
            _settings    = Plugin.Instance!.LetterboxdSettings;
            _sync        = syncService;
            _authContext = authContext;
            _logger      = logger;
        }

        /// <summary>Returns the current user's Letterboxd sync settings.</summary>
        [HttpGet("Settings")]
        [ProducesResponseType(typeof(LetterboxdUserSettings), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetSettings()
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            var s = await _settings.GetAsync(userId.Value.ToString("N")).ConfigureAwait(false);
            return Ok(s);
        }

        /// <summary>Updates the current user's Letterboxd username + auto-sync toggle.</summary>
        [HttpPost("Settings")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> SetSettings([FromBody] SetSettingsRequest req)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            var username = (req.Username ?? string.Empty).Trim();
            // Basic sanity: Letterboxd usernames are [a-z0-9_]{2,15}
            if (username.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(username, "^[a-zA-Z0-9_-]{1,32}$"))
                return BadRequest("Letterboxd username contains invalid characters.");

            await _settings.SetConfigAsync(userId.Value.ToString("N"), username, req.EnableAutoSync).ConfigureAwait(false);
            _logger.LogInformation("[StarTrack] {User} set Letterboxd username={Name} autosync={Auto}", userId.Value, username, req.EnableAutoSync);
            return Ok();
        }

        /// <summary>
        /// Triggers an immediate RSS sync for the current user. Returns the
        /// import report synchronously.
        /// </summary>
        [HttpPost("SyncNow")]
        [ProducesResponseType(typeof(LetterboxdImportResult), StatusCodes.Status200OK)]
        public async Task<IActionResult> SyncNow()
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            var userName = GetCurrentUserName();

            var result = await _sync.SyncRssAsync(userId.Value.ToString("N"), userName).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// Imports a Letterboxd ratings.csv export for the current user.
        /// Body is raw CSV (text/csv or application/octet-stream).
        /// </summary>
        [HttpPost("Import")]
        [Consumes("text/csv", "text/plain", "application/octet-stream")]
        [ProducesResponseType(typeof(LetterboxdImportResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
        public async Task<IActionResult> ImportCsv()
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            var userName = GetCurrentUserName();

            // Guard against runaway uploads: cap at 5 MB (a Letterboxd ratings export
            // for 5000 films is about 300 KB)
            if (Request.ContentLength > 5 * 1024 * 1024)
                return StatusCode(StatusCodes.Status413PayloadTooLarge, "CSV too large (max 5 MB).");

            LetterboxdImportResult result;
            try
            {
                result = await _sync.ImportCsvAsync(userId.Value.ToString("N"), userName, Request.Body).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] Letterboxd CSV import failed");
                return Ok(new LetterboxdImportResult { Error = ex.Message });
            }

            _logger.LogInformation("[StarTrack] {User} CSV import: imported={I} updated={U} unmatched={N}",
                userName, result.Imported, result.Updated, result.Unmatched);
            return Ok(result);
        }

        // ------------------------------------------------------------------
        // Auth helpers (same approach as RatingController)
        // ------------------------------------------------------------------

        private async Task<Guid?> GetCurrentUserIdAsync()
        {
            try
            {
                var info = await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
                if (info?.UserId != null && info.UserId != Guid.Empty)
                    return info.UserId;
            }
            catch { }

            var value = User.FindFirst("Jellyfin-UserId")?.Value
                ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("uid")?.Value
                ?? User.FindFirst("sub")?.Value;
            return Guid.TryParse(value, out var id) ? id : null;
        }

        private string GetCurrentUserName()
        {
            return User.FindFirst("Jellyfin-User")?.Value
                ?? User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
                ?? User.Identity?.Name
                ?? "Unknown";
        }

        public sealed class SetSettingsRequest
        {
            public string? Username       { get; set; }
            public bool    EnableAutoSync { get; set; }
        }
    }
}
