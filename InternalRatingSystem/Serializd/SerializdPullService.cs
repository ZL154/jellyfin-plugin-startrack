using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.InternalRating.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Serializd
{
    /// <summary>What an import run did.</summary>
    public sealed class SerializdImportResult
    {
        /// <summary>Ratings created for items the user had not rated.</summary>
        [JsonPropertyName("imported")]  public int Imported { get; set; }

        /// <summary>Existing ratings overwritten with the Serializd value.</summary>
        [JsonPropertyName("updated")]   public int Updated { get; set; }

        /// <summary>Entries whose show, season or episode is not in the library.</summary>
        [JsonPropertyName("unmatched")] public int Unmatched { get; set; }

        /// <summary>Entries with no rating (a plain watch log), or a duplicate of a newer one.</summary>
        [JsonPropertyName("skipped")]   public int Skipped { get; set; }

        /// <summary>Diary pages read.</summary>
        [JsonPropertyName("pages")]     public int Pages { get; set; }

        /// <summary>Fatal error, if the run stopped early.</summary>
        [JsonPropertyName("error")]     public string? Error { get; set; }

        /// <summary>A sample of titles that could not be matched, for the UI.</summary>
        [JsonPropertyName("unmatchedTitles")] public List<string> UnmatchedTitles { get; set; } = new();

        /// <summary>Total ratings written.</summary>
        [JsonPropertyName("totalWritten")] public int TotalWritten => Imported + Updated;
    }

    /// <summary>
    /// Imports a user's Serializd diary into StarTrack.
    ///
    /// NO PASSWORD IS INVOLVED. <c>/api/user/{username}/diary</c> is
    /// unauthenticated — a nonexistent user comes back as an application-level
    /// <c>{"message":"Invalid user"}</c> rather than a 401, which is what proves
    /// the route is public. So the import half works from a username alone,
    /// exactly like Letterboxd's RSS import, and only the push half needs
    /// credentials.
    ///
    /// ==================== RATING SCALE ====================
    /// Serializd sends 1–10 integers. StarTrack stores 0.5–5.0 half-stars, so
    /// everything is HALVED here — the exact inverse of
    /// <see cref="SerializdWriteService.ToSerializdScale"/>. Getting this
    /// backwards silently doubles or halves a user's whole history, which is how
    /// issue #19 happened. Pinned by tests.
    /// =====================================================
    /// </summary>
    public sealed class SerializdPullService
    {
        /// <summary>
        /// Hard ceiling on diary pages. A very old account could otherwise turn
        /// one "Sync now" into hundreds of requests at a stranger's expense.
        /// </summary>
        private const int MaxPages = 100;

        private readonly ILibraryManager _library;
        private readonly IRatingSink _ratings;
        private readonly ILogger<SerializdPullService> _logger;

        public SerializdPullService(
            ILibraryManager library,
            IRatingSink ratings,
            ILogger<SerializdPullService> logger)
        {
            _library = library;
            _ratings = ratings;
            _logger  = logger;
        }

        /// <summary>Serializd's 1–10 integer back to StarTrack half-stars.</summary>
        internal static double FromSerializdScale(int rating) =>
            Math.Clamp(Math.Round(rating / 2.0, 1), 0.5, 5.0);

        /// <summary>
        /// Reads the whole public diary and writes every rating it can place.
        /// Never throws; failures land in <see cref="SerializdImportResult.Error"/>.
        /// </summary>
        public async Task<SerializdImportResult> ImportAsync(
            string userId,
            string userName,
            SerializdUserSettings settings,
            SerializdSession session,
            CancellationToken ct = default,
            int delayMs = 200)
        {
            var result = new SerializdImportResult();

            if (settings.Direction is not (SerializdDirection.ImportOnly or SerializdDirection.TwoWay))
                return result;

            var username = (settings.Username ?? string.Empty).Trim();
            if (username.Length == 0)
            {
                result.Error = "No Serializd username is set. The import needs only a username, not a password.";
                return result;
            }

            try
            {
                // Newest-wins per item, resolved before anything is written. A
                // diary holds one entry per watch, so a rewatched season appears
                // more than once; writing them in arrival order would leave
                // whichever happened to come last, not the most recent opinion.
                var best = new Dictionary<string, (double Stars, string? Review, DateTime At)>();
                var seasonNumbers = new Dictionary<int, Dictionary<int, int>>();

                var page = 1;
                var totalPages = 1;

                while (page <= totalPages && page <= MaxPages)
                {
                    ct.ThrowIfCancellationRequested();

                    using var res = await session
                        .GetAsync($"/user/{Uri.EscapeDataString(username)}/diary?page={page}", ct)
                        .ConfigureAwait(false);

                    if (!res.IsSuccessStatusCode)
                    {
                        // 404 here means "Invalid user" — a typo'd username, not
                        // an outage. Say which so it can be fixed.
                        result.Error = res.StatusCode == System.Net.HttpStatusCode.NotFound
                            ? $"Serializd has no user called \"{username}\"."
                            : $"Serializd returned HTTP {(int)res.StatusCode} for that diary.";
                        return result;
                    }

                    var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (page == 1 &&
                        root.TryGetProperty("totalPages", out var tp) &&
                        tp.ValueKind == JsonValueKind.Number &&
                        tp.TryGetInt32(out var tpv))
                        totalPages = Math.Max(tpv, 1);

                    if (!root.TryGetProperty("reviews", out var reviews) ||
                        reviews.ValueKind != JsonValueKind.Array)
                        break;

                    var count = 0;
                    foreach (var entry in reviews.EnumerateArray())
                    {
                        count++;
                        await AbsorbAsync(entry, session, seasonNumbers, best, result, ct).ConfigureAwait(false);
                    }

                    result.Pages = page;
                    if (count == 0) break;

                    page++;
                    if (delayMs > 0 && page <= totalPages) await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }

                if (totalPages > MaxPages)
                    _logger.LogWarning(
                        "[StarTrack] Serializd diary for {User} has {Total} pages; stopped at the {Max}-page cap, so this import is partial.",
                        username, totalPages, MaxPages);

                foreach (var kv in best)
                {
                    ct.ThrowIfCancellationRequested();

                    var had = await _ratings.GetUserStarsAsync(userId, kv.Key).ConfigureAwait(false);
                    await _ratings.SaveRatingAsync(
                        kv.Key, userId, userName, kv.Value.Stars, kv.Value.Review, kv.Value.At)
                        .ConfigureAwait(false);

                    if (had.HasValue) result.Updated++; else result.Imported++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] Serializd import failed for user {UserId}", userId);
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>Turns one diary entry into a pending write, or counts why it wasn't.</summary>
        private async Task AbsorbAsync(
            JsonElement entry,
            SerializdSession session,
            Dictionary<int, Dictionary<int, int>> seasonNumbers,
            Dictionary<string, (double Stars, string? Review, DateTime At)> best,
            SerializdImportResult result,
            CancellationToken ct)
        {
            var rating = GetInt(entry, "rating");
            // 0 or absent means the entry is a watch log with no opinion
            // attached. Importing it as 0 stars would invent a rating the user
            // never gave.
            if (rating is null or <= 0) { result.Skipped++; return; }

            var showId = GetInt(entry, "showId");
            if (showId is null) { result.Skipped++; return; }

            var seasonId = GetInt(entry, "seasonId");
            var episodeNo = GetInt(entry, "episodeNumber");
            var showName = GetString(entry, "showName") ?? $"TMDb {showId}";

            var series = FindSeries(showId.Value);
            if (series == null) { Unmatched(result, showName); return; }

            BaseItem? target;

            if (seasonId is null)
            {
                target = series;                       // a whole-show review
            }
            else
            {
                var seasonNo = await SeasonNumberAsync(session, showId.Value, seasonId.Value, seasonNumbers, ct)
                    .ConfigureAwait(false);
                if (seasonNo is null) { Unmatched(result, showName); return; }

                var season = FindChild(series, BaseItemKind.Season, seasonNo.Value);
                if (season == null) { Unmatched(result, $"{showName} S{seasonNo}"); return; }

                target = episodeNo is null
                    ? season
                    : FindChild(season, BaseItemKind.Episode, episodeNo.Value);

                if (target == null) { Unmatched(result, $"{showName} S{seasonNo}E{episodeNo}"); return; }
            }

            var when = GetDate(entry, "backdate") ?? GetDate(entry, "dateAdded") ?? DateTime.UtcNow;
            var review = GetString(entry, "reviewText");
            if (string.IsNullOrWhiteSpace(review)) review = null;

            var id = target.Id.ToString("N");
            if (best.TryGetValue(id, out var existing))
            {
                if (existing.At >= when) { result.Skipped++; return; }
                result.Skipped++;   // the older entry we are replacing
            }

            best[id] = (FromSerializdScale(rating.Value), review, when);
        }

        private static void Unmatched(SerializdImportResult r, string title)
        {
            r.Unmatched++;
            if (r.UnmatchedTitles.Count < 100 && !r.UnmatchedTitles.Contains(title))
                r.UnmatchedTitles.Add(title);
        }

        /// <summary>
        /// Serializd addresses a season by its own internal id, which means
        /// nothing to Jellyfin. <c>GET /show/{tmdb}</c> maps it back to a season
        /// NUMBER. Cached per show: a diary is full of repeat visits to the same
        /// series.
        /// </summary>
        internal static async Task<int?> SeasonNumberAsync(
            SerializdSession session, int showTmdbId, int seasonId,
            Dictionary<int, Dictionary<int, int>> cache, CancellationToken ct)
        {
            if (!cache.TryGetValue(showTmdbId, out var map))
            {
                map = new Dictionary<int, int>();
                try
                {
                    using var res = await session.GetAsync($"/show/{showTmdbId}", ct).ConfigureAwait(false);
                    if (res.IsSuccessStatusCode)
                    {
                        var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("seasons", out var seasons) &&
                            seasons.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var sn in seasons.EnumerateArray())
                            {
                                var num = GetInt(sn, "seasonNumber");
                                var sid = GetInt(sn, "id") ?? GetInt(sn, "seasonId");
                                if (num.HasValue && sid.HasValue) map[sid.Value] = num.Value;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { /* leave the map empty; the entry counts as unmatched */ }

                cache[showTmdbId] = map;
            }

            return map.TryGetValue(seasonId, out var n) ? n : null;
        }

        private BaseItem? FindSeries(int tmdbId)
        {
            try
            {
                var q = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Series },
                    HasAnyProviderId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Tmdb"] = tmdbId.ToString(CultureInfo.InvariantCulture)
                    },
                    Limit = 1
                };
                return _library.GetItemList(q).FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StarTrack] Serializd: series lookup failed for TMDb {Id}", tmdbId);
                return null;
            }
        }

        /// <summary>
        /// Finds a season or episode by its number under a parent.
        ///
        /// The number is matched HERE rather than pushed into the query. An
        /// InternalItemsQuery filter that the repository quietly ignores would
        /// combine with a Limit to return an arbitrary child, and this would
        /// then file a rating against the wrong season with nothing to show for
        /// it. A series has a handful of seasons and a season a few dozen
        /// episodes, so filtering in memory costs nothing worth having.
        /// </summary>
        private BaseItem? FindChild(BaseItem parent, BaseItemKind kind, int index)
        {
            try
            {
                var q = new InternalItemsQuery
                {
                    ParentId         = parent.Id,
                    IncludeItemTypes = new[] { kind }
                };
                return _library.GetItemList(q).FirstOrDefault(c => c.IndexNumber == index);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StarTrack] Serializd: {Kind} {Index} lookup failed", kind, index);
                return null;
            }
        }

        // ---- small JSON helpers; the API sends nulls liberally ----

        private static int? GetInt(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
                ? i : null;

        private static string? GetString(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static DateTime? GetDate(JsonElement e, string name)
        {
            var raw = GetString(e, name);
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                       DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
                ? d : null;
        }
    }
}
