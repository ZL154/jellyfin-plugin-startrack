using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.InternalRating.Data;
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
    /// Per-user "Letterboxd-style" profile pages. Members list + lightweight
    /// profile bundle returns instantly; the heavy aggregate Stats (genres,
    /// directors, decades, hours watched, timeline) load lazily via a
    /// dedicated endpoint with an in-memory cache so repeat visits are fast.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("Plugins/StarTrack")]
    [Produces(MediaTypeNames.Application.Json)]
    public class MembersController : ControllerBase
    {
        private readonly UserInteractionsRepository _interactions;
        private readonly RatingRepository _ratingRepo;
        private readonly DiaryRepository _diaryRepo;
        private readonly PrivacyRepository _privacy;
        private readonly FollowsRepository _follows;
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IAuthorizationContext _authContext;
        private readonly ILogger<MembersController> _logger;

        // Per-user cached stats. Key: userId. Cache is invalidated when the
        // user's (ratings.Count + diary.Count) changes — cheap to compute on
        // every request, avoids stale data after a new rating, and means the
        // expensive director/decade walk only happens once per user-state.
        private static readonly ConcurrentDictionary<string, CachedStats> _statsCache = new();
        private sealed class CachedStats
        {
            public long Signature;
            public MemberStatsExtraDto Data = new();
        }

        // Lightweight profile cache. Avoids re-walking favorites + diary +
        // watchlist + likes on every poll. Invalidated by signature change.
        private static readonly ConcurrentDictionary<string, CachedProfile> _profileCache = new();
        private sealed class CachedProfile
        {
            public long Signature;
            public DateTime ExpiresAt;
            public MemberProfileDto Data = new();
        }

        // ── v1.5.13 perf: shared people/meta cache across all users ─────────
        // GetPeople() is the dominant cost in BuildHeavyStats (5–20 ms per
        // item × 300 sample items = up to 6 s of wall-clock). When two users
        // both rated The Godfather, the prior code resolved its cast/crew
        // twice. Caching the slim tuple form globally drops that to one
        // resolution per unique itemId across the whole server.
        //
        // Cache is keyed by item Guid, never invalidated automatically. If a
        // user re-tags a film in Jellyfin the cached people list goes stale,
        // which is acceptable for personal stats display. (Library-wide
        // metadata changes are rare; the worst-case staleness is a stale
        // director name in a top-list.)
        private static readonly ConcurrentDictionary<Guid, List<PersonSlim>> _peopleCache = new();
        private sealed class PersonSlim
        {
            public string Name = string.Empty;
            public string Id   = string.Empty;
            public string Type = string.Empty; // "Director" | "Actor" | other
        }

        private List<PersonSlim> GetCachedPeople(Guid itemGid, BaseItem item)
        {
            if (_peopleCache.TryGetValue(itemGid, out var existing)) return existing;
            var list = new List<PersonSlim>();
            try
            {
                var people = _libraryManager.GetPeople(item);
                if (people != null)
                {
                    foreach (var p in people)
                    {
                        if (p == null || string.IsNullOrEmpty(p.Name)) continue;
                        list.Add(new PersonSlim
                        {
                            Name = p.Name,
                            Id   = p.Id != Guid.Empty ? p.Id.ToString("N") : string.Empty,
                            Type = p.Type.ToString()
                        });
                    }
                }
            }
            catch { /* best-effort; cache empty list so we don't retry */ }
            _peopleCache[itemGid] = list;
            return list;
        }

        public MembersController(
            IUserManager userManager,
            ILibraryManager libraryManager,
            IAuthorizationContext authContext,
            ILogger<MembersController> logger)
        {
            _interactions   = Plugin.Instance!.Interactions;
            _ratingRepo     = Plugin.Instance!.Repository;
            _diaryRepo      = Plugin.Instance!.Diary;
            _privacy        = Plugin.Instance!.Privacy;
            _follows        = Plugin.Instance!.Follows;
            _userManager    = userManager;
            _libraryManager = libraryManager;
            _authContext    = authContext;
            _logger         = logger;
        }

        // ============================ Members list ============================ //

        [HttpGet("Members")]
        [ProducesResponseType(typeof(List<MemberCardDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> ListMembers()
        {
            var meId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            var meStr = meId?.ToString("N");

            var hidden = await _privacy.GetHiddenUserIdsAsync().ConfigureAwait(false);
            var (followingCounts, followerCounts) = await _follows.CountAllAsync().ConfigureAwait(false);
            var myFollowing = meStr != null ? await _follows.GetFollowingAsync(meStr).ConfigureAwait(false) : new List<string>();
            var myFollowingSet = new HashSet<string>(myFollowing, StringComparer.OrdinalIgnoreCase);

            var list = new List<MemberCardDto>();
            var users = _userManager.Users;
            if (users == null) return Ok(list);

            foreach (var u in users)
            {
                if (u == null) continue;
                var idStr = u.Id.ToString("N");
                if (hidden.Contains(idStr) && !string.Equals(idStr, meStr, StringComparison.OrdinalIgnoreCase))
                    continue;

                var ratingsList = await _ratingRepo.GetUserRatingsAsync(idStr, int.MaxValue).ConfigureAwait(false);
                var watchlistN = (await _interactions.GetWatchlistAsync(idStr).ConfigureAwait(false)).Count;
                var likesN     = (await _interactions.GetLikedAsync(idStr).ConfigureAwait(false)).Count;
                var favs       = await _interactions.GetFavoritesAsync(idStr).ConfigureAwait(false);

                followingCounts.TryGetValue(idStr, out var followingN);
                followerCounts.TryGetValue(idStr, out var followerN);

                double avg = 0;
                if (ratingsList.Count > 0)
                {
                    double s = 0;
                    foreach (var r in ratingsList) s += r.Stars;
                    avg = Math.Round(s / ratingsList.Count, 2);
                }

                list.Add(new MemberCardDto
                {
                    Id             = idStr,
                    Name           = u.Username ?? string.Empty,
                    HasImage       = u.ProfileImage?.Path != null,
                    RatingsCount   = ratingsList.Count,
                    WatchlistCount = watchlistN,
                    LikesCount     = likesN,
                    FavoritesCount = favs.Count,
                    FollowingCount = followingN,
                    FollowersCount = followerN,
                    IsFollowing    = myFollowingSet.Contains(idStr),
                    IsSelf         = string.Equals(idStr, meStr, StringComparison.OrdinalIgnoreCase),
                    Top4Ids        = favs.Take(4).ToList(),
                    AvgRating      = avg
                });
            }

            list = list.OrderByDescending(m => m.RatingsCount + m.WatchlistCount + m.LikesCount)
                       .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                       .ToList();

            // Pre-warm the heavy stats cache for visible members in the
            // background. By the time the user clicks into a profile, the
            // stats endpoint will hit the warm cache and return instantly.
            // Skips users whose stats are already cached (cheap fast path).
            foreach (var card in list)
            {
                if (_statsCache.ContainsKey(card.Id)) continue;
                var capturedId = card.Id;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ratingsP = await _ratingRepo.GetUserRatingsAsync(capturedId, int.MaxValue).ConfigureAwait(false);
                        var diaryP   = await _diaryRepo.GetEntriesAsync(capturedId, 100000).ConfigureAwait(false);
                        long sigP = (long)ratingsP.Count * 1_000_003 + diaryP.Count;
                        if (ratingsP.Count > 0) sigP ^= ratingsP[0].RatedAt.Ticks;
                        if (_statsCache.TryGetValue(capturedId, out var existing) && existing.Signature == sigP) return;
                        var fresh = BuildHeavyStats(ratingsP, diaryP);
                        _statsCache[capturedId] = new CachedStats { Signature = sigP, Data = fresh };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[StarTrack] Pre-warm stats failed for {User}", capturedId);
                    }
                });
            }
            return Ok(list);
        }

        // ============================ Profile bundle (light) ================== //
        // Heavy aggregates moved to /Stats. This endpoint is now snappy even
        // for users with hundreds of ratings.
        [HttpGet("Members/{userId}/Profile")]
        [ProducesResponseType(typeof(MemberProfileDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetProfile([FromRoute] string userId)
        {
            if (!Guid.TryParse(userId, out var gid)) return NotFound();
            var user = _userManager.GetUserById(gid);
            if (user == null) return NotFound();
            var idStr = gid.ToString("N");

            var meId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            var meStr = meId?.ToString("N");
            var isSelf = string.Equals(idStr, meStr, StringComparison.OrdinalIgnoreCase);

            var hidden = await _privacy.GetHiddenUserIdsAsync().ConfigureAwait(false);
            if (hidden.Contains(idStr) && !isSelf) return NotFound();

            var ratings   = await _ratingRepo.GetUserRatingsAsync(idStr, int.MaxValue).ConfigureAwait(false);
            var diary     = await _diaryRepo.GetEntriesAsync(idStr, 200).ConfigureAwait(false);
            var watchlist = await _interactions.GetWatchlistAsync(idStr).ConfigureAwait(false);
            var likes     = await _interactions.GetLikedAsync(idStr).ConfigureAwait(false);
            var favs      = await _interactions.GetFavoritesAsync(idStr).ConfigureAwait(false);

            // Cached pieces (Top4 buckets + per-item type map) are independent
            // of the requesting user and are by far the slowest part because
            // they hit libraryManager.GetItemById per id. Cache aggressively
            // and reuse across requests until the user's data changes.
            long sig = (long)ratings.Count * 1_000_003 + diary.Count * 1009 + watchlist.Count * 17 + likes.Count * 13 + favs.Count;
            if (ratings.Count > 0) sig ^= ratings[0].RatedAt.Ticks;

            List<string> top4Movies, top4Series, top4Anime;
            Dictionary<string, string> typeMap;
            // Per-item year + runtime so the All-Ratings sub-page can sort
            // by film year / length without per-row library calls.
            var itemYear    = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            var itemRuntime = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);

            if (_profileCache.TryGetValue(idStr, out var cachedP) && cachedP.Signature == sig && cachedP.ExpiresAt > DateTime.UtcNow)
            {
                top4Movies = cachedP.Data.Top4Movies;
                top4Series = cachedP.Data.Top4Series;
                top4Anime  = cachedP.Data.Top4Anime;
                typeMap    = cachedP.Data.ItemTypes;
                // Reuse meta from previous DTO ratings projection if present.
                foreach (var rr in cachedP.Data.RecentRatings)
                {
                    itemYear[rr.ItemId]    = rr.ProductionYear;
                    itemRuntime[rr.ItemId] = rr.RuntimeMinutes;
                }
            }
            else
            {
                top4Movies = new List<string>();
                top4Series = new List<string>();
                top4Anime  = new List<string>();
                foreach (var fid in favs)
                {
                    var bucket = ClassifyItem(fid);
                    if (bucket == "anime"  && top4Anime.Count  < 4) top4Anime.Add(fid);
                    if (bucket == "movie"  && top4Movies.Count < 4) top4Movies.Add(fid);
                    if (bucket == "series" && top4Series.Count < 4) top4Series.Add(fid);
                }
                typeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                // Single-pass walk that fills typeMap + itemYear + itemRuntime
                // for every item we'll surface (ratings + diary + watchlist + likes).
                void markFull(string iid)
                {
                    if (string.IsNullOrEmpty(iid) || typeMap.ContainsKey(iid)) return;
                    if (Guid.TryParse(iid, out var gid))
                    {
                        BaseItem? bi = null;
                        try { bi = _libraryManager.GetItemById(gid); } catch { }
                        if (bi != null)
                        {
                            typeMap[iid] = HasAnimeMarker(bi)
                                ? "anime"
                                : (bi.GetBaseItemKind() == BaseItemKind.Movie  ? "movie"
                                :  bi.GetBaseItemKind() == BaseItemKind.Series ? "series"
                                :  "other");
                            itemYear[iid]    = bi.ProductionYear;
                            itemRuntime[iid] = bi.RunTimeTicks.HasValue
                                ? (int?)Math.Round(bi.RunTimeTicks.Value / (double)TimeSpan.TicksPerMinute)
                                : null;
                            return;
                        }
                    }
                    typeMap[iid] = "other";
                }
                foreach (var r in ratings)   markFull(r.ItemId);
                foreach (var d in diary)     markFull(d.ItemId);
                foreach (var w in watchlist) markFull(w.ItemId);
                foreach (var l in likes)     markFull(l.ItemId);
            }

            var histogram = new Dictionary<string, int>();
            for (var b = 1; b <= 10; b++)
                histogram[(b * 0.5).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)] = 0;
            double avg = 0;
            if (ratings.Count > 0)
            {
                double sum = 0;
                foreach (var r in ratings)
                {
                    sum += r.Stars;
                    var bucketKey = (Math.Round(r.Stars * 2) / 2.0)
                        .ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                    if (histogram.ContainsKey(bucketKey)) histogram[bucketKey] += 1;
                }
                avg = sum / ratings.Count;
            }

            var followerCount  = (await _follows.GetFollowersAsync(idStr).ConfigureAwait(false)).Count;
            var followingCount = (await _follows.GetFollowingAsync(idStr).ConfigureAwait(false)).Count;
            var hideFollowerCount = await _privacy.IsFollowerCountHiddenAsync(idStr).ConfigureAwait(false);
            var hideFollowing = await _privacy.IsFollowingHiddenAsync(idStr).ConfigureAwait(false);
            var iAmFollowing = meStr != null && await _follows.IsFollowingAsync(meStr, idStr).ConfigureAwait(false);

            var dto = new MemberProfileDto
            {
                Id          = idStr,
                Name        = user.Username ?? string.Empty,
                HasImage    = user.ProfileImage?.Path != null,
                IsSelf      = isSelf,
                Top4        = favs.Take(4).ToList(),
                Top4Movies  = top4Movies,
                Top4Series  = top4Series,
                Top4Anime   = top4Anime,
                Histogram   = histogram,
                Stats       = new MemberStatsDto
                {
                    RatingsCount     = ratings.Count,
                    DiaryCount       = diary.Count,
                    WatchlistCount   = watchlist.Count,
                    LikesCount       = likes.Count,
                    AverageRating    = Math.Round(avg, 2),
                    FollowingCount   = hideFollowing && !isSelf ? -1 : followingCount,
                    FollowersCount   = hideFollowerCount && !isSelf ? -1 : followerCount,
                    HideFollowerCount = hideFollowerCount,
                    HideFollowing    = hideFollowing
                },
                IsFollowing = iAmFollowing,
                ItemTypes = typeMap,
                RecentRatings = await ProjectRatingsWithViewerAsync(idStr, isSelf, meStr, ratings, itemYear, itemRuntime).ConfigureAwait(false),
                Diary = diary
                    .OrderByDescending(e => e.WatchedAt)
                    .Take(200)
                    .Select(e => new MemberDiaryDto { ItemId = e.ItemId, WatchedAt = e.WatchedAt, Stars = e.Stars, Rewatch = e.Rewatch, Review = e.Review })
                    .ToList(),
                Watchlist = watchlist.Select(w => new MemberItemDto { ItemId = w.ItemId, AddedAt = w.AddedAt }).ToList(),
                Likes     = likes.Select(l => new MemberItemDto { ItemId = l.ItemId, AddedAt = l.LikedAt }).ToList()
            };

            // Cache the heavy bits (Top4 buckets + item-type map) that don't
            // depend on the requesting user. 5-minute TTL acts as a backstop;
            // signature change is the primary invalidation trigger.
            if (cachedP == null || cachedP.Signature != sig)
            {
                _profileCache[idStr] = new CachedProfile
                {
                    Signature = sig,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(5),
                    Data = dto
                };
            }
            return Ok(dto);
        }

        // ============================ Stats (lazy, cached) ==================== //

        [HttpGet("Members/{userId}/Stats")]
        [ProducesResponseType(typeof(MemberStatsExtraDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetStats([FromRoute] string userId)
        {
            if (!Guid.TryParse(userId, out var gid)) return NotFound();
            var user = _userManager.GetUserById(gid);
            if (user == null) return NotFound();
            var idStr = gid.ToString("N");

            var meId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            var meStr = meId?.ToString("N");
            var isSelf = string.Equals(idStr, meStr, StringComparison.OrdinalIgnoreCase);

            var hidden = await _privacy.GetHiddenUserIdsAsync().ConfigureAwait(false);
            if (hidden.Contains(idStr) && !isSelf) return NotFound();
            // Privacy: target user has hidden their stats from others.
            if (await _privacy.IsStatsHiddenAsync(idStr).ConfigureAwait(false) && !isSelf)
                return Ok(new MemberStatsExtraDto());

            var ratings = await _ratingRepo.GetUserRatingsAsync(idStr, int.MaxValue).ConfigureAwait(false);
            var diary   = await _diaryRepo.GetEntriesAsync(idStr, 100000).ConfigureAwait(false);

            // Cache signature: cheap (count + sentinel of newest entry's tick)
            // so a new rating invalidates without per-request library walks.
            long sig = (long)ratings.Count * 1_000_003 + diary.Count;
            if (ratings.Count > 0) sig ^= ratings[0].RatedAt.Ticks;

            // Stale-while-revalidate: if we have ANY cached entry, return it
            // immediately so the UI paints fast. If the signature is stale,
            // kick a background rebuild so the next request is fresh.
            if (_statsCache.TryGetValue(idStr, out var cached))
            {
                if (cached.Signature != sig)
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var fresh = BuildHeavyStats(ratings, diary);
                            _statsCache[idStr] = new CachedStats { Signature = sig, Data = fresh };
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[StarTrack] Background stats rebuild failed for {User}", idStr);
                        }
                    });
                }
                return Ok(cached.Data);
            }

            // First-ever request: build synchronously (the caller has to wait
            // exactly once per user; subsequent visits are stale-while-revalidate).
            var data = await Task.Run(() => BuildHeavyStats(ratings, diary)).ConfigureAwait(false);
            _statsCache[idStr] = new CachedStats { Signature = sig, Data = data };
            return Ok(data);
        }

        // ============================ Search Members ========================== //

        [HttpGet("MembersSearch")]
        [ProducesResponseType(typeof(List<MemberCardDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> SearchMembers([FromQuery] string q = "")
        {
            var resp = (await ListMembers().ConfigureAwait(false)) as OkObjectResult;
            var all = (resp?.Value as List<MemberCardDto>) ?? new List<MemberCardDto>();
            if (string.IsNullOrWhiteSpace(q)) return Ok(all);
            var needle = q.Trim();
            var filtered = all.Where(m =>
                m.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
            ).ToList();
            return Ok(filtered);
        }

        // ============================ Privacy ================================= //

        [HttpGet("MyPrivacy")]
        [ProducesResponseType(typeof(PrivacySettings), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetMyPrivacy()
        {
            var me = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (me == null) return Unauthorized();
            return Ok(await _privacy.GetAsync(me.Value.ToString("N")).ConfigureAwait(false));
        }

        public sealed class PrivacyRequest
        {
            [JsonPropertyName("hideFromMembers")]   public bool HideFromMembers { get; set; }
            [JsonPropertyName("hideFollowerCount")] public bool HideFollowerCount { get; set; }
            [JsonPropertyName("hideFollowing")]     public bool HideFollowing { get; set; }
            [JsonPropertyName("hideStats")]         public bool HideStats { get; set; }
            [JsonPropertyName("hideActivity")]      public bool HideActivity { get; set; }
        }

        [HttpPost("MyPrivacy")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<IActionResult> SetMyPrivacy([FromBody] PrivacyRequest req)
        {
            var me = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (me == null) return Unauthorized();
            await _privacy.SetAsync(me.Value.ToString("N"), req.HideFromMembers, req.HideFollowerCount, req.HideFollowing, req.HideStats, req.HideActivity).ConfigureAwait(false);
            return NoContent();
        }

        // ============================ Reviews ================================= //

        [HttpGet("Members/{userId}/Reviews")]
        [ProducesResponseType(typeof(List<MemberDiaryDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetMemberReviews([FromRoute] string userId)
        {
            if (!Guid.TryParse(userId, out var gid)) return NotFound();
            var idStr = gid.ToString("N");
            var meId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            var meStr = meId?.ToString("N");
            var isSelf = string.Equals(idStr, meStr, StringComparison.OrdinalIgnoreCase);

            var hidden = await _privacy.GetHiddenUserIdsAsync().ConfigureAwait(false);
            if (hidden.Contains(idStr) && !isSelf) return NotFound();
            // Reviews are surfaced as part of the user's stats/profile narrative,
            // so they honour HideStats too. Otherwise a user who hid their stats
            // would still leak review text + per-film ratings via this endpoint.
            if (await _privacy.IsStatsHiddenAsync(idStr).ConfigureAwait(false) && !isSelf)
                return Ok(new List<MemberDiaryDto>());

            var diary = await _diaryRepo.GetEntriesAsync(idStr, int.MaxValue).ConfigureAwait(false);
            var rows = diary
                .Where(d => !string.IsNullOrWhiteSpace(d.Review))
                .OrderByDescending(d => d.WatchedAt)
                .Select(d => new MemberDiaryDto
                {
                    ItemId    = d.ItemId,
                    WatchedAt = d.WatchedAt,
                    Stars     = d.Stars,
                    Rewatch   = d.Rewatch,
                    Review    = d.Review
                })
                .ToList();
            return Ok(rows);
        }

        // ============================ Activity feed =========================== //

        public sealed class ActivityEntry
        {
            [JsonPropertyName("kind")]      public string Kind { get; set; } = string.Empty; // "rating" | "diary" | "review"
            [JsonPropertyName("userId")]    public string UserId { get; set; } = string.Empty;
            [JsonPropertyName("userName")]  public string UserName { get; set; } = string.Empty;
            [JsonPropertyName("hasImage")]  public bool HasImage { get; set; }
            [JsonPropertyName("itemId")]    public string ItemId { get; set; } = string.Empty;
            [JsonPropertyName("when")]      public DateTime When { get; set; }
            [JsonPropertyName("stars")]     public double? Stars { get; set; }
            [JsonPropertyName("rewatch")]   public bool Rewatch { get; set; }
            [JsonPropertyName("review")]    public string? Review { get; set; }
        }

        // Activity feed: chronological union of (rating + diary entry) events
        // from the requesting user's followed users (plus self). Capped at
        // 200 items. Respects hideActivity privacy: users who hid their
        // activity never appear in the feed for anyone but themselves.
        [HttpGet("Activity")]
        [ProducesResponseType(typeof(List<ActivityEntry>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetActivity([FromQuery] int limit = 100, [FromQuery] string scope = "following")
        {
            var meId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (meId == null) return Unauthorized();
            var meStr = meId.Value.ToString("N");

            // Resolve the user-id set we'll pull from.
            HashSet<string> userIds;
            if (string.Equals(scope, "everyone", StringComparison.OrdinalIgnoreCase))
            {
                userIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (_userManager.Users != null)
                    foreach (var u in _userManager.Users) if (u != null) userIds.Add(u.Id.ToString("N"));
            }
            else
            {
                var following = await _follows.GetFollowingAsync(meStr).ConfigureAwait(false);
                userIds = new HashSet<string>(following, StringComparer.OrdinalIgnoreCase);
                userIds.Add(meStr); // include self
            }

            var hiddenAct = await _privacy.GetActivityHiddenIdsAsync().ConfigureAwait(false);
            var hiddenMembers = await _privacy.GetHiddenUserIdsAsync().ConfigureAwait(false);

            // Resolve names + image flags once.
            var nameMap = new Dictionary<string, (string name, bool hasImg)>(StringComparer.OrdinalIgnoreCase);
            foreach (var idStr in userIds)
            {
                if (Guid.TryParse(idStr, out var gid))
                {
                    var u = _userManager.GetUserById(gid);
                    if (u != null) nameMap[idStr] = (u.Username ?? string.Empty, u.ProfileImage?.Path != null);
                }
            }

            var entries = new List<ActivityEntry>();
            foreach (var idStr in userIds)
            {
                if (idStr != meStr && hiddenAct.Contains(idStr)) continue;
                if (idStr != meStr && hiddenMembers.Contains(idStr)) continue;
                var (uname, uimg) = nameMap.TryGetValue(idStr, out var nm) ? nm : (string.Empty, false);

                var ratings = await _ratingRepo.GetUserRatingsAsync(idStr, 50).ConfigureAwait(false);
                foreach (var r in ratings)
                {
                    entries.Add(new ActivityEntry
                    {
                        Kind = "rating",
                        UserId = idStr, UserName = uname, HasImage = uimg,
                        ItemId = r.ItemId, When = r.RatedAt, Stars = r.Stars
                    });
                }
                var diary = await _diaryRepo.GetEntriesAsync(idStr, 100).ConfigureAwait(false);
                foreach (var d in diary)
                {
                    entries.Add(new ActivityEntry
                    {
                        Kind = string.IsNullOrWhiteSpace(d.Review) ? "diary" : "review",
                        UserId = idStr, UserName = uname, HasImage = uimg,
                        ItemId = d.ItemId, When = d.WatchedAt, Stars = d.Stars,
                        Rewatch = d.Rewatch, Review = d.Review
                    });
                }
            }

            entries = entries
                .OrderByDescending(e => e.When)
                .Take(Math.Clamp(limit, 1, 500))
                .ToList();
            return Ok(entries);
        }

        // ============================ Compare profiles ======================== //

        public sealed class CompareDto
        {
            [JsonPropertyName("a")] public string A { get; set; } = string.Empty;
            [JsonPropertyName("b")] public string B { get; set; } = string.Empty;
            [JsonPropertyName("aName")] public string AName { get; set; } = string.Empty;
            [JsonPropertyName("bName")] public string BName { get; set; } = string.Empty;
            [JsonPropertyName("similarity")] public double Similarity { get; set; }
            [JsonPropertyName("overlapCount")] public int OverlapCount { get; set; }
            [JsonPropertyName("rows")] public List<CompareRow> Rows { get; set; } = new();
            [JsonPropertyName("aOnlyTop")] public List<string> AOnlyTop { get; set; } = new();
            [JsonPropertyName("bOnlyTop")] public List<string> BOnlyTop { get; set; } = new();

            // Richer compare payload — added v1.5.11.
            [JsonPropertyName("aTotalRated")] public int ATotalRated { get; set; }
            [JsonPropertyName("bTotalRated")] public int BTotalRated { get; set; }
            [JsonPropertyName("aAvg")] public double AAvg { get; set; }
            [JsonPropertyName("bAvg")] public double BAvg { get; set; }
            [JsonPropertyName("aAvgOverlap")] public double AAvgOverlap { get; set; }
            [JsonPropertyName("bAvgOverlap")] public double BAvgOverlap { get; set; }
            [JsonPropertyName("aHist")] public List<int> AHist { get; set; } = new();
            [JsonPropertyName("bHist")] public List<int> BHist { get; set; } = new();
            [JsonPropertyName("bothLoved")] public int BothLoved { get; set; }
            [JsonPropertyName("bothHated")] public int BothHated { get; set; }
            [JsonPropertyName("aHigher")] public int AHigher { get; set; }
            [JsonPropertyName("bHigher")] public int BHigher { get; set; }
            [JsonPropertyName("agree")] public int Agree { get; set; }
            [JsonPropertyName("agreedTop")] public List<string> AgreedTop { get; set; } = new();
        }
        public sealed class CompareRow
        {
            [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;
            [JsonPropertyName("aStars")] public double AStars { get; set; }
            [JsonPropertyName("bStars")] public double BStars { get; set; }
            [JsonPropertyName("delta")]  public double Delta  { get; set; }
        }

        [HttpGet("Compare")]
        [ProducesResponseType(typeof(CompareDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> Compare([FromQuery] string a, [FromQuery] string b)
        {
            if (!Guid.TryParse(a, out var ag) || !Guid.TryParse(b, out var bg)) return NotFound();
            var ua = _userManager.GetUserById(ag); var ub = _userManager.GetUserById(bg);
            if (ua == null || ub == null) return NotFound();

            var aRatings = await _ratingRepo.GetUserRatingsAsync(ag.ToString("N"), int.MaxValue).ConfigureAwait(false);
            var bRatings = await _ratingRepo.GetUserRatingsAsync(bg.ToString("N"), int.MaxValue).ConfigureAwait(false);
            var aMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var bMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in aRatings) aMap[r.ItemId] = r.Stars;
            foreach (var r in bRatings) bMap[r.ItemId] = r.Stars;

            var rows = new List<CompareRow>();
            foreach (var kv in aMap)
            {
                if (bMap.TryGetValue(kv.Key, out var bs))
                {
                    rows.Add(new CompareRow
                    {
                        ItemId = kv.Key, AStars = kv.Value, BStars = bs,
                        Delta = Math.Round(kv.Value - bs, 2)
                    });
                }
            }

            // Pearson correlation for taste similarity.
            double sim = 0;
            if (rows.Count >= 3)
            {
                double meanA = rows.Average(r => r.AStars);
                double meanB = rows.Average(r => r.BStars);
                double num = 0, denA = 0, denB = 0;
                foreach (var r in rows)
                {
                    var da = r.AStars - meanA; var db = r.BStars - meanB;
                    num += da * db; denA += da * da; denB += db * db;
                }
                var den = Math.Sqrt(denA) * Math.Sqrt(denB);
                if (den > 0) sim = Math.Round(num / den, 3);
            }

            // A-only / B-only top picks: items rated >= 4.0 by one and not by the other.
            var aOnly = aRatings.Where(r => !bMap.ContainsKey(r.ItemId) && r.Stars >= 4.0)
                .OrderByDescending(r => r.Stars).Take(8).Select(r => r.ItemId).ToList();
            var bOnly = bRatings.Where(r => !aMap.ContainsKey(r.ItemId) && r.Stars >= 4.0)
                .OrderByDescending(r => r.Stars).Take(8).Select(r => r.ItemId).ToList();

            // ── Richer compare payload (v1.5.11) ────────────────────────────
            // Both 10-bucket histograms cover stars 0.5..5.0 in 0.5 increments.
            int Bucket(double s) {
                var i = (int)Math.Round(s * 2) - 1;          // 0.5 -> 0, 1.0 -> 1, 5.0 -> 9
                if (i < 0) i = 0; if (i > 9) i = 9; return i;
            }
            var aHist = new int[10]; var bHist = new int[10];
            foreach (var r in aRatings) aHist[Bucket(r.Stars)]++;
            foreach (var r in bRatings) bHist[Bucket(r.Stars)]++;

            int bothLoved = 0, bothHated = 0, aHigher = 0, bHigher = 0, agree = 0;
            double overSumA = 0, overSumB = 0;
            foreach (var r in rows)
            {
                overSumA += r.AStars; overSumB += r.BStars;
                if (r.AStars >= 4.0 && r.BStars >= 4.0) bothLoved++;
                if (r.AStars <= 2.5 && r.BStars <= 2.5) bothHated++;
                if (r.Delta > 0)        aHigher++;
                else if (r.Delta < 0)   bHigher++;
                if (Math.Abs(r.Delta) <= 0.5) agree++;
            }

            // Films we agree on AND both loved — best feel-good list.
            // Sort by combined stars desc, then by lowest delta.
            var agreedTop = rows
                .Where(r => r.AStars >= 3.5 && r.BStars >= 3.5 && Math.Abs(r.Delta) <= 0.5)
                .OrderByDescending(r => r.AStars + r.BStars)
                .ThenBy(r => Math.Abs(r.Delta))
                .Take(8)
                .Select(r => r.ItemId)
                .ToList();

            double aAvgAll      = aRatings.Count > 0 ? Math.Round(aRatings.Average(r => r.Stars), 2) : 0;
            double bAvgAll      = bRatings.Count > 0 ? Math.Round(bRatings.Average(r => r.Stars), 2) : 0;
            double aAvgOverlap  = rows.Count > 0     ? Math.Round(overSumA / rows.Count, 2)        : 0;
            double bAvgOverlap  = rows.Count > 0     ? Math.Round(overSumB / rows.Count, 2)        : 0;

            return Ok(new CompareDto
            {
                A = ag.ToString("N"), B = bg.ToString("N"),
                AName = ua.Username ?? "", BName = ub.Username ?? "",
                Similarity = sim,
                OverlapCount = rows.Count,
                Rows = rows.OrderByDescending(r => Math.Abs(r.Delta)).Take(40).ToList(),
                AOnlyTop = aOnly, BOnlyTop = bOnly,
                ATotalRated = aRatings.Count, BTotalRated = bRatings.Count,
                AAvg = aAvgAll, BAvg = bAvgAll,
                AAvgOverlap = aAvgOverlap, BAvgOverlap = bAvgOverlap,
                AHist = aHist.ToList(), BHist = bHist.ToList(),
                BothLoved = bothLoved, BothHated = bothHated,
                AHigher = aHigher, BHigher = bHigher, Agree = agree,
                AgreedTop = agreedTop
            });
        }

        // ============================ Follow graph ============================ //

        [HttpPost("Members/{userId}/Follow")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> Follow([FromRoute] string userId)
        {
            var me = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (me == null) return Unauthorized();
            if (!Guid.TryParse(userId, out var gid)) return NotFound();
            // Verify the target user actually exists, otherwise follows.json
            // accumulates "me -> nonexistent-guid" edges. Also block following
            // a user who hid themselves from the Members list.
            if (_userManager.GetUserById(gid) == null) return NotFound();
            var meStr = me.Value.ToString("N");
            var targetStr = gid.ToString("N");
            if (!string.Equals(meStr, targetStr, StringComparison.OrdinalIgnoreCase))
            {
                var hidden = await _privacy.GetHiddenUserIdsAsync().ConfigureAwait(false);
                if (hidden.Contains(targetStr)) return NotFound();
            }
            var added = await _follows.FollowAsync(meStr, targetStr).ConfigureAwait(false);
            return Ok(new { added });
        }

        [HttpPost("Members/{userId}/Unfollow")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<IActionResult> Unfollow([FromRoute] string userId)
        {
            var me = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (me == null) return Unauthorized();
            await _follows.UnfollowAsync(me.Value.ToString("N"), userId).ConfigureAwait(false);
            return NoContent();
        }

        [HttpGet("Members/{userId}/Followers")]
        [ProducesResponseType(typeof(List<MemberCardDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetFollowers([FromRoute] string userId)
        {
            var me = await GetCurrentUserIdAsync().ConfigureAwait(false);
            var meStr = me?.ToString("N");
            var isSelf = string.Equals(userId, meStr, StringComparison.OrdinalIgnoreCase);
            if (await _privacy.IsFollowerCountHiddenAsync(userId).ConfigureAwait(false) && !isSelf)
                return Ok(new List<MemberCardDto>());

            var ids = await _follows.GetFollowersAsync(userId).ConfigureAwait(false);
            return Ok(await BuildMemberCardsAsync(ids).ConfigureAwait(false));
        }

        [HttpGet("Members/{userId}/Following")]
        [ProducesResponseType(typeof(List<MemberCardDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetFollowing([FromRoute] string userId)
        {
            var me = await GetCurrentUserIdAsync().ConfigureAwait(false);
            var meStr = me?.ToString("N");
            var isSelf = string.Equals(userId, meStr, StringComparison.OrdinalIgnoreCase);
            if (await _privacy.IsFollowingHiddenAsync(userId).ConfigureAwait(false) && !isSelf)
                return Ok(new List<MemberCardDto>());

            var ids = await _follows.GetFollowingAsync(userId).ConfigureAwait(false);
            return Ok(await BuildMemberCardsAsync(ids).ConfigureAwait(false));
        }

        // ============================ Helpers ================================= //

        private async Task<List<MemberCardDto>> BuildMemberCardsAsync(IEnumerable<string> userIds)
        {
            var hidden = await _privacy.GetHiddenUserIdsAsync().ConfigureAwait(false);
            var (followingCounts, followerCounts) = await _follows.CountAllAsync().ConfigureAwait(false);
            var meId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            var meStr = meId?.ToString("N");
            var myFollowing = meStr != null ? await _follows.GetFollowingAsync(meStr).ConfigureAwait(false) : new List<string>();
            var myFollowingSet = new HashSet<string>(myFollowing, StringComparer.OrdinalIgnoreCase);

            var list = new List<MemberCardDto>();
            foreach (var idStr in userIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Guid.TryParse(idStr, out var gid)) continue;
                var u = _userManager.GetUserById(gid);
                if (u == null) continue;
                if (hidden.Contains(idStr) && !string.Equals(idStr, meStr, StringComparison.OrdinalIgnoreCase))
                    continue;

                followingCounts.TryGetValue(idStr, out var followingN);
                followerCounts.TryGetValue(idStr, out var followerN);

                list.Add(new MemberCardDto
                {
                    Id             = idStr,
                    Name           = u.Username ?? string.Empty,
                    HasImage       = u.ProfileImage?.Path != null,
                    FollowingCount = followingN,
                    FollowersCount = followerN,
                    IsFollowing    = myFollowingSet.Contains(idStr),
                    IsSelf         = string.Equals(idStr, meStr, StringComparison.OrdinalIgnoreCase)
                });
            }
            return list;
        }

        private string ClassifyItem(string itemId)
        {
            if (!Guid.TryParse(itemId, out var gid)) return "other";
            BaseItem? item;
            try { item = _libraryManager.GetItemById(gid); } catch { item = null; }
            if (item == null) return "other";

            if (HasAnimeMarker(item)) return "anime";

            return item.GetBaseItemKind() switch
            {
                BaseItemKind.Movie  => "movie",
                BaseItemKind.Series => "series",
                _ => "other"
            };
        }

        private static bool HasAnimeMarker(BaseItem item)
        {
            if (item.Genres != null)
                foreach (var g in item.Genres)
                    if (!string.IsNullOrEmpty(g) && g.IndexOf("anime", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (item.Tags != null)
                foreach (var t in item.Tags)
                    if (!string.IsNullOrEmpty(t) && t.IndexOf("anime", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // Heavy stats: walks every rated/diary item via libraryManager. Runs
        // off the request thread (Task.Run) and gets cached server-side until
        // the user logs a new rating or diary entry.
        private MemberStatsExtraDto BuildHeavyStats(
            List<Models.UserRatingEntry> ratings,
            List<Models.DiaryEntry> diary)
        {
            var dto = new MemberStatsExtraDto();
            var genreCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var directorCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var directorIdByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var actorCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var actorIdByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var decadeCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var decadeStarsSum = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var decadeStarsCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // Per-year aggregation for the Letterboxd-style year-card stack.
            var yearAgg = new Dictionary<int, (int n, double sum, long ticks, double topStars, string? topId, string? topTitle)>();

            BaseItem? longestFilm = null;
            long longestTicks = 0;
            long totalTicks = 0;

            // Per-type breakdown: counts/avg/runtime grouped into movie/series/anime
            var typeStats = new Dictionary<string, (int n, double sum, long ticks, Dictionary<string, int> genres)>();
            void bump(string t, double? stars, long ticks, IList<string>? genres)
            {
                if (!typeStats.TryGetValue(t, out var cur))
                    cur = (0, 0.0, 0L, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
                cur.n += 1;
                if (stars.HasValue) cur.sum += stars.Value;
                cur.ticks += ticks;
                if (genres != null)
                    foreach (var g in genres)
                        if (!string.IsNullOrEmpty(g))
                        { cur.genres.TryGetValue(g, out var c); cur.genres[g] = c + 1; }
                typeStats[t] = cur;
            }

            // Ratings-per-month timeline (UTC year-month buckets).
            var monthCount = new SortedDictionary<string, int>(StringComparer.Ordinal);
            DateTime? firstRating = null;
            DateTime? lastRating = null;
            foreach (var r in ratings)
            {
                var m = r.RatedAt.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
                monthCount.TryGetValue(m, out var c);
                monthCount[m] = c + 1;
                if (firstRating == null || r.RatedAt < firstRating) firstRating = r.RatedAt;
                if (lastRating == null || r.RatedAt > lastRating)   lastRating = r.RatedAt;
            }

            // Days journaled = unique watch dates from diary.
            var distinctWatchDays = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in diary)
                distinctWatchDays.Add(d.WatchedAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

            // Items to walk for genres/directors/decades. Walk only ratings for
            // taste signal — diary alone (no rating) is unreliable for "loved
            // genre" inference.
            var ratingByItem = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in ratings) ratingByItem[r.ItemId] = r.Stars;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in ratings) seen.Add(r.ItemId);
            foreach (var d in diary)   seen.Add(d.ItemId);

            // Hard cap on people lookups to keep latency bounded for power
            // users with thousands of entries. 300 most-recent items by rating
            // captures the dominant tastes without touching every record.
            var topItemsForPeople = ratings
                .OrderByDescending(r => r.RatedAt)
                .Take(300)
                .Select(r => r.ItemId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // v1.5.13: resolve every BaseItem in `seen` ONCE up front. Prior
            // versions called _libraryManager.GetItemById up to 3× per item
            // across the main loop, the year-aggregation prep loop, and the
            // per-year people walk. Build a single Dictionary<Guid, BaseItem>
            // here and reuse it everywhere downstream.
            var itemBy = new Dictionary<Guid, BaseItem>();
            var gidByItemId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var itemId in seen)
            {
                if (!Guid.TryParse(itemId, out var gid0)) continue;
                gidByItemId[itemId] = gid0;
                if (itemBy.ContainsKey(gid0)) continue;
                BaseItem? bi0;
                try { bi0 = _libraryManager.GetItemById(gid0); } catch { bi0 = null; }
                if (bi0 != null) itemBy[gid0] = bi0;
            }

            foreach (var itemId in seen)
            {
                if (!gidByItemId.TryGetValue(itemId, out var gid)) continue;
                if (!itemBy.TryGetValue(gid, out var item)) continue;

                var ticks = item.RunTimeTicks ?? 0;
                totalTicks += ticks;
                if (ticks > longestTicks) { longestTicks = ticks; longestFilm = item; }

                if (item.Genres != null)
                    foreach (var g in item.Genres)
                        if (!string.IsNullOrEmpty(g))
                        {
                            genreCount.TryGetValue(g, out var c);
                            genreCount[g] = c + 1;
                        }

                // Per-type breakdown: classify and accumulate
                var bucket = HasAnimeMarker(item) ? "anime" :
                             item.GetBaseItemKind() == BaseItemKind.Movie ? "movie" :
                             item.GetBaseItemKind() == BaseItemKind.Series ? "series" : "other";
                if (bucket != "other")
                {
                    double? rating = null;
                    if (ratingByItem.TryGetValue(itemId, out var rs)) rating = rs;
                    bump(bucket, rating, ticks, item.Genres);
                }

                if (topItemsForPeople.Contains(itemId))
                {
                    var people = GetCachedPeople(gid, item);
                    foreach (var p in people)
                    {
                        if (string.Equals(p.Type, "Director", StringComparison.OrdinalIgnoreCase))
                        {
                            directorCount.TryGetValue(p.Name, out var c);
                            directorCount[p.Name] = c + 1;
                            if (!string.IsNullOrEmpty(p.Id) && !directorIdByName.ContainsKey(p.Name))
                                directorIdByName[p.Name] = p.Id;
                        }
                        else if (string.Equals(p.Type, "Actor", StringComparison.OrdinalIgnoreCase))
                        {
                            actorCount.TryGetValue(p.Name, out var c);
                            actorCount[p.Name] = c + 1;
                            if (!string.IsNullOrEmpty(p.Id) && !actorIdByName.ContainsKey(p.Name))
                                actorIdByName[p.Name] = p.Id;
                        }
                    }
                }

                // (Year aggregation moved to a single O(N) pass below — no
                // per-item scan needed.)

                var year = item.ProductionYear ?? 0;
                if (year > 0)
                {
                    var decade = (year / 10 * 10) + "s";
                    decadeCount.TryGetValue(decade, out var dc);
                    decadeCount[decade] = dc + 1;
                    if (ratingByItem.TryGetValue(itemId, out var stars))
                    {
                        decadeStarsSum.TryGetValue(decade, out var sum);
                        decadeStarsCount.TryGetValue(decade, out var cnt);
                        decadeStarsSum[decade] = sum + stars;
                        decadeStarsCount[decade] = cnt + 1;
                    }
                }
            }

            // Single-pass year aggregation (replaces the prior O(N²) scan).
            // v1.5.13: reuses the upfront itemBy dictionary instead of doing
            // a second round-trip to libraryManager.GetItemById per item.
            var itemTicks2 = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var itemNames2 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var iid in seen)
            {
                if (!gidByItemId.TryGetValue(iid, out var gid2)) continue;
                if (!itemBy.TryGetValue(gid2, out var bi2)) continue;
                itemTicks2[iid] = bi2.RunTimeTicks ?? 0;
                if (!string.IsNullOrEmpty(bi2.Name)) itemNames2[iid] = bi2.Name;
            }
            // Per-year detail accumulators. Keyed by year, then by name for
            // genres/directors/actors and by half-star bucket for histograms.
            var yearGenres    = new Dictionary<int, Dictionary<string, int>>();
            var yearDirectors = new Dictionary<int, Dictionary<string, int>>();
            var yearActors    = new Dictionary<int, Dictionary<string, int>>();
            var yearHist      = new Dictionary<int, Dictionary<string, int>>();
            var yearItems     = new Dictionary<int, List<string>>(); // for per-year people walk
            // Per-year temporal stats: first/last rated, day-of-week count,
            // single-day peak count.
            var yearFirst     = new Dictionary<int, DateTime>();
            var yearLast      = new Dictionary<int, DateTime>();
            var yearDow       = new Dictionary<int, Dictionary<DayOfWeek, int>>();
            var yearDay       = new Dictionary<int, Dictionary<string, int>>(); // key: yyyy-MM-dd

            foreach (var r in ratings)
            {
                var ry = r.RatedAt.Year;
                yearAgg.TryGetValue(ry, out var ya);
                ya.n += 1;
                ya.sum += r.Stars;
                if (itemTicks2.TryGetValue(r.ItemId, out var rticks)) ya.ticks += rticks;
                if (r.Stars > ya.topStars)
                {
                    ya.topStars = r.Stars;
                    ya.topId = r.ItemId;
                    ya.topTitle = itemNames2.TryGetValue(r.ItemId, out var nm) ? nm : null;
                }
                yearAgg[ry] = ya;

                // Histogram per year.
                if (!yearHist.TryGetValue(ry, out var yh))
                {
                    yh = new Dictionary<string, int>();
                    for (var b = 1; b <= 10; b++) yh[(b * 0.5).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)] = 0;
                    yearHist[ry] = yh;
                }
                var bucketKey = (Math.Round(r.Stars * 2) / 2.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                if (yh.ContainsKey(bucketKey)) yh[bucketKey] += 1;

                // Track items per year so we can do a single people walk below
                // (avoid re-fetching the BaseItem twice per year).
                if (!yearItems.TryGetValue(ry, out var ylist)) { ylist = new List<string>(); yearItems[ry] = ylist; }
                ylist.Add(r.ItemId);

                // First / last rated within year.
                if (!yearFirst.TryGetValue(ry, out var f) || r.RatedAt < f) yearFirst[ry] = r.RatedAt;
                if (!yearLast.TryGetValue(ry, out var l) || r.RatedAt > l)  yearLast[ry]  = r.RatedAt;
                // Day-of-week count (Sunday..Saturday).
                if (!yearDow.TryGetValue(ry, out var dowMap)) { dowMap = new Dictionary<DayOfWeek, int>(); yearDow[ry] = dowMap; }
                dowMap.TryGetValue(r.RatedAt.DayOfWeek, out var dc); dowMap[r.RatedAt.DayOfWeek] = dc + 1;
                // Single-day peak.
                var dayKey = r.RatedAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                if (!yearDay.TryGetValue(ry, out var dMap)) { dMap = new Dictionary<string, int>(); yearDay[ry] = dMap; }
                dMap.TryGetValue(dayKey, out var dayC); dMap[dayKey] = dayC + 1;
            }

            // Walk per-year items once for genres/directors/actors. Bounded by
            // the global topItemsForPeople sample so we never blow out the
            // people-fetch budget on long-tenured users.
            foreach (var yk in yearItems)
            {
                var ry = yk.Key;
                if (!yearGenres.ContainsKey(ry))    yearGenres[ry]    = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (!yearDirectors.ContainsKey(ry)) yearDirectors[ry] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (!yearActors.ContainsKey(ry))    yearActors[ry]    = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var iid in yk.Value)
                {
                    if (!gidByItemId.TryGetValue(iid, out var gid3)) continue;
                    if (!itemBy.TryGetValue(gid3, out var bi3)) continue;
                    if (bi3.Genres != null)
                        foreach (var g in bi3.Genres)
                            if (!string.IsNullOrEmpty(g))
                            { yearGenres[ry].TryGetValue(g, out var c); yearGenres[ry][g] = c + 1; }
                    if (topItemsForPeople.Contains(iid))
                    {
                        var pp = GetCachedPeople(gid3, bi3);
                        foreach (var p in pp)
                        {
                            if (string.Equals(p.Type, "Director", StringComparison.OrdinalIgnoreCase))
                            {
                                yearDirectors[ry].TryGetValue(p.Name, out var c);
                                yearDirectors[ry][p.Name] = c + 1;
                            }
                            else if (string.Equals(p.Type, "Actor", StringComparison.OrdinalIgnoreCase))
                            {
                                yearActors[ry].TryGetValue(p.Name, out var c);
                                yearActors[ry][p.Name] = c + 1;
                            }
                        }
                    }
                }
            }

            // Calendar heatmap — last 365 days, daily count of (rating
            // events ∪ diary watches) so the chart picks up rewatches too.
            var heatmap = new Dictionary<string, int>();
            var heatStart = DateTime.UtcNow.Date.AddDays(-364);
            void heatAdd(DateTime t)
            {
                var d = t.ToUniversalTime().Date;
                if (d < heatStart) return;
                var k = d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                heatmap.TryGetValue(k, out var c); heatmap[k] = c + 1;
            }
            foreach (var r in ratings) heatAdd(r.RatedAt);
            foreach (var d in diary)   heatAdd(d.WatchedAt);
            dto.CalendarHeatmap = heatmap;

            // Most rewatched: aggregate diary entries with rewatch=true.
            // Each diary row = one watch; counting the rewatch=true ones
            // gives the rewatch tally per film. Top 8.
            var rewatchCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in diary)
            {
                if (!d.Rewatch || string.IsNullOrEmpty(d.ItemId)) continue;
                rewatchCount.TryGetValue(d.ItemId, out var c);
                rewatchCount[d.ItemId] = c + 1;
            }
            dto.RewatchTop = rewatchCount
                .OrderByDescending(kv => kv.Value)
                .Take(8)
                .Select(kv => new RewatchEntry { ItemId = kv.Key, RewatchCount = kv.Value })
                .ToList();

            // On this day: ratings/diary on the same MM-DD as today (any year
            // before this one). Capped at 12.
            var todayMonth = DateTime.UtcNow.Month;
            var todayDay = DateTime.UtcNow.Day;
            var thisYearN = DateTime.UtcNow.Year;
            var seenOtd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var otd = new List<MemberDiaryDto>();
            foreach (var d in diary)
            {
                if (d.WatchedAt.Month == todayMonth && d.WatchedAt.Day == todayDay && d.WatchedAt.Year < thisYearN)
                {
                    var key = d.ItemId + "|" + d.WatchedAt.ToString("yyyy-MM-dd");
                    if (!seenOtd.Add(key)) continue;
                    otd.Add(new MemberDiaryDto
                    {
                        ItemId = d.ItemId, WatchedAt = d.WatchedAt,
                        Stars = d.Stars, Rewatch = d.Rewatch, Review = d.Review
                    });
                }
            }
            foreach (var r in ratings)
            {
                if (r.RatedAt.Month == todayMonth && r.RatedAt.Day == todayDay && r.RatedAt.Year < thisYearN)
                {
                    var key = r.ItemId + "|" + r.RatedAt.ToString("yyyy-MM-dd");
                    if (!seenOtd.Add(key)) continue;
                    otd.Add(new MemberDiaryDto
                    {
                        ItemId = r.ItemId, WatchedAt = r.RatedAt,
                        Stars = r.Stars, Rewatch = false
                    });
                }
            }
            dto.OnThisDay = otd.OrderByDescending(e => e.WatchedAt).Take(12).ToList();

            dto.HoursWatched      = Math.Round(totalTicks / (double)TimeSpan.TicksPerHour, 1);
            dto.LongestFilmTitle  = longestFilm?.Name ?? "";
            dto.LongestFilmHours  = longestTicks > 0 ? Math.Round(longestTicks / (double)TimeSpan.TicksPerHour, 2) : 0;
            dto.FirstRatingDate   = firstRating;
            dto.LastRatingDate    = lastRating;
            dto.DaysJournaled     = distinctWatchDays.Count;
            dto.RatingsThisYear   = ratings.Count(r => r.RatedAt.Year == DateTime.UtcNow.Year);

            dto.TopGenres = genreCount
                .OrderByDescending(kv => kv.Value)
                .Take(8)
                .Select(kv => new StatCount { Name = kv.Key, Count = kv.Value })
                .ToList();
            dto.TopDirectors = directorCount
                .OrderByDescending(kv => kv.Value)
                .Take(8)
                .Select(kv => new StatCount
                {
                    Name = kv.Key,
                    Count = kv.Value,
                    PersonId = directorIdByName.TryGetValue(kv.Key, out var did) ? did : null
                })
                .ToList();
            dto.TopActors = actorCount
                .OrderByDescending(kv => kv.Value)
                .Take(12)
                .Select(kv => new StatCount
                {
                    Name = kv.Key,
                    Count = kv.Value,
                    PersonId = actorIdByName.TryGetValue(kv.Key, out var aid) ? aid : null
                })
                .ToList();
            dto.DecadeBreakdown = decadeCount
                .OrderBy(kv => kv.Key)
                .Select(kv => new StatCount { Name = kv.Key, Count = kv.Value })
                .ToList();
            dto.MonthTimeline = monthCount
                .Select(kv => new StatCount { Name = kv.Key, Count = kv.Value })
                .ToList();
            // Year breakdown: newest year first. Letterboxd's profile shows
            // year cards top-down, so the most recent appears first.
            dto.YearBreakdown = yearAgg
                .OrderByDescending(kv => kv.Key)
                .Select(kv =>
                {
                    var y = kv.Key;
                    var ys = new YearStat
                    {
                        Year = y,
                        RatingsCount = kv.Value.n,
                        AvgRating = kv.Value.n > 0 ? Math.Round(kv.Value.sum / kv.Value.n, 2) : 0,
                        HoursWatched = Math.Round(kv.Value.ticks / (double)TimeSpan.TicksPerHour, 1),
                        TopFilmId = kv.Value.topId,
                        TopFilmTitle = kv.Value.topTitle,
                        TopFilmStars = kv.Value.topStars,
                        Histogram = yearHist.TryGetValue(y, out var yh) ? yh : new Dictionary<string, int>()
                    };
                    if (yearGenres.TryGetValue(y, out var yg))
                        ys.TopGenres = yg.OrderByDescending(p => p.Value).Take(5)
                            .Select(p => new StatCount { Name = p.Key, Count = p.Value }).ToList();
                    if (yearDirectors.TryGetValue(y, out var yd))
                        ys.TopDirectors = yd.OrderByDescending(p => p.Value).Take(5)
                            .Select(p => new StatCount {
                                Name = p.Key, Count = p.Value,
                                PersonId = directorIdByName.TryGetValue(p.Key, out var did2) ? did2 : null
                            }).ToList();
                    if (yearActors.TryGetValue(y, out var ya2))
                        ys.TopActors = ya2.OrderByDescending(p => p.Value).Take(8)
                            .Select(p => new StatCount {
                                Name = p.Key, Count = p.Value,
                                PersonId = actorIdByName.TryGetValue(p.Key, out var aid2) ? aid2 : null
                            }).ToList();

                    if (yearFirst.TryGetValue(y, out var fd)) ys.FirstWatched = fd;
                    if (yearLast.TryGetValue(y, out var ld))  ys.LastWatched  = ld;

                    if (yearDow.TryGetValue(y, out var dowM) && dowM.Count > 0)
                    {
                        var topDow = dowM.OrderByDescending(kv => kv.Value).First();
                        ys.MostWatchedDay      = topDow.Key.ToString();
                        ys.MostWatchedDayCount = topDow.Value;
                    }
                    if (yearDay.TryGetValue(y, out var dayM) && dayM.Count > 0)
                    {
                        var topDay = dayM.OrderByDescending(kv => kv.Value).First();
                        if (DateTime.TryParse(topDay.Key, System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                                out var pd))
                        {
                            ys.PeakDate      = pd;
                            ys.PeakDateCount = topDay.Value;
                        }
                    }
                    return ys;
                })
                .ToList();

            string bestDecade = "";
            double bestAvg = 0;
            foreach (var kv in decadeStarsCount)
            {
                if (kv.Value < 3) continue;
                var a = decadeStarsSum[kv.Key] / kv.Value;
                if (a > bestAvg) { bestAvg = a; bestDecade = kv.Key; }
            }
            dto.HighestRatedDecade = bestDecade;
            dto.HighestRatedDecadeAvg = Math.Round(bestAvg, 2);

            // Project per-media-type aggregates. Always emit movie/series/anime
            // (with zero counts if empty) so the UI can render an empty-state
            // tab cleanly without conditional checks.
            foreach (var t in new[] { "movie", "series", "anime" })
            {
                typeStats.TryGetValue(t, out var s);
                var topG = s.genres == null
                    ? new List<StatCount>()
                    : s.genres.OrderByDescending(kv => kv.Value).Take(5)
                        .Select(kv => new StatCount { Name = kv.Key, Count = kv.Value }).ToList();
                var avgRated = s.n > 0 ? Math.Round(s.sum / Math.Max(1, s.n), 2) : 0;
                dto.MediaBreakdown.Add(new MediaTypeBreakdown
                {
                    Type = t,
                    RatingsCount = s.n,
                    AvgRating = avgRated,
                    HoursWatched = Math.Round(s.ticks / (double)TimeSpan.TicksPerHour, 1),
                    TopGenres = topG
                });
            }
            return dto;
        }

        // Build the projection of the profile-owner's ratings list with the
        // viewer's own rating per item attached. When the viewer is the
        // profile owner, MyStars equals Stars (so the sort works the same).
        private async Task<List<MemberRatingDto>> ProjectRatingsWithViewerAsync(
            string profileUserId, bool isSelf, string? viewerId,
            List<Models.UserRatingEntry> ratings,
            Dictionary<string, int?> itemYear,
            Dictionary<string, int?> itemRuntime)
        {
            // Build a fast lookup of the viewer's ratings keyed by itemId so we
            // don't loop the rating store per row.
            Dictionary<string, double>? viewerByItem = null;
            if (!string.IsNullOrEmpty(viewerId) && !isSelf)
            {
                var viewerRatings = await _ratingRepo.GetUserRatingsAsync(viewerId, int.MaxValue).ConfigureAwait(false);
                viewerByItem = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var vr in viewerRatings) viewerByItem[vr.ItemId] = vr.Stars;
            }

            return ratings
                .OrderByDescending(r => r.RatedAt)
                .Select(r => new MemberRatingDto
                {
                    ItemId = r.ItemId,
                    Stars = r.Stars,
                    RatedAt = r.RatedAt,
                    ProductionYear = itemYear.TryGetValue(r.ItemId, out var yy) ? yy : null,
                    RuntimeMinutes = itemRuntime.TryGetValue(r.ItemId, out var rt) ? rt : null,
                    MyStars = isSelf
                        ? (double?)r.Stars
                        : (viewerByItem != null && viewerByItem.TryGetValue(r.ItemId, out var ms) ? (double?)ms : null)
                })
                .ToList();
        }

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

        // -- DTOs ---------------------------------------------------------- //

        public sealed class MemberCardDto
        {
            [JsonPropertyName("id")]             public string Id   { get; set; } = string.Empty;
            [JsonPropertyName("name")]           public string Name { get; set; } = string.Empty;
            [JsonPropertyName("hasImage")]       public bool HasImage { get; set; }
            [JsonPropertyName("ratingsCount")]   public int RatingsCount   { get; set; }
            [JsonPropertyName("watchlistCount")] public int WatchlistCount { get; set; }
            [JsonPropertyName("likesCount")]     public int LikesCount     { get; set; }
            [JsonPropertyName("favoritesCount")] public int FavoritesCount { get; set; }
            [JsonPropertyName("followingCount")] public int FollowingCount { get; set; }
            [JsonPropertyName("followersCount")] public int FollowersCount { get; set; }
            [JsonPropertyName("isFollowing")]    public bool IsFollowing { get; set; }
            [JsonPropertyName("isSelf")]         public bool IsSelf { get; set; }
            // Top 4 favorite item ids so the Members tab can preview each
            // member's taste with thumbnails next to the avatar.
            [JsonPropertyName("top4Ids")]        public List<string> Top4Ids { get; set; } = new();
            [JsonPropertyName("avgRating")]      public double AvgRating { get; set; }
        }

        public sealed class MemberStatsDto
        {
            [JsonPropertyName("ratingsCount")]     public int RatingsCount { get; set; }
            [JsonPropertyName("diaryCount")]       public int DiaryCount { get; set; }
            [JsonPropertyName("watchlistCount")]   public int WatchlistCount { get; set; }
            [JsonPropertyName("likesCount")]       public int LikesCount { get; set; }
            [JsonPropertyName("averageRating")]    public double AverageRating { get; set; }
            [JsonPropertyName("followingCount")]   public int FollowingCount { get; set; } // -1 if hidden
            [JsonPropertyName("followersCount")]   public int FollowersCount { get; set; } // -1 if hidden
            [JsonPropertyName("hideFollowerCount")] public bool HideFollowerCount { get; set; }
            [JsonPropertyName("hideFollowing")]    public bool HideFollowing { get; set; }
        }

        public sealed class MediaTypeBreakdown
        {
            [JsonPropertyName("type")]          public string Type { get; set; } = string.Empty;
            [JsonPropertyName("ratingsCount")]  public int RatingsCount { get; set; }
            [JsonPropertyName("avgRating")]     public double AvgRating { get; set; }
            [JsonPropertyName("hoursWatched")]  public double HoursWatched { get; set; }
            [JsonPropertyName("topGenres")]     public List<StatCount> TopGenres { get; set; } = new();
        }

        public sealed class MemberStatsExtraDto
        {
            [JsonPropertyName("hoursWatched")]      public double HoursWatched { get; set; }
            [JsonPropertyName("longestFilmTitle")]  public string LongestFilmTitle { get; set; } = string.Empty;
            [JsonPropertyName("longestFilmHours")]  public double LongestFilmHours { get; set; }
            [JsonPropertyName("firstRatingDate")]   public DateTime? FirstRatingDate { get; set; }
            [JsonPropertyName("lastRatingDate")]    public DateTime? LastRatingDate { get; set; }
            [JsonPropertyName("daysJournaled")]     public int DaysJournaled { get; set; }
            [JsonPropertyName("ratingsThisYear")]   public int RatingsThisYear { get; set; }
            [JsonPropertyName("topGenres")]         public List<StatCount> TopGenres { get; set; } = new();
            [JsonPropertyName("topDirectors")]      public List<StatCount> TopDirectors { get; set; } = new();
            [JsonPropertyName("topActors")]         public List<StatCount> TopActors { get; set; } = new();
            [JsonPropertyName("decadeBreakdown")]   public List<StatCount> DecadeBreakdown { get; set; } = new();
            [JsonPropertyName("monthTimeline")]     public List<StatCount> MonthTimeline { get; set; } = new();
            [JsonPropertyName("yearBreakdown")]     public List<YearStat> YearBreakdown { get; set; } = new();
            [JsonPropertyName("highestRatedDecade")]    public string HighestRatedDecade { get; set; } = string.Empty;
            [JsonPropertyName("highestRatedDecadeAvg")] public double HighestRatedDecadeAvg { get; set; }
            [JsonPropertyName("mediaBreakdown")]    public List<MediaTypeBreakdown> MediaBreakdown { get; set; } = new();
            // Calendar heatmap: yyyy-MM-dd -> count, last 365 days only.
            [JsonPropertyName("calendarHeatmap")]   public Dictionary<string, int> CalendarHeatmap { get; set; } = new();
            // Top rewatched films (count of rewatch=true diary entries per item).
            [JsonPropertyName("rewatchTop")]        public List<RewatchEntry> RewatchTop { get; set; } = new();
            // "On this day" — films watched on the same MM-DD in past years.
            [JsonPropertyName("onThisDay")]         public List<MemberDiaryDto> OnThisDay { get; set; } = new();
        }

        public sealed class RewatchEntry
        {
            [JsonPropertyName("itemId")]      public string ItemId { get; set; } = string.Empty;
            [JsonPropertyName("rewatchCount")] public int RewatchCount { get; set; }
        }

        public sealed class StatCount
        {
            [JsonPropertyName("name")]     public string Name { get; set; } = string.Empty;
            [JsonPropertyName("count")]    public int Count { get; set; }
            // PersonId set for top-directors / top-actors so the UI can
            // build avatar URLs (/Items/{personId}/Images/Primary).
            [JsonPropertyName("personId")] public string? PersonId { get; set; }
        }

        public sealed class YearStat
        {
            [JsonPropertyName("year")]         public int Year { get; set; }
            [JsonPropertyName("ratingsCount")] public int RatingsCount { get; set; }
            [JsonPropertyName("avgRating")]    public double AvgRating { get; set; }
            [JsonPropertyName("hoursWatched")] public double HoursWatched { get; set; }
            [JsonPropertyName("topFilmId")]    public string? TopFilmId { get; set; }
            [JsonPropertyName("topFilmTitle")] public string? TopFilmTitle { get; set; }
            [JsonPropertyName("topFilmStars")] public double TopFilmStars { get; set; }
            // Per-year detail (revealed when a year card is expanded).
            [JsonPropertyName("topGenres")]    public List<StatCount> TopGenres { get; set; } = new();
            [JsonPropertyName("topDirectors")] public List<StatCount> TopDirectors { get; set; } = new();
            [JsonPropertyName("topActors")]    public List<StatCount> TopActors { get; set; } = new();
            [JsonPropertyName("histogram")]    public Dictionary<string, int> Histogram { get; set; } = new();
            // First / last rated dates within this year (UTC). Most-watched
            // day-of-week (e.g. "Saturday") + how many films on that DOW.
            // Peak single-day date + count of films rated on that date.
            [JsonPropertyName("firstWatched")]        public DateTime? FirstWatched { get; set; }
            [JsonPropertyName("lastWatched")]         public DateTime? LastWatched { get; set; }
            [JsonPropertyName("mostWatchedDay")]      public string MostWatchedDay { get; set; } = string.Empty;
            [JsonPropertyName("mostWatchedDayCount")] public int MostWatchedDayCount { get; set; }
            [JsonPropertyName("peakDate")]            public DateTime? PeakDate { get; set; }
            [JsonPropertyName("peakDateCount")]       public int PeakDateCount { get; set; }
        }

        public sealed class MemberRatingDto
        {
            [JsonPropertyName("itemId")]         public string ItemId { get; set; } = string.Empty;
            [JsonPropertyName("stars")]          public double Stars { get; set; }
            [JsonPropertyName("ratedAt")]        public DateTime RatedAt { get; set; }
            // For Letterboxd-style sort options on the All Ratings page.
            [JsonPropertyName("productionYear")] public int? ProductionYear { get; set; }
            [JsonPropertyName("runtimeMinutes")] public int? RuntimeMinutes { get; set; }
            // Viewer's own rating for the same item (when viewing someone
            // else's profile). null = viewer hasn't rated this film. Lets
            // the All-Ratings page sort by "My rating" alongside the
            // profile-owner's rating.
            [JsonPropertyName("myStars")]        public double? MyStars { get; set; }
        }

        public sealed class MemberDiaryDto
        {
            [JsonPropertyName("itemId")]    public string ItemId { get; set; } = string.Empty;
            [JsonPropertyName("watchedAt")] public DateTime WatchedAt { get; set; }
            [JsonPropertyName("stars")]     public double? Stars { get; set; }
            [JsonPropertyName("rewatch")]   public bool Rewatch { get; set; }
            [JsonPropertyName("review")]    public string? Review { get; set; }
        }

        public sealed class MemberItemDto
        {
            [JsonPropertyName("itemId")]  public string ItemId { get; set; } = string.Empty;
            [JsonPropertyName("addedAt")] public DateTime AddedAt { get; set; }
        }

        public sealed class MemberProfileDto
        {
            [JsonPropertyName("id")]            public string Id { get; set; } = string.Empty;
            [JsonPropertyName("name")]          public string Name { get; set; } = string.Empty;
            [JsonPropertyName("hasImage")]      public bool HasImage { get; set; }
            [JsonPropertyName("isSelf")]        public bool IsSelf { get; set; }
            [JsonPropertyName("isFollowing")]   public bool IsFollowing { get; set; }
            [JsonPropertyName("top4")]          public List<string> Top4 { get; set; } = new();
            [JsonPropertyName("top4Movies")]    public List<string> Top4Movies { get; set; } = new();
            [JsonPropertyName("top4Series")]    public List<string> Top4Series { get; set; } = new();
            [JsonPropertyName("top4Anime")]     public List<string> Top4Anime { get; set; } = new();
            [JsonPropertyName("histogram")]     public Dictionary<string, int> Histogram { get; set; } = new();
            [JsonPropertyName("stats")]         public MemberStatsDto Stats { get; set; } = new();
            [JsonPropertyName("recentRatings")] public List<MemberRatingDto> RecentRatings { get; set; } = new();
            [JsonPropertyName("diary")]         public List<MemberDiaryDto> Diary { get; set; } = new();
            [JsonPropertyName("watchlist")]     public List<MemberItemDto> Watchlist { get; set; } = new();
            [JsonPropertyName("likes")]         public List<MemberItemDto> Likes { get; set; } = new();
            // Per-item-id type lookup ('movie' / 'series' / 'anime' / 'other')
            // for the diary/watchlist/likes filter pills.
            [JsonPropertyName("itemTypes")]     public Dictionary<string, string> ItemTypes { get; set; } = new();
        }
    }
}
