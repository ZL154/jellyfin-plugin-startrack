using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.InternalRating.ExternalSync;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Controllers
{
    /// <summary>
    /// Letterboxd sync endpoints. Most routes are scoped to the current
    /// authenticated user — users can view or modify their own settings,
    /// import their own CSV, and trigger sync for themselves. The
    /// <c>AdminUsers</c>/<c>AdminSettings</c> pair below is the exception:
    /// elevated admins can link a Letterboxd username on behalf of any user.
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
        private readonly IUserManager _userManager;
        private readonly LetterboxdPushRunner _runner;
        private readonly IRatingGatherer _gatherer;
        private readonly ILogger<LetterboxdController> _logger;

        public LetterboxdController(
            LetterboxdSyncService syncService,
            IAuthorizationContext authContext,
            IUserManager userManager,
            LetterboxdPushRunner runner,
            IRatingGatherer gatherer,
            ILogger<LetterboxdController> logger)
        {
            _settings    = Plugin.Instance!.LetterboxdSettings;
            _sync        = syncService;
            _authContext = authContext;
            _userManager = userManager;
            _runner      = runner;
            _gatherer    = gatherer;
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
        /// Admin-only: every Jellyfin user on the server plus their current
        /// Letterboxd link (if any), for the Letterboxd sync admin panel.
        /// </summary>
        [HttpGet("AdminUsers")]
        [Authorize(Policy = "RequiresElevation")]
        [ProducesResponseType(typeof(List<AdminUserLetterboxdDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAdminUsers()
        {
            var list = new List<AdminUserLetterboxdDto>();
            var users = _userManager.EnumerateAll();
            if (users == null) return Ok(list);

            var all = await _settings.GetAllAsync().ConfigureAwait(false);

            foreach (var u in users)
            {
                if (u == null) continue;
                var idStr = u.Id.ToString("N");
                all.TryGetValue(idStr, out var s);
                s ??= new LetterboxdUserSettings();
                list.Add(new AdminUserLetterboxdDto
                {
                    UserId         = idStr,
                    UserName       = u.Username,
                    Username       = s.Username,
                    EnableAutoSync = s.EnableAutoSync,
                    LastSyncedAt   = s.LastSyncedAt
                });
            }

            return Ok(list);
        }

        /// <summary>
        /// Admin-only: sets a target user's Letterboxd username + auto-sync
        /// toggle, then runs an immediate sync so the admin gets the same
        /// "N ratings imported" feedback the user's own Sync Now button gives.
        /// </summary>
        [HttpPost("AdminSettings/{userId}")]
        [Authorize(Policy = "RequiresElevation")]
        [ProducesResponseType(typeof(LetterboxdImportResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> SetAdminSettings([FromRoute] string userId, [FromBody] SetSettingsRequest req)
        {
            if (!Guid.TryParse(userId, out var targetId)) return NotFound();
            var target = _userManager.GetUserById(targetId);
            if (target == null) return NotFound();

            var username = (req.Username ?? string.Empty).Trim();
            if (username.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(username, "^[a-zA-Z0-9_-]{1,32}$"))
                return BadRequest("Letterboxd username contains invalid characters.");

            var idStr = targetId.ToString("N");
            await _settings.SetConfigAsync(idStr, username, req.EnableAutoSync).ConfigureAwait(false);
            _logger.LogInformation("[StarTrack] Admin set Letterboxd username for {User}: {Name} autosync={Auto}",
                target.Username, username, req.EnableAutoSync);

            if (string.IsNullOrEmpty(username))
                return Ok(new LetterboxdImportResult());

            var result = await _sync.SyncRssAsync(idStr, target.Username).ConfigureAwait(false);
            return Ok(result);
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

            _logger.LogInformation("[StarTrack] Letterboxd SyncNow request received from {User}", userName);
            var result = await _sync.SyncRssAsync(userId.Value.ToString("N"), userName).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// Diagnostic endpoint — runs the library query the matcher uses
        /// and returns the total movie count, the "used fallback" flag,
        /// and a sample of the first 20 normalized titles so the user can
        /// verify the library is being read correctly.
        /// </summary>
        [HttpGet("Diagnose")]
        [Authorize(Policy = "RequiresElevation")]
        [ProducesResponseType(typeof(LetterboxdDiagnoseResult), StatusCodes.Status200OK)]
        public async Task<IActionResult> Diagnose()
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            _logger.LogInformation("[StarTrack] Letterboxd Diagnose request received");
            var result = _sync.Diagnose();
            _logger.LogInformation("[StarTrack] Letterboxd Diagnose: library={N}, fallback={F}, samples={S}, zombies={Z}",
                result.LibraryMovieCount, result.UsedFallbackQuery, result.SampleMovies.Count, result.ZombiesFiltered);
            return Ok(result);
        }

        /// <summary>
        /// Scrapes the user's Letterboxd profile page for the "favourite
        /// films" section and sets them as StarTrack favorites. Requires
        /// the user's Letterboxd username to already be saved.
        /// </summary>
        [HttpPost("ScrapeFavorites")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> ScrapeFavorites()
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            var userIdStr = userId.Value.ToString("N");

            var settings = await _settings.GetAsync(userIdStr).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(settings.Username))
                return Ok(new { imported = 0, error = "No Letterboxd username saved." });

            _logger.LogInformation("[StarTrack] ScrapeFavorites request from {User}", GetCurrentUserName());
            var lookup = _sync.BuildLookupForImport();
            var count = await _sync.ScrapeLetterboxdFavoritesAsync(userIdStr, settings.Username!, lookup).ConfigureAwait(false);
            return Ok(new { imported = count });
        }

        /// <summary>
        /// Cleans up dead ratings — rating entries that point to library
        /// items whose underlying file no longer exists on disk. This is
        /// what happens when a hard drive dies and Jellyfin leaves zombie
        /// DB rows behind. Returns the number of rating rows + items
        /// removed.
        /// </summary>
        [HttpPost("Cleanup")]
        [Authorize(Policy = "RequiresElevation")]
        [ProducesResponseType(typeof(CleanupResult), StatusCodes.Status200OK)]
        public async Task<IActionResult> CleanupDeadRatings()
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            _logger.LogInformation("[StarTrack] Cleanup dead-ratings request received from {User}", GetCurrentUserName());
            var result = await _sync.CleanupDeadRatingsAsync().ConfigureAwait(false);
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

            _logger.LogInformation("[StarTrack] Letterboxd Import request received from {User}, contentLength={Len}, contentType={Type}",
                userName, Request.ContentLength, Request.ContentType);

            // Guard against runaway uploads: cap at 5 MB. A Letterboxd full
            // export ZIP for 5000 films is about 200-400 KB compressed, so
            // 5 MB is generous headroom.
            //
            // Reject when Content-Length is missing OR over the cap. A
            // chunked-encoded request without Content-Length would otherwise
            // bypass the size check entirely and stream unbounded into the
            // MemoryStream below — OOM the host.
            const long MaxBytes = 5L * 1024 * 1024;
            if (Request.ContentLength is null || Request.ContentLength > MaxBytes)
                return StatusCode(StatusCodes.Status413PayloadTooLarge, "Upload missing Content-Length or exceeds 5 MB.");

            // Buffer the request body into memory so we can detect the format
            // (ZIP vs CSV) by magic bytes and then rewind for the real parser.
            // Defense in depth: enforce the cap a second time during the copy
            // in case ContentLength lied about the actual body size.
            using var buffer = new MemoryStream();
            var copyBuf = new byte[64 * 1024];
            int read;
            long total = 0;
            while ((read = await Request.Body.ReadAsync(copyBuf.AsMemory(0, copyBuf.Length)).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxBytes)
                    return StatusCode(StatusCodes.Status413PayloadTooLarge, "Upload exceeded 5 MB during transfer.");
                buffer.Write(copyBuf, 0, read);
            }
            buffer.Position = 0;

            // Two code paths:
            //  A) Raw CSV body → assume it's ratings.csv, import ratings only.
            //  B) ZIP body → extract ratings.csv, watchlist.csv, likes.csv
            //     (any of them that exist) and import all of them in one pass.
            if (LooksLikeZip(buffer))
            {
                try
                {
                    using var zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: false);

                    var ratingsEntry = FindCsvEntry(zip, "ratings.csv");
                    var diaryEntry   = FindCsvEntry(zip, "diary.csv");
                    var watchEntry   = FindCsvEntry(zip, "watchlist.csv");
                    // Letterboxd puts likes at "likes/films.csv" inside the
                    // archive, NOT at the root. Try the path first, fall back
                    // to a flat "likes.csv" for older or alternate exports.
                    var likesEntry   = FindCsvEntry(zip, "likes/films.csv")
                                    ?? FindCsvEntry(zip, "likes.csv");

                    var oversizedEntry = new[] { ratingsEntry, diaryEntry, watchEntry, likesEntry }
                        .Where(e => e != null)
                        .FirstOrDefault(e => e!.Length > MaxBytes);
                    if (oversizedEntry != null)
                    {
                        return StatusCode(StatusCodes.Status413PayloadTooLarge,
                            $"ZIP entry {oversizedEntry.FullName} exceeds the 5 MB CSV limit.");
                    }

                    if (ratingsEntry == null && diaryEntry == null && watchEntry == null && likesEntry == null)
                    {
                        return Ok(new LetterboxdImportResult
                        {
                            Error = "ZIP did not contain any recognised Letterboxd CSVs (ratings.csv, diary.csv, watchlist.csv, likes.csv). Make sure you uploaded the full export ZIP from Settings \u2192 Import & Export."
                        });
                    }

                    // Build the movie lookup ONCE and reuse it for every CSV
                    // in the ZIP so we don't hit the library 4x.
                    var lookup = _sync.BuildLookupForImport();
                    var userIdStr = userId.Value.ToString("N");

                    // Ratings first so the result object carries the full
                    // rating-import stats; then watchlist + likes + diary added.
                    LetterboxdImportResult result = new();
                    if (ratingsEntry != null)
                    {
                        try
                        {
                            using var rs = ratingsEntry.Open();
                            result = await _sync.ImportCsvAsync(userIdStr, userName, rs, lookup).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[StarTrack] ratings.csv import from ZIP failed");
                            result.Error = ex.Message;
                        }
                    }

                    if (watchEntry != null)
                    {
                        try
                        {
                            using var ws = watchEntry.Open();
                            var (wa, wsk) = await _sync.ImportWatchlistCsvAsync(userIdStr, ws, lookup).ConfigureAwait(false);
                            result.WatchlistAdded   += wa;
                            result.WatchlistSkipped += wsk;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[StarTrack] watchlist.csv import from ZIP failed");
                        }
                    }

                    if (likesEntry != null)
                    {
                        try
                        {
                            using var ls = likesEntry.Open();
                            var (la, lsk) = await _sync.ImportLikesCsvAsync(userIdStr, ls, lookup).ConfigureAwait(false);
                            result.LikesAdded   += la;
                            result.LikesSkipped += lsk;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[StarTrack] likes.csv import from ZIP failed");
                        }
                    }

                    if (diaryEntry != null)
                    {
                        try
                        {
                            using var ds = diaryEntry.Open();
                            await _sync.ImportDiaryCsvAsync(userIdStr, ds, lookup).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[StarTrack] diary.csv import from ZIP failed");
                        }
                    }

                    _logger.LogInformation("[StarTrack] {User} ZIP import: ratings={R} updated={U} watchlist+{W} likes+{L}",
                        userName, result.Imported, result.Updated, result.WatchlistAdded, result.LikesAdded);
                    return Ok(result);
                }
                catch (InvalidDataException)
                {
                    return Ok(new LetterboxdImportResult { Error = "Uploaded file is not a valid ZIP archive." });
                }
            }

            // Raw CSV path — legacy direct ratings.csv upload
            buffer.Position = 0;
            LetterboxdImportResult csvResult;
            try
            {
                csvResult = await _sync.ImportCsvAsync(userId.Value.ToString("N"), userName, buffer).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] Letterboxd CSV import failed");
                return Ok(new LetterboxdImportResult { Error = ex.Message });
            }

            _logger.LogInformation("[StarTrack] {User} import: imported={I} updated={U} unmatched={N} ambiguous={A}",
                userName, csvResult.Imported, csvResult.Updated, csvResult.Unmatched, csvResult.Ambiguous);
            return Ok(csvResult);
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

        /// <summary>
        /// Case-insensitive lookup for a CSV entry anywhere in the ZIP.
        /// Matches BOTH the bare filename (e.g. "ratings.csv") and the
        /// full path (e.g. "likes/films.csv") because Letterboxd puts the
        /// likes file inside a "likes/" subfolder rather than at the
        /// archive root.
        /// </summary>
        private static ZipArchiveEntry? FindCsvEntry(ZipArchive zip, string filename)
        {
            return zip.Entries.FirstOrDefault(e =>
                string.Equals(e.Name,     filename, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.FullName, filename, StringComparison.OrdinalIgnoreCase));
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

        // ================================================================== //
        // WRITE-BACK (v1.6.5)
        //
        // Letterboxd has no public write API, so pushing requires the account
        // password. These endpoints are scoped to the caller, and there is
        // deliberately NO admin equivalent: an admin linking a username on
        // someone's behalf is a convenience, an admin typing another user's
        // Letterboxd password is not something StarTrack should make easy.
        // ================================================================== //

        /// <summary>
        /// The caller's write-back configuration. Never returns the password or
        /// cookies, only whether they are set.
        /// </summary>
        [HttpGet("Account")]
        [ProducesResponseType(typeof(AccountStateDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAccount()
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            var s = await _settings.GetAsync(userId.Value.ToString("N")).ConfigureAwait(false);
            return Ok(new AccountStateDto
            {
                Username          = s.Username,
                Direction         = (int)s.Direction,
                HasPassword       = !string.IsNullOrEmpty(s.PasswordEnc),
                HasRawCookies     = !string.IsNullOrEmpty(s.RawCookiesEnc),
                UserAgent         = s.UserAgent,
                PushRatings       = s.PushRatings,
                PushWatched       = s.PushWatched,
                PushLiked         = s.PushLiked,
                PushReviews       = s.PushReviews,
                PushDiary         = s.PushDiary,
                PushWatchlist     = s.PushWatchlist,
                DiaryLoggingSince = s.DiaryLoggingSince,
                LastPushedAt      = s.LastPushedAt,
                LastPushedCount   = s.LastPushedCount,
                LastPushError     = s.LastPushError
            });
        }

        /// <summary>
        /// Saves the caller's write-back configuration. Omit the password to keep
        /// the stored one; send an empty string to clear it.
        /// </summary>
        [HttpPost("Account")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> SetAccount([FromBody] SetAccountRequest req)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            if (req.Direction < 0 || req.Direction > 3) return BadRequest("Unknown sync direction.");
            var direction = (LetterboxdDirection)req.Direction;

            // Refuse to arm an export direction with no way to authenticate,
            // rather than accepting it and failing quietly an hour later.
            var stored = await _settings.GetAsync(userId.Value.ToString("N")).ConfigureAwait(false);
            var willHavePassword = req.Password == null
                ? !string.IsNullOrEmpty(stored.PasswordEnc)
                : req.Password.Length > 0;

            var exporting = direction == LetterboxdDirection.ExportOnly || direction == LetterboxdDirection.TwoWay;
            if (exporting && !willHavePassword)
                return BadRequest("Pushing to Letterboxd needs the account password. Letterboxd has no public write API, so there is no token-based alternative.");

            var ok = await _settings.SetAccountAsync(
                userId.Value.ToString("N"),
                direction,
                req.Password,
                req.RawCookies,
                req.UserAgent,
                req.PushRatings, req.PushWatched, req.PushLiked, req.PushReviews,
                req.PushDiary, req.PushWatchlist).ConfigureAwait(false);

            if (!ok)
                return BadRequest("Could not encrypt the credentials for storage, so nothing was saved. StarTrack will not fall back to storing them in plain text.");

            return Ok();
        }

        /// <summary>
        /// Tests a Letterboxd sign-in without writing anything, so a bad password
        /// or a Cloudflare block is visible immediately instead of an hour later.
        /// </summary>
        [HttpPost("VerifyLogin")]
        [ProducesResponseType(typeof(VerifyResultDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> VerifyLogin([FromBody] VerifyLoginRequest req)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            var s = await _settings.GetAsync(userId.Value.ToString("N")).ConfigureAwait(false);
            var username = string.IsNullOrWhiteSpace(req.Username) ? s.Username : req.Username.Trim();

            // Fall back to the stored password so Verify works without the
            // browser having to hold the secret again just to re-check it.
            var password = string.IsNullOrEmpty(req.Password)
                ? LetterboxdSecretProtector.Unprotect(s.PasswordEnc)
                : req.Password;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
                return Ok(new VerifyResultDto { Ok = false, Status = "BadCredentials", Message = "Username and password are both required." });

            var cookies = string.IsNullOrEmpty(req.RawCookies)
                ? LetterboxdSecretProtector.Unprotect(s.RawCookiesEnc)
                : req.RawCookies;

            var r = await _runner.VerifyAsync(username, password, cookies, req.UserAgent ?? s.UserAgent, HttpContext.RequestAborted)
                                 .ConfigureAwait(false);

            return Ok(new VerifyResultDto { Ok = r.Ok, Status = r.Status.ToString(), Message = r.Message });
        }

        /// <summary>Runs a push for the caller immediately and returns the report.</summary>
        [HttpPost("PushNow")]
        [ProducesResponseType(typeof(LetterboxdPushResult), StatusCodes.Status200OK)]
        public async Task<IActionResult> PushNow()
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            // Smaller cap and tighter pacing than the scheduled task: this one is
            // a button press and has to come back while the user is still looking
            // at it. Anything left over is reported as Remaining and picked up by
            // the hourly task, or by pressing again.
            var r = await _runner.RunForUserAsync(
                userId.Value.ToString("N"), HttpContext.RequestAborted, maxFilms: 25, delayMs: 120)
                .ConfigureAwait(false);
            _logger.LogInformation("[StarTrack] Letterboxd PushNow for {User}: written={W} error={E}",
                userId.Value, r.TotalWritten, r.Error ?? "none");
            return Ok(r);
        }

        /// <summary>
        /// Downloads the caller's ratings as a CSV that letterboxd.com/import
        /// accepts. This is the credential-free route into Letterboxd: no
        /// password, nothing for Cloudflare to block, and it works for accounts
        /// with two-factor authentication, which the session-based push
        /// genuinely cannot support.
        /// </summary>
        [HttpGet("ExportCsv")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> ExportCsv()
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            var ratings = await _gatherer.GatherAsync(userId.Value.ToString("N")).ConfigureAwait(false);
            var csv = LetterboxdCsvExporter.Build(ratings);

            return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", "startrack-letterboxd.csv");
        }

        public sealed class SetSettingsRequest
        {
            public string? Username       { get; set; }
            public bool    EnableAutoSync { get; set; }
        }

        /// <summary>Write-back configuration as returned to the UI. Secrets are reported as booleans only.</summary>
        public sealed class AccountStateDto
        {
            [JsonPropertyName("username")]          public string    Username          { get; set; } = string.Empty;
            [JsonPropertyName("direction")]         public int       Direction         { get; set; }
            [JsonPropertyName("hasPassword")]       public bool      HasPassword       { get; set; }
            [JsonPropertyName("hasRawCookies")]     public bool      HasRawCookies     { get; set; }
            [JsonPropertyName("userAgent")]         public string?   UserAgent         { get; set; }
            [JsonPropertyName("pushRatings")]       public bool      PushRatings       { get; set; }
            [JsonPropertyName("pushWatched")]       public bool      PushWatched       { get; set; }
            [JsonPropertyName("pushLiked")]         public bool      PushLiked         { get; set; }
            [JsonPropertyName("pushReviews")]       public bool      PushReviews       { get; set; }
            [JsonPropertyName("pushDiary")]         public bool      PushDiary         { get; set; }
            [JsonPropertyName("pushWatchlist")]     public bool      PushWatchlist     { get; set; }
            [JsonPropertyName("diaryLoggingSince")] public DateTime? DiaryLoggingSince { get; set; }
            [JsonPropertyName("lastPushedAt")]      public DateTime? LastPushedAt      { get; set; }
            [JsonPropertyName("lastPushedCount")]   public int       LastPushedCount   { get; set; }
            [JsonPropertyName("lastPushError")]     public string?   LastPushError     { get; set; }
        }

        /// <summary>Write-back configuration submitted by the UI.</summary>
        public sealed class SetAccountRequest
        {
            /// <summary>0 Off, 1 ImportOnly, 2 ExportOnly, 3 TwoWay.</summary>
            public int     Direction   { get; set; }

            /// <summary>Null keeps the stored password; empty string clears it.</summary>
            public string? Password    { get; set; }

            /// <summary>Null keeps stored cookies; empty string clears them.</summary>
            public string? RawCookies  { get; set; }

            /// <summary>User-Agent paired with RawCookies for Cloudflare.</summary>
            public string? UserAgent   { get; set; }

            public bool    PushRatings { get; set; } = true;
            public bool    PushWatched { get; set; } = true;
            public bool    PushLiked   { get; set; } = true;
            public bool    PushReviews { get; set; }
            public bool    PushDiary   { get; set; }

            /// <summary>Mirror the StarTrack watchlist into Letterboxd. Additive only.</summary>
            public bool    PushWatchlist { get; set; }
        }

        /// <summary>Credentials to test. Empty fields fall back to what is stored.</summary>
        public sealed class VerifyLoginRequest
        {
            public string? Username   { get; set; }
            public string? Password   { get; set; }
            public string? RawCookies { get; set; }
            public string? UserAgent  { get; set; }
        }

        /// <summary>Outcome of a verify attempt.</summary>
        public sealed class VerifyResultDto
        {
            [JsonPropertyName("ok")]      public bool    Ok      { get; set; }
            [JsonPropertyName("status")]  public string  Status  { get; set; } = string.Empty;
            [JsonPropertyName("message")] public string? Message { get; set; }
        }
    }
}
