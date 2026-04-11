using System;
using System.IO;
using System.IO.Compression;
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
        /// Imports a Letterboxd export for the current user. Accepts either:
        ///  - the raw <c>ratings.csv</c> (content-type text/csv or text/plain), or
        ///  - the full Letterboxd export ZIP (content-type application/zip) —
        ///    the controller extracts <c>ratings.csv</c> from inside the archive
        ///    automatically so users don't have to unzip it first.
        /// </summary>
        [HttpPost("Import")]
        [Consumes("text/csv", "text/plain", "application/zip", "application/octet-stream")]
        [ProducesResponseType(typeof(LetterboxdImportResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
        public async Task<IActionResult> ImportCsv()
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            var userName = GetCurrentUserName();

            // Guard against runaway uploads: cap at 5 MB. A Letterboxd full
            // export ZIP for 5000 films is about 200-400 KB compressed, so
            // 5 MB is generous headroom.
            if (Request.ContentLength > 5 * 1024 * 1024)
                return StatusCode(StatusCodes.Status413PayloadTooLarge, "File too large (max 5 MB).");

            // Buffer the request body into memory so we can detect the format
            // (ZIP vs CSV) by magic bytes and then rewind for the real parser.
            using var buffer = new MemoryStream();
            await Request.Body.CopyToAsync(buffer).ConfigureAwait(false);
            buffer.Position = 0;

            Stream csvStream;
            IDisposable? zipGuard = null;

            if (LooksLikeZip(buffer))
            {
                try
                {
                    var zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: false);
                    zipGuard = zip;
                    // Prefer ratings.csv (only rated films). Fall back to diary.csv
                    // which also has rating + watched date and usually the same
                    // set of entries for most users.
                    var entry = FindCsvEntry(zip, "ratings.csv")
                             ?? FindCsvEntry(zip, "diary.csv");
                    if (entry == null)
                    {
                        return Ok(new LetterboxdImportResult
                        {
                            Error = "ZIP did not contain ratings.csv or diary.csv. Make sure you uploaded the full Letterboxd export ZIP from Settings \u2192 Import & Export."
                        });
                    }
                    csvStream = entry.Open();
                }
                catch (InvalidDataException)
                {
                    return Ok(new LetterboxdImportResult { Error = "Uploaded file is not a valid ZIP archive." });
                }
            }
            else
            {
                buffer.Position = 0;
                csvStream = buffer;
            }

            LetterboxdImportResult result;
            try
            {
                result = await _sync.ImportCsvAsync(userId.Value.ToString("N"), userName, csvStream).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] Letterboxd CSV import failed");
                return Ok(new LetterboxdImportResult { Error = ex.Message });
            }
            finally
            {
                csvStream?.Dispose();
                zipGuard?.Dispose();
            }

            _logger.LogInformation("[StarTrack] {User} import: imported={I} updated={U} unmatched={N} ambiguous={A}",
                userName, result.Imported, result.Updated, result.Unmatched, result.Ambiguous);
            return Ok(result);
        }

        /// <summary>
        /// Detects the ZIP local file header magic bytes (PK\x03\x04).
        /// Rewinds the stream to position 0 before returning so the caller
        /// can read it from the start.
        /// </summary>
        private static bool LooksLikeZip(Stream s)
        {
            if (!s.CanSeek || s.Length < 4) return false;
            var saved = s.Position;
            s.Position = 0;
            Span<byte> sig = stackalloc byte[4];
            var read = s.Read(sig);
            s.Position = saved;
            return read == 4 && sig[0] == 0x50 && sig[1] == 0x4B && sig[2] == 0x03 && sig[3] == 0x04;
        }

        /// <summary>Case-insensitive lookup for a CSV entry anywhere in the ZIP.</summary>
        private static ZipArchiveEntry? FindCsvEntry(ZipArchive zip, string filename)
        {
            return zip.Entries.FirstOrDefault(e =>
                string.Equals(e.Name, filename, StringComparison.OrdinalIgnoreCase));
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
