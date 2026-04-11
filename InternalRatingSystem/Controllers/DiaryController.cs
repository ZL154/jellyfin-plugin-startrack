using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
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
    /// Diary endpoints — chronological watch journal with rewatches.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("Plugins/StarTrack")]
    [Produces(MediaTypeNames.Application.Json)]
    public class DiaryController : ControllerBase
    {
        private readonly DiaryRepository _repo;
        private readonly IAuthorizationContext _authContext;
        private readonly ILogger<DiaryController> _logger;

        public DiaryController(IAuthorizationContext authContext, ILogger<DiaryController> logger)
        {
            _repo        = Plugin.Instance!.Diary;
            _authContext = authContext;
            _logger      = logger;
        }

        [HttpGet("MyDiary")]
        [ProducesResponseType(typeof(List<DiaryEntry>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetMyDiary([FromQuery] int limit = 10000)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            return Ok(await _repo.GetEntriesAsync(userId.Value.ToString("N"), limit).ConfigureAwait(false));
        }

        [HttpPost("Diary")]
        [ProducesResponseType(typeof(DiaryEntry), StatusCodes.Status200OK)]
        public async Task<IActionResult> AddDiaryEntry([FromBody] CreateDiaryEntryRequest req)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            if (string.IsNullOrWhiteSpace(req.ItemId)) return BadRequest("itemId is required");

            var entry = new DiaryEntry
            {
                ItemId    = req.ItemId!,
                WatchedAt = req.WatchedAt ?? DateTime.UtcNow,
                Stars     = req.Stars,
                Review    = string.IsNullOrWhiteSpace(req.Review) ? null : req.Review.Trim(),
                Rewatch   = req.Rewatch ?? false
            };
            var saved = await _repo.AddEntryAsync(userId.Value.ToString("N"), entry).ConfigureAwait(false);
            return Ok(saved);
        }

        [HttpDelete("Diary/{entryId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> DeleteDiaryEntry([FromRoute] string entryId)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            await _repo.DeleteEntryAsync(userId.Value.ToString("N"), entryId).ConfigureAwait(false);
            return Ok();
        }

        // ============================== Export CSV =============================== //

        /// <summary>
        /// Exports the current user's StarTrack ratings as a Letterboxd-compatible
        /// CSV so they can re-import it into Letterboxd or use it as a backup.
        /// Columns: Date, Name, Year, Rating (Letterboxd's own export format).
        /// </summary>
        [HttpGet("ExportCsv")]
        [Produces("text/csv")]
        public async Task<IActionResult> ExportCsv()
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            var ratings = await Plugin.Instance!.Repository
                .GetUserRatingsAsync(userId.Value.ToString("N"), 100000)
                .ConfigureAwait(false);

            var libraryManager = HttpContext.RequestServices.GetService(typeof(MediaBrowser.Controller.Library.ILibraryManager)) as MediaBrowser.Controller.Library.ILibraryManager;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Date,Name,Year,Rating");
            foreach (var r in ratings)
            {
                string name = r.ItemId;
                int? year = null;
                try
                {
                    if (libraryManager != null && Guid.TryParse(r.ItemId, out var gid))
                    {
                        var item = libraryManager.GetItemById(gid);
                        if (item != null)
                        {
                            name = item.Name ?? r.ItemId;
                            year = item.ProductionYear;
                        }
                    }
                }
                catch { }

                var date  = r.RatedAt.ToString("yyyy-MM-dd");
                var escapedName = name.Contains(',') || name.Contains('"')
                    ? "\"" + name.Replace("\"", "\"\"") + "\""
                    : name;
                sb.AppendLine($"{date},{escapedName},{year},{r.Stars.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            Response.Headers["Content-Disposition"] = "attachment; filename=\"startrack-ratings.csv\"";
            return File(bytes, "text/csv; charset=utf-8");
        }

        // ============================== Helpers ================================== //

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
                ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            return Guid.TryParse(value, out var id) ? id : null;
        }
    }
}
