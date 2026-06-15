using System;
using System.Net.Mime;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.ExternalSync;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Controllers
{
    /// <summary>
    /// Export / import endpoints for StarTrack ratings.
    /// All routes require a valid Jellyfin session token.
    /// Route prefix: /Plugins/StarTrack/ExternalSync
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("Plugins/StarTrack/ExternalSync")]
    [Produces(MediaTypeNames.Application.Json)]
    public class ExternalSyncController : ControllerBase
    {
        private readonly RatingGatherer _gatherer;
        private readonly ExternalIdResolver _resolver;
        private readonly FileExportService _exportService;
        private readonly IAuthorizationContext _authContext;
        private readonly ILogger<ExternalSyncController> _logger;

        public ExternalSyncController(
            RatingGatherer gatherer,
            ExternalIdResolver resolver,
            FileExportService exportService,
            IAuthorizationContext authContext,
            ILogger<ExternalSyncController> logger)
        {
            _gatherer      = gatherer;
            _resolver      = resolver;
            _exportService = exportService;
            _authContext   = authContext;
            _logger        = logger;
        }

        /// <summary>
        /// Exports the current user's StarTrack ratings as a downloadable CSV or JSON file.
        /// GET /Plugins/StarTrack/ExternalSync/Export?format=csv|json
        /// </summary>
        [HttpGet("Export")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> Export([FromQuery] string format = "csv")
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            var userIdStr = userId.Value.ToString("N");
            _logger.LogInformation("[StarTrack] ExternalSync Export request from user={User}, format={Fmt}", userIdStr, format);

            var ratings = await _gatherer.GatherAsync(userIdStr).ConfigureAwait(false);
            var dateSuffix = DateTime.UtcNow.ToString("yyyy-MM-dd");

            bool useJson = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);

            if (useJson)
            {
                var json = _exportService.BuildJson(ratings);
                var jsonBytes = Encoding.UTF8.GetBytes(json);
                return File(jsonBytes, "application/json", $"startrack-ratings-{dateSuffix}.json");
            }
            else
            {
                var csv = _exportService.BuildLetterboxdCsv(ratings);
                var csvBytes = Encoding.UTF8.GetBytes(csv);
                return File(csvBytes, "text/csv", $"startrack-ratings-{dateSuffix}.csv");
            }
        }

        /// <summary>
        /// Imports ratings from a CSV or JSON file body into the current user's StarTrack library.
        /// POST /Plugins/StarTrack/ExternalSync/Import?format=csv|json
        /// </summary>
        [HttpPost("Import")]
        [RequestSizeLimit(5 * 1024 * 1024)]
        [Consumes("text/csv", "text/plain", "application/json", "application/octet-stream")]
        [ProducesResponseType(typeof(SyncResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
        public async Task<IActionResult> Import([FromQuery] string format = "csv")
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            var userIdStr = userId.Value.ToString("N");
            var userName  = GetCurrentUserName();

            _logger.LogInformation("[StarTrack] ExternalSync Import request from user={User}, format={Fmt}, contentLength={Len}",
                userIdStr, format, Request.ContentLength);

            // Cap uploads at 5 MB to prevent OOM via unbounded MemoryStream.
            const long MaxBytes = 5L * 1024 * 1024;
            // Fast-reject when Content-Length header is present and already over limit.
            // Chunked-transfer (no Content-Length) is valid and handled by the mid-stream cap below.
            if (Request.ContentLength > MaxBytes)
                return StatusCode(StatusCodes.Status413PayloadTooLarge, "Upload exceeds 5 MB.");

            // Buffer the body
            using var ms = new System.IO.MemoryStream();
            var buf = new byte[64 * 1024];
            int read;
            long total = 0;
            while ((read = await Request.Body.ReadAsync(buf.AsMemory(0, buf.Length)).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxBytes)
                    return StatusCode(StatusCodes.Status413PayloadTooLarge, "Upload exceeded 5 MB during transfer.");
                ms.Write(buf, 0, read);
            }

            var bodyText = Encoding.UTF8.GetString(ms.ToArray());

            bool useJson = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase)
                           || (Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);

            System.Collections.Generic.IReadOnlyList<ExternalRating> parsed;
            try
            {
                parsed = useJson
                    ? _exportService.ParseJson(bodyText)
                    : _exportService.ParseCsv(bodyText);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] ExternalSync Import parse failed for user={User}", userIdStr);
                return Ok(new SyncResult { Error = ex.Message });
            }

            if (Plugin.Instance?.Repository is not { } repository)
            {
                _logger.LogWarning("[StarTrack] ExternalSync Import: plugin/repository not initialised");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Plugin not ready.");
            }

            var result = new SyncResult();

            foreach (var r in parsed)
            {
                // Try to find the item in the Jellyfin library
                string? itemId;
                try { itemId = _resolver.FindItemId(r); }
                catch (Exception ex) { _logger.LogWarning(ex, "[StarTrack] FindItemId failed for title={Title}", r.Title); itemId = null; }

                if (itemId == null)
                {
                    result.Skipped++;
                    continue;
                }

                // Validate star range (matches RatingController which enforces 0.5–5)
                if (r.Stars < 0.5 || r.Stars > 5)
                {
                    result.Skipped++;
                    continue;
                }

                try
                {
                    await repository.SaveRatingAsync(itemId, userIdStr, userName, r.Stars, null, r.RatedAt)
                        .ConfigureAwait(false);
                    result.Pulled++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[StarTrack] ExternalSync Import: failed to save rating for item={Item}", itemId);
                    result.Error = ex.Message;
                    result.Skipped++;
                }
            }

            _logger.LogInformation("[StarTrack] ExternalSync Import done for user={User}: pulled={P} skipped={S}",
                userIdStr, result.Pulled, result.Skipped);
            return Ok(result);
        }

        // ------------------------------------------------------------------
        // Auth helpers (same approach as RatingController and LetterboxdController)
        // ------------------------------------------------------------------

        private async Task<Guid?> GetCurrentUserIdAsync()
        {
            // Primary: Jellyfin's IAuthorizationContext — most reliable
            try
            {
                var info = await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
                if (info?.UserId != null && info.UserId != Guid.Empty)
                    return info.UserId;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[StarTrack] GetAuthorizationInfo failed; falling back to claims"); }

            // Fallback: parse claims (Jellyfin 10.9–10.11 uses "Jellyfin-UserId")
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
    }
}
