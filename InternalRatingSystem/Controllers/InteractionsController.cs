using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.InternalRating.Data;
using Jellyfin.Plugin.InternalRating.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Controllers
{
    /// <summary>
    /// Watchlist, liked films, favorites, and recommendations for the
    /// current user. All endpoints are scoped to the authenticated user.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("Plugins/StarTrack")]
    [Produces(MediaTypeNames.Application.Json)]
    public class InteractionsController : ControllerBase
    {
        private readonly UserInteractionsRepository _repo;
        private readonly RatingRepository _ratingRepo;
        private readonly ILibraryManager _libraryManager;
        private readonly IAuthorizationContext _authContext;
        private readonly ILogger<InteractionsController> _logger;

        public InteractionsController(
            ILibraryManager libraryManager,
            IAuthorizationContext authContext,
            ILogger<InteractionsController> logger)
        {
            _repo         = Plugin.Instance!.Interactions;
            _ratingRepo   = Plugin.Instance!.Repository;
            _libraryManager = libraryManager;
            _authContext  = authContext;
            _logger       = logger;
        }

        // ============================== Status =================================== //

        /// <summary>
        /// Returns whether the current user has watchlisted / liked / favorited
        /// a given item. Used by the rating pill to light up the heart and
        /// bookmark buttons without three separate round-trips.
        /// </summary>
        [HttpGet("Interactions/{itemId}")]
        [ProducesResponseType(typeof(InteractionStatusDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetStatus([FromRoute] string itemId)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            var status = await _repo.GetStatusAsync(userId.Value.ToString("N"), itemId).ConfigureAwait(false);
            return Ok(status);
        }

        // ============================== Watchlist ================================ //

        [HttpGet("MyWatchlist")]
        [ProducesResponseType(typeof(List<WatchlistEntryDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetMyWatchlist()
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            return Ok(await _repo.GetWatchlistAsync(userId.Value.ToString("N")).ConfigureAwait(false));
        }

        /// <summary>
        /// Returns the watchlists of every user on the server, aggregated
        /// per item with a list of which users want each title. Sorted by
        /// number of users descending so popular picks bubble to the top.
        /// </summary>
        [HttpGet("EveryonesWatchlist")]
        [ProducesResponseType(typeof(List<EveryonesWatchlistEntry>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetEveryonesWatchlist()
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            var rows = await _repo.GetEveryonesWatchlistAsync().ConfigureAwait(false);

            // Resolve user IDs to usernames so the UI can show "wanted by
            // Zack, Adam, Mum" instead of GUID strings.
            var um = HttpContext.RequestServices.GetService(typeof(MediaBrowser.Controller.Library.IUserManager))
                     as MediaBrowser.Controller.Library.IUserManager;
            var nameLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                foreach (var uid in r.UserIds)
                {
                    if (nameLookup.ContainsKey(uid)) continue;
                    string name = uid;
                    try
                    {
                        if (Guid.TryParse(uid, out var gid))
                        {
                            var user = um?.GetUserById(gid);
                            if (user != null) name = user.Username;
                        }
                    }
                    catch { }
                    nameLookup[uid] = name;
                }
                r.UserNames = r.UserIds.Select(u => nameLookup.TryGetValue(u, out var n) ? n : u).ToList();
            }

            return Ok(rows);
        }

        [HttpPost("Watchlist/{itemId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> AddToWatchlist([FromRoute] string itemId)
        {
            if (!Guid.TryParse(itemId, out _)) return BadRequest("Invalid item id.");
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            var added = await _repo.AddToWatchlistAsync(userId.Value.ToString("N"), itemId).ConfigureAwait(false);
            _logger.LogInformation("[StarTrack] {User} watchlist +{Item} (added={A})", userId, itemId, added);
            return Ok(new { added });
        }

        [HttpDelete("Watchlist/{itemId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> RemoveFromWatchlist([FromRoute] string itemId)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            await _repo.RemoveFromWatchlistAsync(userId.Value.ToString("N"), itemId).ConfigureAwait(false);
            return Ok();
        }

        // ============================== Liked ==================================== //

        [HttpGet("MyLikes")]
        [ProducesResponseType(typeof(List<LikedEntryDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetMyLikes()
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            return Ok(await _repo.GetLikedAsync(userId.Value.ToString("N")).ConfigureAwait(false));
        }

        [HttpPost("Likes/{itemId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> AddLike([FromRoute] string itemId)
        {
            if (!Guid.TryParse(itemId, out _)) return BadRequest("Invalid item id.");
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            var added = await _repo.AddLikeAsync(userId.Value.ToString("N"), itemId).ConfigureAwait(false);
            return Ok(new { added });
        }

        [HttpDelete("Likes/{itemId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> RemoveLike([FromRoute] string itemId)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            await _repo.RemoveLikeAsync(userId.Value.ToString("N"), itemId).ConfigureAwait(false);
            return Ok();
        }

        // ============================== Favorites ================================ //

        [HttpGet("MyFavorites")]
        [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetMyFavorites()
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            return Ok(await _repo.GetFavoritesAsync(userId.Value.ToString("N")).ConfigureAwait(false));
        }

        [HttpPost("MyFavorites")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> SetMyFavorites([FromBody] SetFavoritesRequest req)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            // Reject oversized payloads + non-Guid ids so user_interactions.json
            // can't be polluted with arbitrary blobs. 100 favorites is generous —
            // the UI surface only displays the top 12.
            var ids = req.ItemIds ?? new List<string>();
            if (ids.Count > 100) return BadRequest("Too many favorites (max 100).");
            foreach (var id in ids)
                if (!Guid.TryParse(id, out _))
                    return BadRequest("Invalid item id in favorites list.");

            await _repo.SetFavoritesAsync(userId.Value.ToString("N"), ids).ConfigureAwait(false);
            return Ok();
        }

        public sealed class SetFavoritesRequest
        {
            public List<string>? ItemIds { get; set; }
        }

        // ============================== Recommendations ========================== //

        /// <summary>
        /// Returns up to 30 films the user hasn't rated yet, weighted by the
        /// genres the user tends to rate highly. Simple and fast: aggregate
        /// each rated film's genres with its stars (as a weight), then pick
        /// unrated films from the user's top-3 genres, sort by community
        /// rating descending.
        /// </summary>
        [HttpGet("Recommendations")]
        [ProducesResponseType(typeof(List<RecommendationDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetRecommendations([FromQuery] int limit = 30)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            var userIdStr = userId.Value.ToString("N");

            var myRatings = await _ratingRepo.GetUserRatingsAsync(userIdStr, 10000).ConfigureAwait(false);
            if (myRatings.Count == 0)
                return Ok(new List<RecommendationDto>());

            // Look up each rated item and accumulate genre weights.
            var genreScore = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var ratedIds = new HashSet<Guid>();
            foreach (var r in myRatings)
            {
                if (!Guid.TryParse(r.ItemId, out var gid)) continue;
                ratedIds.Add(gid);
                BaseItem? item;
                try { item = _libraryManager.GetItemById(gid); } catch { item = null; }
                if (item == null) continue;
                var weight = r.Stars; // 0.5 → 5
                foreach (var g in item.Genres ?? Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(g)) continue;
                    genreScore.TryGetValue(g, out var cur);
                    genreScore[g] = cur + weight;
                }
            }

            if (genreScore.Count == 0)
                return Ok(new List<RecommendationDto>());

            var topGenres = genreScore
                .OrderByDescending(kv => kv.Value)
                .Take(3)
                .Select(kv => kv.Key)
                .ToArray();

            // Pull every movie OR series in at least one of those genres,
            // exclude items the user has already rated, sort by community
            // rating. Series are included because user genre preferences
            // are usually the same across film and TV.
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                Recursive        = true,
                Genres           = topGenres
            };
            IReadOnlyList<BaseItem> candidates;
            try { candidates = _libraryManager.GetItemList(query) ?? (IReadOnlyList<BaseItem>)Array.Empty<BaseItem>(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StarTrack] Recommendations query failed, falling back to ungenred");
                candidates = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                    Recursive = true
                }) ?? (IReadOnlyList<BaseItem>)Array.Empty<BaseItem>();
            }

            // Filter: item must be alive (file or folder exists), not
            // already rated, and have a path. Series have folder paths, so
            // we accept Directory.Exists too — without this every Series
            // recommendation was being silently dropped as a zombie.
            var recs = candidates
                .Where(c =>
                    c != null &&
                    !ratedIds.Contains(c.Id) &&
                    !string.IsNullOrEmpty(c.Path) &&
                    (System.IO.File.Exists(c.Path) || System.IO.Directory.Exists(c.Path))
                )
                .OrderByDescending(c => c.CommunityRating ?? 0)
                .ThenByDescending(c => c.ProductionYear ?? 0)
                .Take(limit)
                .Select(c => new RecommendationDto
                {
                    ItemId = c.Id.ToString("N"),
                    Reason = "Because you rate " + string.Join(", ", (c.Genres ?? Array.Empty<string>()).Intersect(topGenres, StringComparer.OrdinalIgnoreCase).Take(2)) + " highly"
                })
                .ToList();

            return Ok(recs);
        }

        public sealed class RecommendationDto
        {
            [System.Text.Json.Serialization.JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;
            [System.Text.Json.Serialization.JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
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
