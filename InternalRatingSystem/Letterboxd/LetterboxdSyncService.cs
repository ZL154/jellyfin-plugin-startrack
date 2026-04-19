using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.InternalRating.Data;
using Jellyfin.Plugin.InternalRating.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Letterboxd
{
    /// <summary>
    /// Core Letterboxd sync logic: parses CSV exports and RSS feeds, matches
    /// films against the Jellyfin library by (title, year), and writes the
    /// resulting ratings through the existing RatingRepository.
    /// </summary>
    public sealed class LetterboxdSyncService
    {
        // Letterboxd's anti-bot returns 403 to the default System.Net.Http
        // User-Agent on the watchlist RSS and likes endpoints (the favourites
        // page is more lenient). Set a real browser UA so we get HTML/RSS
        // back instead of a 403. Also send a sane Accept header so the
        // RSS endpoint returns XML instead of redirecting to HTML.
        private static readonly HttpClient _http = CreateHttp();
        private static HttpClient CreateHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml,application/xml,text/html;q=0.9,*/*;q=0.8");
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            return http;
        }

        private readonly RatingRepository _ratingRepo;
        private readonly LetterboxdSettingsRepository _settingsRepo;
        private readonly UserInteractionsRepository _interactions;
        private readonly DiaryRepository _diaryRepo;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<LetterboxdSyncService> _logger;

        public LetterboxdSyncService(
            RatingRepository ratingRepo,
            LetterboxdSettingsRepository settingsRepo,
            UserInteractionsRepository interactions,
            DiaryRepository diaryRepo,
            ILibraryManager libraryManager,
            ILogger<LetterboxdSyncService> logger)
        {
            _ratingRepo     = ratingRepo;
            _settingsRepo   = settingsRepo;
            _interactions   = interactions;
            _diaryRepo      = diaryRepo;
            _libraryManager = libraryManager;
            _logger         = logger;
        }

        // ============================================================= //
        // CSV IMPORT
        // ============================================================= //

        /// <summary>
        /// Imports ratings from a Letterboxd ratings.csv file.
        /// Expected columns: Date, Name, Year, Letterboxd URI, Rating.
        /// </summary>
        public Task<LetterboxdImportResult> ImportCsvAsync(
            string userId, string userName, Stream csvStream)
        {
            return ImportCsvAsync(userId, userName, csvStream, null);
        }

        /// <summary>
        /// Overload that accepts a pre-built MovieLookup so the ZIP
        /// importer can reuse the same library index across ratings,
        /// watchlist, and likes in a single request.
        /// </summary>
        internal async Task<LetterboxdImportResult> ImportCsvAsync(
            string userId, string userName, Stream csvStream, MovieLookup? prebuiltLookup)
        {
            var result = new LetterboxdImportResult();
            List<Dictionary<string, string>> rows;

            try
            {
                using var reader = new StreamReader(csvStream, Encoding.UTF8);
                var content = await reader.ReadToEndAsync().ConfigureAwait(false);
                rows = ParseCsv(content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] Failed to read Letterboxd CSV");
                result.Error = "Could not read CSV file: " + ex.Message;
                return result;
            }

            _logger.LogInformation("[StarTrack] Letterboxd CSV import: {N} rows", rows.Count);

            // Build the in-memory movie lookup ONCE per import instead of
            // running a separate library query per row. Much faster, and
            // avoids any quirks with InternalItemsQuery.Years filtering.
            // If the ZIP importer already built a lookup for this request,
            // reuse it so we don't hit the library multiple times.
            var lookup = prebuiltLookup ?? BuildMovieLookup();
            result.LibraryMovieCount = lookup.TotalMovies;
            _logger.LogInformation("[StarTrack] Letterboxd import: library has {N} movies indexed for matching", lookup.TotalMovies);

            foreach (var row in rows)
            {
                var name   = GetCol(row, "Name", "Title", "Film");
                var yearS  = GetCol(row, "Year");
                var rating = GetCol(row, "Rating");
                var dateS  = GetCol(row, "Date");

                if (string.IsNullOrWhiteSpace(name))
                {
                    result.Skipped++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(rating) || !double.TryParse(rating, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var stars))
                {
                    result.Skipped++;
                    continue;
                }

                // Clamp to StarTrack's 0.5..5 scale
                if (stars < 0.5) stars = 0.5;
                if (stars > 5)   stars = 5;

                int? year = null;
                if (int.TryParse(yearS, out var y)) year = y;

                var matched = lookup.Find(name, year, out var ambiguous);
                if (matched == null)
                {
                    if (ambiguous) result.Ambiguous++;
                    else           result.Unmatched++;
                    if (result.UnmatchedTitles.Count < 100)
                        result.UnmatchedTitles.Add($"{name}{(year.HasValue ? $" ({year})" : "")}");
                    _logger.LogDebug("[StarTrack] Letterboxd unmatched: '{Name}' ({Year}) -> normalized '{Norm}'",
                        name, year, NormalizeTitle(name));
                    continue;
                }

                // Use the Letterboxd Date column as the RatedAt timestamp so
                // imported ratings keep their original chronology and sort
                // correctly. Without this, every imported rating clusters at
                // DateTime.UtcNow and the Newest-rated sort returns junk.
                DateTime? ratedAt = null;
                if (!string.IsNullOrWhiteSpace(dateS) &&
                    DateTime.TryParse(dateS, System.Globalization.CultureInfo.InvariantCulture,
                                      System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedDate))
                {
                    ratedAt = parsedDate.ToUniversalTime();
                }

                // Check if already rated by this user — counts as Update vs Imported
                var existing = await _ratingRepo.GetRatingsAsync(matched.Id.ToString("N")).ConfigureAwait(false);
                var userHad  = existing.UserRatings?.Any(r => r.UserId == userId) == true;

                await _ratingRepo.SaveRatingAsync(matched.Id.ToString("N"), userId, userName, stars, null, ratedAt).ConfigureAwait(false);

                if (userHad) result.Updated++;
                else         result.Imported++;
            }

            _logger.LogInformation("[StarTrack] Letterboxd CSV import done: library={L}, rows={R}, imported={I}, updated={U}, unmatched={N}, ambiguous={A}",
                result.LibraryMovieCount, rows.Count, result.Imported, result.Updated, result.Unmatched, result.Ambiguous);

            await _settingsRepo.SetSyncStateAsync(userId, null, DateTime.UtcNow, result.Imported + result.Updated, result.Unmatched).ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// Imports a Letterboxd watchlist.csv (columns: Date, Name, Year,
        /// Letterboxd URI) into the user's StarTrack watchlist. Same
        /// title+year matching as the ratings importer — films not in the
        /// library are skipped silently and counted.
        /// </summary>
        internal async Task<(int added, int skipped)> ImportWatchlistCsvAsync(
            string userId, Stream csvStream, MovieLookup lookup)
        {
            int added = 0, alreadyOn = 0, notInLib = 0;
            List<Dictionary<string, string>> rows;
            try
            {
                using var reader = new StreamReader(csvStream, Encoding.UTF8);
                var content = await reader.ReadToEndAsync().ConfigureAwait(false);
                rows = ParseCsv(content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] Letterboxd watchlist CSV read failed");
                return (0, 0);
            }

            foreach (var row in rows)
            {
                var name  = GetCol(row, "Name", "Title", "Film");
                var yearS = GetCol(row, "Year");
                if (string.IsNullOrWhiteSpace(name)) { notInLib++; continue; }
                int? year = int.TryParse(yearS, out var y) ? y : null;

                var matched = lookup.Find(name, year, out _);
                if (matched == null) { notInLib++; continue; }

                var itemIdStr = matched.Id.ToString("N");
                if (await _interactions.AddToWatchlistAsync(userId, itemIdStr).ConfigureAwait(false))
                    added++;
                else
                    alreadyOn++;
            }
            _logger.LogInformation("[StarTrack] Letterboxd watchlist import: rows={R}, added={A}, alreadyOnWatchlist={O}, notInLibrary={N}",
                rows.Count, added, alreadyOn, notInLib);
            return (added, alreadyOn + notInLib);
        }

        /// <summary>
        /// Imports a Letterboxd likes.csv (columns: Date, Name, Year,
        /// Letterboxd URI) into the user's StarTrack liked-films list.
        /// </summary>
        internal async Task<(int added, int skipped)> ImportLikesCsvAsync(
            string userId, Stream csvStream, MovieLookup lookup)
        {
            int added = 0, alreadyOn = 0, notInLib = 0;
            List<Dictionary<string, string>> rows;
            try
            {
                using var reader = new StreamReader(csvStream, Encoding.UTF8);
                var content = await reader.ReadToEndAsync().ConfigureAwait(false);
                rows = ParseCsv(content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] Letterboxd likes CSV read failed");
                return (0, 0);
            }

            foreach (var row in rows)
            {
                var name  = GetCol(row, "Name", "Title", "Film");
                var yearS = GetCol(row, "Year");
                if (string.IsNullOrWhiteSpace(name)) { notInLib++; continue; }
                int? year = int.TryParse(yearS, out var y) ? y : null;

                var matched = lookup.Find(name, year, out _);
                if (matched == null) { notInLib++; continue; }

                var itemIdStr = matched.Id.ToString("N");
                if (await _interactions.AddLikeAsync(userId, itemIdStr).ConfigureAwait(false))
                    added++;
                else
                    alreadyOn++;
            }
            _logger.LogInformation("[StarTrack] Letterboxd likes import: rows={R}, added={A}, alreadyLiked={O}, notInLibrary={N}",
                rows.Count, added, alreadyOn, notInLib);
            return (added, alreadyOn + notInLib);
        }

        /// <summary>
        /// Imports Letterboxd diary.csv — the chronological journal with
        /// one row per watch (including rewatches). Columns of interest:
        /// Date, Name, Year, Letterboxd URI, Rating, Rewatch, Tags, Watched Date.
        /// The Watched Date column is what we use for the diary entry
        /// timestamp since Date is just when the row was entered on
        /// Letterboxd.
        /// </summary>
        internal async Task<int> ImportDiaryCsvAsync(
            string userId, Stream csvStream, MovieLookup lookup)
        {
            List<Dictionary<string, string>> rows;
            try
            {
                using var reader = new StreamReader(csvStream, Encoding.UTF8);
                var content = await reader.ReadToEndAsync().ConfigureAwait(false);
                rows = ParseCsv(content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] Letterboxd diary CSV read failed");
                return 0;
            }

            var entries = new List<Models.DiaryEntry>();
            foreach (var row in rows)
            {
                var name    = GetCol(row, "Name", "Title", "Film");
                var yearS   = GetCol(row, "Year");
                var ratingS = GetCol(row, "Rating");
                var rewatchS = GetCol(row, "Rewatch");
                var watchedS = GetCol(row, "Watched Date", "WatchedDate", "Date");
                var review  = GetCol(row, "Review");

                if (string.IsNullOrWhiteSpace(name)) continue;
                int? year = int.TryParse(yearS, out var y) ? y : null;
                var matched = lookup.Find(name, year, out _);
                if (matched == null) continue;

                double? stars = null;
                if (!string.IsNullOrWhiteSpace(ratingS) &&
                    double.TryParse(ratingS, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var st))
                {
                    stars = Math.Clamp(st, 0.5, 5.0);
                }

                DateTime watched;
                if (!DateTime.TryParse(watchedS, System.Globalization.CultureInfo.InvariantCulture,
                                       System.Globalization.DateTimeStyles.AssumeUniversal, out watched))
                {
                    watched = DateTime.UtcNow;
                }

                entries.Add(new Models.DiaryEntry
                {
                    ItemId    = matched.Id.ToString("N"),
                    WatchedAt = watched.ToUniversalTime(),
                    Stars     = stars,
                    Review    = string.IsNullOrWhiteSpace(review) ? null : review.Trim(),
                    Rewatch   = string.Equals(rewatchS, "Yes", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(rewatchS, "true", StringComparison.OrdinalIgnoreCase)
                });
            }

            var added = await _diaryRepo.ImportEntriesAsync(userId, entries).ConfigureAwait(false);
            _logger.LogInformation("[StarTrack] Letterboxd diary import: added {N} entries (of {T} rows)", added, entries.Count);
            return added;
        }

        /// <summary>
        /// Letterboxd has no public RSS feed for likes, so this scrapes the
        /// /username/likes/films/ HTML page (first page only — usually 72
        /// items per page). Same poster-list shape as the favourites scrape.
        /// </summary>
        internal async Task<int> SyncLikesScrapeAsync(string userId, string letterboxdUsername, MovieLookup lookup)
        {
            if (string.IsNullOrWhiteSpace(letterboxdUsername)) return 0;
            var url = $"https://letterboxd.com/{Uri.EscapeDataString(letterboxdUsername)}/likes/films/";
            string html;
            try { html = await _http.GetStringAsync(url).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning("[StarTrack] Likes page fetch failed for {User}: {Msg}", letterboxdUsername, ex.Message);
                return 0;
            }

            // The likes page has a long list of <li> elements with film cards.
            // Each card has alt="Film Name" inside an <img>. We extract every
            // unique alt text under the main poster list.
            var titles = new List<string>();
            try
            {
                // Restrict to the films grid container (poster-list class)
                var listIdx = html.IndexOf("class=\"poster-list", StringComparison.OrdinalIgnoreCase);
                if (listIdx < 0) return 0;
                var section = html.Substring(listIdx);
                var endIdx = section.IndexOf("</ul>", StringComparison.OrdinalIgnoreCase);
                if (endIdx > 0) section = section.Substring(0, endIdx);

                var altRx = new System.Text.RegularExpressions.Regex("alt=\"([^\"]+)\"",
                    System.Text.RegularExpressions.RegexOptions.Compiled);
                foreach (System.Text.RegularExpressions.Match m in altRx.Matches(section))
                {
                    var t = m.Groups[1].Value.Trim();
                    if (t.Length > 0 && !titles.Contains(t, StringComparer.OrdinalIgnoreCase))
                        titles.Add(t);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StarTrack] Likes HTML parse failed");
                return 0;
            }

            int added = 0;
            foreach (var title in titles)
            {
                var matched = lookup.Find(title, null, out _);
                if (matched == null) continue;
                if (await _interactions.AddLikeAsync(userId, matched.Id.ToString("N")).ConfigureAwait(false))
                    added++;
            }
            _logger.LogInformation("[StarTrack] Letterboxd likes scrape for {User}: added {N} new likes", letterboxdUsername, added);
            return added;
        }

        /// <summary>
        /// Fetches the user's Letterboxd watchlist RSS and adds any new
        /// entries to the StarTrack watchlist. Called from SyncRssAsync
        /// after the main rating sync so a single "Sync now" click pulls
        /// everything.
        /// </summary>
        internal async Task<int> SyncWatchlistRssAsync(string userId, string letterboxdUsername, MovieLookup lookup)
        {
            if (string.IsNullOrWhiteSpace(letterboxdUsername)) return 0;
            var url = $"https://letterboxd.com/{Uri.EscapeDataString(letterboxdUsername)}/watchlist/rss/";
            string xml;
            try { xml = await _http.GetStringAsync(url).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning("[StarTrack] Watchlist RSS fetch failed for {User}: {Msg}", letterboxdUsername, ex.Message);
                return 0;
            }

            List<LetterboxdRssEntry> entries;
            try { entries = ParseRss(xml); }
            catch (Exception ex)
            {
                _logger.LogWarning("[StarTrack] Watchlist RSS parse failed: {Msg}", ex.Message);
                return 0;
            }

            int added = 0;
            foreach (var entry in entries)
            {
                var matched = lookup.Find(entry.FilmTitle, entry.FilmYear, out _);
                if (matched == null) continue;
                if (await _interactions.AddToWatchlistAsync(userId, matched.Id.ToString("N")).ConfigureAwait(false))
                    added++;
            }
            _logger.LogInformation("[StarTrack] Letterboxd watchlist RSS sync: added {N} new entries", added);
            return added;
        }

        /// <summary>
        /// Scrapes the Letterboxd user profile page for the "favourite films"
        /// section (the user's Top 4) and sets those as StarTrack favorites.
        /// Best-effort HTML parsing; returns a count of favorites populated.
        /// </summary>
        internal async Task<int> ScrapeLetterboxdFavoritesAsync(string userId, string letterboxdUsername, MovieLookup lookup)
        {
            if (string.IsNullOrWhiteSpace(letterboxdUsername)) return 0;
            var url = $"https://letterboxd.com/{Uri.EscapeDataString(letterboxdUsername)}/";
            string html;
            try { html = await _http.GetStringAsync(url).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning("[StarTrack] Letterboxd profile fetch failed for {User}: {Msg}", letterboxdUsername, ex.Message);
                return 0;
            }

            // Letterboxd's favourites section has this shape (simplified):
            //   <section id="favourites" ...>
            //     <ul class="poster-list">
            //       <li><div class="film-poster" data-film-slug="the-batman" ...>
            //         <img alt="The Batman" ... >
            //
            // We regex-extract data-film-slug + alt text as the candidate
            // (title, _) tuples, then title-match against the library.
            var favoriteTitles = new List<string>();
            try
            {
                // Find the favourites section first
                var favIdx = html.IndexOf("id=\"favourites\"", StringComparison.OrdinalIgnoreCase);
                if (favIdx < 0) return 0;
                var favSection = html.Substring(favIdx, Math.Min(8000, html.Length - favIdx));

                // Extract alt="..." values from <img> tags inside
                var altRx = new System.Text.RegularExpressions.Regex("alt=\"([^\"]+)\"",
                    System.Text.RegularExpressions.RegexOptions.Compiled);
                foreach (System.Text.RegularExpressions.Match m in altRx.Matches(favSection))
                {
                    var title = m.Groups[1].Value.Trim();
                    if (title.Length > 0 && !favoriteTitles.Contains(title, StringComparer.OrdinalIgnoreCase))
                    {
                        favoriteTitles.Add(title);
                        if (favoriteTitles.Count >= 4) break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StarTrack] Letterboxd favorites HTML parse failed");
                return 0;
            }

            if (favoriteTitles.Count == 0) return 0;

            var ids = new List<string>();
            foreach (var title in favoriteTitles)
            {
                // No year in alt text — Find() will pick the oldest by default
                var matched = lookup.Find(title, null, out _);
                if (matched != null) ids.Add(matched.Id.ToString("N"));
            }

            if (ids.Count == 0) return 0;
            await _interactions.SetFavoritesAsync(userId, ids).ConfigureAwait(false);
            _logger.LogInformation("[StarTrack] Letterboxd profile scrape set {N} favorites for {User}", ids.Count, letterboxdUsername);
            return ids.Count;
        }

        /// <summary>
        /// Exposed so the controller can build the lookup once and pass it
        /// through to all importers (ratings, watchlist, likes, diary) to
        /// avoid separate library queries for a single ZIP upload.
        /// </summary>
        internal MovieLookup BuildLookupForImport() => BuildMovieLookup();

        // ============================================================= //
        // RSS SYNC
        // ============================================================= //

        /// <summary>
        /// Fetches the user's Letterboxd RSS feed and imports new ratings
        /// (anything newer than the last-synced guid).
        /// </summary>
        public async Task<LetterboxdImportResult> SyncRssAsync(string userId, string userName)
        {
            var result = new LetterboxdImportResult();
            var settings = await _settingsRepo.GetAsync(userId).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(settings.Username))
            {
                result.Error = "No Letterboxd username configured";
                return result;
            }

            var url = $"https://letterboxd.com/{Uri.EscapeDataString(settings.Username)}/rss/";
            string xml;
            try
            {
                xml = await _http.GetStringAsync(url).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[StarTrack] Letterboxd RSS fetch failed for {User}: {Msg}", settings.Username, ex.Message);
                result.Error = "Could not reach Letterboxd: " + ex.Message;
                return result;
            }

            List<LetterboxdRssEntry> entries;
            try
            {
                entries = ParseRss(xml);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] Failed to parse Letterboxd RSS for {User}", settings.Username);
                result.Error = "Could not parse Letterboxd RSS: " + ex.Message;
                return result;
            }

            // Diff: only import entries newer than lastSyncedGuid.
            // RSS is newest-first. Walk entries and stop when we hit lastSyncedGuid.
            var toImport = new List<LetterboxdRssEntry>();
            foreach (var entry in entries)
            {
                if (!string.IsNullOrEmpty(settings.LastSyncedGuid) && entry.Guid == settings.LastSyncedGuid)
                    break;
                if (entry.Rating.HasValue)
                    toImport.Add(entry);
            }

            var lookup = BuildMovieLookup();
            result.LibraryMovieCount = lookup.TotalMovies;

            // Collect RSS entries that need to become diary entries too
            var diaryAdds = new List<Models.DiaryEntry>();
            var likesFromRss = 0;

            foreach (var entry in toImport)
            {
                var matched = lookup.Find(entry.FilmTitle, entry.FilmYear, out var ambiguous);
                if (matched == null)
                {
                    if (ambiguous) result.Ambiguous++;
                    else           result.Unmatched++;
                    if (result.UnmatchedTitles.Count < 100)
                        result.UnmatchedTitles.Add($"{entry.FilmTitle}{(entry.FilmYear.HasValue ? $" ({entry.FilmYear})" : "")}");
                    _logger.LogDebug("[StarTrack] Letterboxd RSS unmatched: '{Name}' ({Year}) -> normalized '{Norm}'",
                        entry.FilmTitle, entry.FilmYear, NormalizeTitle(entry.FilmTitle));
                    continue;
                }

                var itemIdStr = matched.Id.ToString("N");

                // Each RSS entry corresponds to a watch on Letterboxd, so it
                // also belongs in the diary store. Without this, "Sync now"
                // updated the films view but never populated the diary, and
                // rewatches via sync would silently miss the diary tab.
                diaryAdds.Add(new Models.DiaryEntry
                {
                    ItemId    = itemIdStr,
                    WatchedAt = (entry.WatchedDate ?? DateTime.UtcNow).ToUniversalTime(),
                    Stars     = entry.Rating,
                    Review    = null,
                    Rewatch   = entry.Rewatch
                });

                var stars = Math.Clamp(entry.Rating!.Value, 0.5, 5.0);
                var existing = await _ratingRepo.GetRatingsAsync(itemIdStr).ConfigureAwait(false);
                var userHad  = existing.UserRatings?.Any(r => r.UserId == userId) == true;

                await _ratingRepo.SaveRatingAsync(itemIdStr, userId, userName, stars).ConfigureAwait(false);

                if (userHad) result.Updated++;
                else         result.Imported++;

                // If the user also liked this film on Letterboxd, mirror
                // the like into StarTrack. AddLikeAsync is idempotent so
                // re-syncing won't create duplicates.
                if (entry.Liked)
                {
                    try
                    {
                        if (await _interactions.AddLikeAsync(userId, itemIdStr).ConfigureAwait(false))
                            likesFromRss++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[StarTrack] RSS-sync: liking {Item} failed — continuing", itemIdStr);
                    }
                }
            }

            if (likesFromRss > 0)
            {
                result.LikesAdded += likesFromRss;
                _logger.LogInformation("[StarTrack] RSS sync imported {N} new likes from rated entries", likesFromRss);
            }

            // ---- Likes catch-up ----
            // If a user likes something days/weeks AFTER they rated it on
            // Letterboxd, the main sync loop above will break at the
            // lastSyncedGuid before reaching that entry (rating + diary
            // were already synced; there's nothing "new"). So we do a
            // separate pass across every RSS entry — ignoring lastSyncedGuid
            // — and call AddLikeAsync for any rated+liked entry we haven't
            // already liked. Both AddLikeAsync and the match are cheap so
            // this is safe to run every sync.
            var likesCatchup = 0;
            foreach (var entry in entries)
            {
                if (!entry.Liked) continue;
                if (!entry.Rating.HasValue) continue; // pure unrated likes are handled by the HTML scraper
                var matched = lookup.Find(entry.FilmTitle, entry.FilmYear, out _);
                if (matched == null) continue;
                var itemIdStr = matched.Id.ToString("N");
                try
                {
                    if (await _interactions.AddLikeAsync(userId, itemIdStr).ConfigureAwait(false))
                        likesCatchup++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[StarTrack] Likes-catchup: liking {Item} failed — continuing", itemIdStr);
                }
            }
            if (likesCatchup > 0)
            {
                // likesFromRss and likesCatchup are disjoint sets (both
                // only increment when AddLikeAsync returns true, i.e. a
                // NEW like was written; entries already liked during the
                // main loop will have AddLikeAsync return false the
                // second time). So just add them straight.
                result.LikesAdded += likesCatchup;
                _logger.LogInformation("[StarTrack] RSS likes catch-up added {N} likes on already-synced rated entries", likesCatchup);
            }

            // ---- Diary entries ----
            // Each RSS rating becomes a diary entry too (rewatch-aware).
            // Dedupes on (itemId, watched-day) so re-syncing doesn't spam.
            if (diaryAdds.Count > 0)
            {
                try
                {
                    var diaryNew = await _diaryRepo.ImportEntriesAsync(userId, diaryAdds).ConfigureAwait(false);
                    _logger.LogInformation("[StarTrack] RSS sync added {N} new diary entries", diaryNew);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[StarTrack] Diary write from RSS threw — continuing");
                }
            }

            // ---- Watchlist RSS ----
            // Same "Sync now" button should pull the user's watchlist too,
            // not just their ratings. Reuse the already-built movie lookup.
            try
            {
                var wlAdded = await SyncWatchlistRssAsync(userId, settings.Username!, lookup).ConfigureAwait(false);
                result.WatchlistAdded += wlAdded;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StarTrack] Watchlist RSS sync threw — continuing");
            }

            // ---- Likes scrape ----
            // Letterboxd doesn't expose a public RSS for likes, so we scrape
            // the /username/likes/films/ HTML page (first page only).
            try
            {
                var likesAdded = await SyncLikesScrapeAsync(userId, settings.Username!, lookup).ConfigureAwait(false);
                result.LikesAdded += likesAdded;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StarTrack] Likes scrape threw — continuing");
            }

            // Mark newest entry as the last-seen guid so the next run diffs correctly
            var newest = entries.FirstOrDefault();
            await _settingsRepo.SetSyncStateAsync(userId, newest?.Guid, DateTime.UtcNow, result.Imported + result.Updated, result.Unmatched).ConfigureAwait(false);

            _logger.LogInformation("[StarTrack] Letterboxd RSS sync for {User} ({Lb}): imported={I}, updated={U}, unmatched={N}, watchlist+{W}, likes+{L}",
                userName, settings.Username, result.Imported, result.Updated, result.Unmatched, result.WatchlistAdded, result.LikesAdded);
            return result;
        }

        // ============================================================= //
        // SHARED: LIBRARY MATCHING
        // ============================================================= //

        /// <summary>
        /// Diagnostic endpoint helper — runs the same library query the
        /// matcher would use and returns a sample of normalized titles so
        /// the user can see exactly what StarTrack sees.
        /// </summary>
        public LetterboxdDiagnoseResult Diagnose()
        {
            var result = new LetterboxdDiagnoseResult();
            try
            {
                var lookup = BuildMovieLookup(out var usedFallback, out var sample, out var zombies);
                result.LibraryMovieCount       = lookup.TotalMovies;
                result.UniqueNormalizedTitles  = lookup.UniqueNormalizedTitles;
                result.UsedFallbackQuery       = usedFallback;
                result.SampleMovies            = sample;
                result.ZombiesFiltered         = zombies;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] Letterboxd Diagnose failed");
                result.Error = ex.Message;
            }
            return result;
        }

        /// <summary>
        /// Cleans up "dead" ratings — entries in ratings.json that point to
        /// library items whose underlying file no longer exists on disk
        /// (typical after a hard drive failure leaves zombie items in the
        /// Jellyfin DB). Returns a count of deleted rating entries and the
        /// number of dead items those entries were attached to.
        /// </summary>
        public async Task<CleanupResult> CleanupDeadRatingsAsync()
        {
            var result = new CleanupResult();
            try
            {
                // Fetch every item that currently has ratings in our store.
                var recent = await _ratingRepo.GetRecentAsync(int.MaxValue).ConfigureAwait(false);
                var seenItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in recent) seenItems.Add(r.ItemId);
                result.TotalItems = seenItems.Count;

                var deadItemIds = new List<string>();
                foreach (var itemId in seenItems)
                {
                    if (!Guid.TryParse(itemId, out var gid))
                    {
                        deadItemIds.Add(itemId);
                        continue;
                    }
                    BaseItem? item;
                    try { item = _libraryManager.GetItemById(gid); }
                    catch { item = null; }

                    if (item == null)
                    {
                        deadItemIds.Add(itemId);
                        continue;
                    }
                    if (!IsLivingItem(item))
                    {
                        deadItemIds.Add(itemId);
                    }
                }

                _logger.LogInformation("[StarTrack] Cleanup found {D} dead items out of {T}", deadItemIds.Count, seenItems.Count);
                result.DeletedItems = deadItemIds.Count;

                // Count how many individual rating rows those dead items carried,
                // then drop every one of them.
                foreach (var itemId in deadItemIds)
                {
                    var details = await _ratingRepo.GetRatingsAsync(itemId).ConfigureAwait(false);
                    var count = details?.UserRatings?.Count ?? 0;
                    result.DeletedRatings += count;
                    foreach (var ur in details?.UserRatings ?? new List<UserRatingDto>())
                    {
                        await _ratingRepo.DeleteRatingAsync(itemId, ur.UserId).ConfigureAwait(false);
                    }
                }

                _logger.LogInformation("[StarTrack] Cleanup deleted {R} ratings across {I} items", result.DeletedRatings, result.DeletedItems);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] CleanupDeadRatings failed");
                result.Error = ex.Message;
            }
            return result;
        }

        /// <summary>
        /// Detects whether a library item is "alive" — i.e., has a non-empty
        /// Path that actually resolves on disk. Dead items (Jellyfin DB rows
        /// that linger after a drive failure) return false and are filtered
        /// out of the matcher so Letterboxd imports never land on a zombie
        /// the user can't play back or see a thumbnail for.
        /// </summary>
        private static bool IsLivingItem(BaseItem item)
        {
            if (item == null) return false;
            var path = item.Path;
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                // Non-file protocols (http://, ftp://, etc) — assume alive,
                // no sensible way to File.Exists() them.
                if (!item.IsFileProtocol) return true;
                return File.Exists(path) || Directory.Exists(path);
            }
            catch
            {
                // Network mount down, permission error, etc — be permissive
                // and treat as alive rather than wiping the user's ratings.
                return true;
            }
        }

        private MovieLookup BuildMovieLookup()
        {
            return BuildMovieLookup(out _, out _, out _);
        }

        /// <summary>
        /// Pulls every Movie from the library once and builds an in-memory
        /// lookup keyed by normalized title. Matching against this dictionary
        /// is O(1), handles year drift flexibly, and avoids any quirks with
        /// InternalItemsQuery's Years filter returning 0 results in some
        /// Jellyfin setups.
        ///
        /// Filters out "zombie" items whose Path no longer resolves on disk,
        /// which happens when a hard drive fails — Jellyfin keeps the DB rows
        /// but the actual file is gone. Without this filter, the matcher can
        /// pick a zombie as the winner for a title and leave the user with a
        /// rating that has no thumbnail and can't be played.
        /// </summary>
        private MovieLookup BuildMovieLookup(out bool usedFallback, out List<SampleMovie> sample, out int zombiesFiltered)
        {
            usedFallback = false;
            sample = new List<SampleMovie>();
            zombiesFiltered = 0;
            // No filters beyond IncludeItemTypes so we capture every movie
            // regardless of year metadata quality. Recursive = true walks
            // all library folders.
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive        = true
            };
            IReadOnlyList<BaseItem> items = _libraryManager.GetItemList(query) ?? (IReadOnlyList<BaseItem>)Array.Empty<BaseItem>();
            _logger.LogInformation("[StarTrack] Movie library query returned {N} items", items.Count);

            // Fallback: if the typed query returned nothing (some Jellyfin
            // setups are fussy about IncludeItemTypes), pull all items and
            // filter in-memory by GetBaseItemKind.
            if (items.Count == 0)
            {
                usedFallback = true;
                var allQuery = new InternalItemsQuery { Recursive = true };
                var allItems = _libraryManager.GetItemList(allQuery) ?? (IReadOnlyList<BaseItem>)Array.Empty<BaseItem>();
                _logger.LogInformation("[StarTrack] Fallback: all-items query returned {N} items", allItems.Count);
                items = allItems
                    .Where(i => i != null && i.GetBaseItemKind() == BaseItemKind.Movie)
                    .ToList();
                _logger.LogInformation("[StarTrack] Fallback: filtered down to {N} movies", items.Count);
            }

            // Filter out zombie items whose path doesn't resolve on disk.
            // This captures items left over after hard-drive failures where
            // Jellyfin keeps the DB row but the underlying file is gone.
            var living = new List<BaseItem>(items.Count);
            foreach (var m in items)
            {
                if (m == null) continue;
                if (IsLivingItem(m)) living.Add(m);
                else                 zombiesFiltered++;
            }
            if (zombiesFiltered > 0)
            {
                _logger.LogInformation("[StarTrack] Filtered out {N} zombie library items (path missing on disk)", zombiesFiltered);
            }
            items = living;

            // Collect a sample of normalized titles so the Diagnose
            // endpoint can show them to the user in the UI.
            for (int i = 0; i < items.Count && sample.Count < 20; i++)
            {
                var m = items[i];
                if (m == null || string.IsNullOrEmpty(m.Name)) continue;
                sample.Add(new SampleMovie
                {
                    OriginalTitle   = m.Name,
                    NormalizedTitle = NormalizeTitle(m.Name),
                    Year            = m.ProductionYear
                });
            }

            return new MovieLookup(items, _logger);
        }

        internal sealed class MovieLookup
        {
            // Dictionary from normalized title → list of movies with that title
            private readonly Dictionary<string, List<BaseItem>> _byTitle =
                new(StringComparer.OrdinalIgnoreCase);

            private readonly ILogger _logger;

            public int TotalMovies { get; }
            public int UniqueNormalizedTitles => _byTitle.Count;

            public MovieLookup(IReadOnlyList<BaseItem> movies, ILogger logger)
            {
                _logger = logger;
                TotalMovies = movies.Count;
                int sampleCount = 0;
                foreach (var m in movies)
                {
                    if (m == null || string.IsNullOrEmpty(m.Name)) continue;
                    var norm = NormalizeTitle(m.Name);
                    if (string.IsNullOrEmpty(norm)) continue;
                    if (!_byTitle.TryGetValue(norm, out var list))
                    {
                        list = new List<BaseItem>();
                        _byTitle[norm] = list;
                    }
                    list.Add(m);

                    if (sampleCount < 5)
                    {
                        logger.LogDebug("[StarTrack] Library sample {I}: '{Orig}' ({Year}) -> '{Norm}'",
                            sampleCount + 1, m.Name, m.ProductionYear, norm);
                        sampleCount++;
                    }
                }
                logger.LogInformation("[StarTrack] Built movie lookup: {N} unique normalized titles ({T} total movies, {D} duplicates)",
                    _byTitle.Count, movies.Count, movies.Count - _byTitle.Count);
            }

            /// <summary>
            /// Finds the best movie match for a (title, year) from Letterboxd.
            /// NEVER returns ambiguous=true as a terminal state unless there's
            /// literally no title match — when there are multiple candidates,
            /// we always pick a winner:
            ///
            ///   1. Exact year match and exactly one candidate → that one.
            ///   2. Exact year match with multiple candidates → first one (duplicate copies
            ///      of the same film across libraries — it doesn't matter which we pick).
            ///   3. Within ±1 year, closest year wins (ties → first).
            ///   4. No year info → candidate with the oldest ProductionYear
            ///      (usually the canonical/original release).
            ///   5. No ProductionYear metadata at all → first candidate.
            ///
            /// This fixes the v1.1.3-v1.1.4 bug where a user with multiple
            /// copies of the same movie in different libraries would have
            /// 100+ "ambiguous" matches and nothing got imported.
            /// </summary>
            public BaseItem? Find(string title, int? year, out bool ambiguous)
            {
                ambiguous = false;
                var norm = NormalizeTitle(title);
                if (string.IsNullOrEmpty(norm)) return null;

                if (!_byTitle.TryGetValue(norm, out var candidates) || candidates.Count == 0)
                    return null;

                if (candidates.Count == 1) return candidates[0];

                // Multiple candidates — pick the best one deterministically.
                if (year.HasValue)
                {
                    // 1. Exact year match (take first if multiple duplicates)
                    var exact = candidates.Where(c => c.ProductionYear == year.Value).ToList();
                    if (exact.Count >= 1)
                    {
                        if (exact.Count > 1)
                            _logger.LogDebug("[StarTrack] Title '{T}' year {Y}: {N} exact duplicates, picking first", title, year, exact.Count);
                        return exact[0];
                    }

                    // 2. Closest year within ±1 (or more, as a generous fallback)
                    var withYear = candidates.Where(c => c.ProductionYear.HasValue).ToList();
                    if (withYear.Count > 0)
                    {
                        var closest = withYear
                            .OrderBy(c => Math.Abs(c.ProductionYear!.Value - year.Value))
                            .ThenBy(c => c.ProductionYear!.Value)
                            .First();
                        _logger.LogDebug("[StarTrack] Title '{T}' year {Y}: no exact match, closest is {C} ({CY})",
                            title, year, closest.Name, closest.ProductionYear);
                        return closest;
                    }
                }

                // 3. No year given, or library items have no year metadata.
                //    Prefer items that DO have a year (pick oldest as canonical).
                var withYearAny = candidates.Where(c => c.ProductionYear.HasValue)
                                             .OrderBy(c => c.ProductionYear!.Value)
                                             .ToList();
                if (withYearAny.Count > 0)
                {
                    _logger.LogDebug("[StarTrack] Title '{T}': no year hint, picking oldest ({Y})",
                        title, withYearAny[0].ProductionYear);
                    return withYearAny[0];
                }

                // 4. Dead last fallback: just take the first candidate in the list.
                _logger.LogDebug("[StarTrack] Title '{T}': no year metadata anywhere, picking first of {N} candidates",
                    title, candidates.Count);
                return candidates[0];
            }
        }

        /// <summary>
        /// Aggressive title normalization designed to make Letterboxd titles
        /// and Jellyfin titles match as often as possible. Applies:
        ///   1. Unicode NFD decompose + strip combining marks (strips accents
        ///      so "Amélie" matches "Amelie")
        ///   2. "The"/"A"/"An" prefix handling — Letterboxd sometimes writes
        ///      "Matrix, The" and Jellyfin "The Matrix" (or vice versa). We
        ///      strip leading articles from BOTH sides before comparing.
        ///   3. Punctuation-to-space (Unicode dashes, curly quotes, colons,
        ///      ampersands, common stylistic characters)
        ///   4. Lowercase invariant
        ///   5. Whitespace collapse
        /// </summary>
        private static string NormalizeTitle(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            // 1. Decompose + strip diacritics
            var normalized = s.Normalize(NormalizationForm.FormKD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(ch);
            }

            // 2. Punctuation -> space. Handle Unicode dashes, curly quotes, etc.
            var chars = new StringBuilder(sb.Length);
            foreach (var ch in sb.ToString())
            {
                if (char.IsLetterOrDigit(ch))
                {
                    chars.Append(char.ToLowerInvariant(ch));
                }
                else if (char.IsWhiteSpace(ch))
                {
                    chars.Append(' ');
                }
                else
                {
                    // Everything else (apostrophes, dashes, colons, quotes,
                    // parens, &, !, ?, periods, commas, ...) becomes a space.
                    chars.Append(' ');
                }
            }

            // 3. Collapse whitespace + trim
            var parts = chars.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // 4. Strip leading article ("the", "a", "an") so
            //    "The Matrix" == "Matrix" == "Matrix, The" after a further
            //    transform below.
            if (parts.Length > 1 && (parts[0] == "the" || parts[0] == "a" || parts[0] == "an"))
            {
                parts = parts.Skip(1).ToArray();
            }

            // 5. Strip trailing ", The" / ", A" / ", An" (already lost the
            //    comma during punctuation removal, so now the last token is
            //    the article).
            if (parts.Length > 1)
            {
                var last = parts[parts.Length - 1];
                if (last == "the" || last == "a" || last == "an")
                    parts = parts.Take(parts.Length - 1).ToArray();
            }

            return string.Join(' ', parts);
        }

        // ============================================================= //
        // CSV PARSING
        // ============================================================= //

        private static List<Dictionary<string, string>> ParseCsv(string content)
        {
            var rows = new List<Dictionary<string, string>>();
            var lines = SplitCsvLines(content);
            if (lines.Count == 0) return rows;

            var headers = ParseCsvLine(lines[0]);
            for (int i = 1; i < lines.Count; i++)
            {
                var fields = ParseCsvLine(lines[i]);
                if (fields.Count == 0) continue;
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < headers.Count && c < fields.Count; c++)
                    dict[headers[c]] = fields[c];
                rows.Add(dict);
            }
            return rows;
        }

        // Split on newline BUT respect quoted fields that span lines
        private static List<string> SplitCsvLines(string content)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;
            foreach (var ch in content)
            {
                if (ch == '"') inQuotes = !inQuotes;
                if (ch == '\n' && !inQuotes)
                {
                    var line = current.ToString().TrimEnd('\r');
                    if (line.Length > 0) result.Add(line);
                    current.Clear();
                    continue;
                }
                current.Append(ch);
            }
            if (current.Length > 0) result.Add(current.ToString().TrimEnd('\r'));
            return result;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else field.Append(ch);
                }
                else
                {
                    if (ch == ',') { result.Add(field.ToString()); field.Clear(); }
                    else if (ch == '"') inQuotes = true;
                    else field.Append(ch);
                }
            }
            result.Add(field.ToString());
            return result;
        }

        private static string GetCol(Dictionary<string, string> row, params string[] names)
        {
            foreach (var n in names)
                if (row.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            return string.Empty;
        }

        // ============================================================= //
        // RSS PARSING
        // ============================================================= //

        private sealed class LetterboxdRssEntry
        {
            public string  Guid       { get; set; } = string.Empty;
            public string  FilmTitle  { get; set; } = string.Empty;
            public int?    FilmYear   { get; set; }
            public double? Rating     { get; set; }
            public DateTime? WatchedDate { get; set; }
            public bool    Rewatch    { get; set; }
            public bool    Liked      { get; set; }
        }

        private static List<LetterboxdRssEntry> ParseRss(string xml)
        {
            var doc = XDocument.Parse(xml);
            var result = new List<LetterboxdRssEntry>();

            // Find the letterboxd: namespace dynamically (usually https://letterboxd.com)
            var lbNsUri = doc.Root?.Attributes()
                .FirstOrDefault(a => a.IsNamespaceDeclaration && a.Name.LocalName == "letterboxd")?.Value
                ?? "https://letterboxd.com";
            XNamespace lb = lbNsUri;

            var items = doc.Descendants("item");
            foreach (var item in items)
            {
                var entry = new LetterboxdRssEntry
                {
                    Guid      = item.Element("guid")?.Value ?? string.Empty,
                    FilmTitle = item.Element(lb + "filmTitle")?.Value ?? string.Empty
                };

                var yearStr = item.Element(lb + "filmYear")?.Value;
                if (int.TryParse(yearStr, out var yr)) entry.FilmYear = yr;

                var ratingStr = item.Element(lb + "memberRating")?.Value;
                if (double.TryParse(ratingStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r))
                    entry.Rating = r;

                var watchedStr = item.Element(lb + "watchedDate")?.Value;
                if (DateTime.TryParse(watchedStr, out var wd)) entry.WatchedDate = wd;

                var rewatchStr = item.Element(lb + "rewatch")?.Value;
                entry.Rewatch = string.Equals(rewatchStr, "Yes", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(rewatchStr, "true", StringComparison.OrdinalIgnoreCase);

                // Letterboxd's RSS includes <letterboxd:liked>Yes</letterboxd:liked>
                // on entries the user has liked. Parsing it here means a
                // rate+like in one Letterboxd action becomes a rating +
                // diary entry + like in StarTrack, all from a single RSS
                // pass — no need to wait for the HTML likes scrape.
                var likedStr = item.Element(lb + "liked")?.Value;
                entry.Liked = string.Equals(likedStr, "Yes", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(likedStr, "true", StringComparison.OrdinalIgnoreCase);

                // Fallback: some entries put title in <title> like "Film Name, 2022 - ★★★★½"
                if (string.IsNullOrEmpty(entry.FilmTitle))
                {
                    var titleEl = item.Element("title")?.Value;
                    if (!string.IsNullOrEmpty(titleEl))
                    {
                        var dash = titleEl.LastIndexOf('-');
                        var core = dash > 0 ? titleEl[..dash].Trim() : titleEl.Trim();
                        var comma = core.LastIndexOf(',');
                        if (comma > 0 && int.TryParse(core[(comma + 1)..].Trim(), out var ty))
                        {
                            entry.FilmTitle = core[..comma].Trim();
                            if (!entry.FilmYear.HasValue) entry.FilmYear = ty;
                        }
                        else
                        {
                            entry.FilmTitle = core;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(entry.FilmTitle))
                    result.Add(entry);
            }
            return result;
        }
    }
}
