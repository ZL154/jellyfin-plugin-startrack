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
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

        private readonly RatingRepository _ratingRepo;
        private readonly LetterboxdSettingsRepository _settingsRepo;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<LetterboxdSyncService> _logger;

        public LetterboxdSyncService(
            RatingRepository ratingRepo,
            LetterboxdSettingsRepository settingsRepo,
            ILibraryManager libraryManager,
            ILogger<LetterboxdSyncService> logger)
        {
            _ratingRepo     = ratingRepo;
            _settingsRepo   = settingsRepo;
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
        public async Task<LetterboxdImportResult> ImportCsvAsync(
            string userId, string userName, Stream csvStream)
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
            var lookup = BuildMovieLookup();
            result.LibraryMovieCount = lookup.TotalMovies;
            _logger.LogInformation("[StarTrack] Letterboxd import: library has {N} movies indexed for matching", lookup.TotalMovies);

            foreach (var row in rows)
            {
                var name   = GetCol(row, "Name", "Title", "Film");
                var yearS  = GetCol(row, "Year");
                var rating = GetCol(row, "Rating");

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

                // Check if already rated by this user — counts as Update vs Imported
                var existing = await _ratingRepo.GetRatingsAsync(matched.Id.ToString("N")).ConfigureAwait(false);
                var userHad  = existing.UserRatings?.Any(r => r.UserId == userId) == true;

                await _ratingRepo.SaveRatingAsync(matched.Id.ToString("N"), userId, userName, stars).ConfigureAwait(false);

                if (userHad) result.Updated++;
                else         result.Imported++;
            }

            _logger.LogInformation("[StarTrack] Letterboxd CSV import done: library={L}, rows={R}, imported={I}, updated={U}, unmatched={N}, ambiguous={A}",
                result.LibraryMovieCount, rows.Count, result.Imported, result.Updated, result.Unmatched, result.Ambiguous);

            await _settingsRepo.SetSyncStateAsync(userId, null, DateTime.UtcNow, result.Imported + result.Updated, result.Unmatched).ConfigureAwait(false);
            return result;
        }

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

                var stars = Math.Clamp(entry.Rating!.Value, 0.5, 5.0);
                var existing = await _ratingRepo.GetRatingsAsync(matched.Id.ToString("N")).ConfigureAwait(false);
                var userHad  = existing.UserRatings?.Any(r => r.UserId == userId) == true;

                await _ratingRepo.SaveRatingAsync(matched.Id.ToString("N"), userId, userName, stars).ConfigureAwait(false);

                if (userHad) result.Updated++;
                else         result.Imported++;
            }

            // Mark newest entry as the last-seen guid so the next run diffs correctly
            var newest = entries.FirstOrDefault();
            await _settingsRepo.SetSyncStateAsync(userId, newest?.Guid, DateTime.UtcNow, result.Imported + result.Updated, result.Unmatched).ConfigureAwait(false);

            _logger.LogInformation("[StarTrack] Letterboxd RSS sync for {User} ({Lb}): imported={I}, updated={U}, unmatched={N}",
                userName, settings.Username, result.Imported, result.Updated, result.Unmatched);
            return result;
        }

        // ============================================================= //
        // SHARED: LIBRARY MATCHING
        // ============================================================= //

        /// <summary>
        /// Pulls every Movie from the library once and builds an in-memory
        /// lookup keyed by normalized title. Matching against this dictionary
        /// is O(1), handles year drift flexibly, and avoids any quirks with
        /// InternalItemsQuery's Years filter returning 0 results in some
        /// Jellyfin setups.
        /// </summary>
        private MovieLookup BuildMovieLookup()
        {
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
                var allQuery = new InternalItemsQuery { Recursive = true };
                var allItems = _libraryManager.GetItemList(allQuery) ?? (IReadOnlyList<BaseItem>)Array.Empty<BaseItem>();
                items = allItems
                    .Where(i => i != null && i.GetBaseItemKind() == BaseItemKind.Movie)
                    .ToList();
                _logger.LogInformation("[StarTrack] Fallback query (all-items + filter) found {N} movies", items.Count);
            }

            return new MovieLookup(items, _logger);
        }

        private sealed class MovieLookup
        {
            // Dictionary from normalized title → list of movies with that title
            private readonly Dictionary<string, List<BaseItem>> _byTitle =
                new(StringComparer.OrdinalIgnoreCase);

            public int TotalMovies { get; }

            public MovieLookup(IReadOnlyList<BaseItem> movies, ILogger logger)
            {
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
                logger.LogInformation("[StarTrack] Built movie lookup: {N} unique normalized titles", _byTitle.Count);
            }

            /// <summary>
            /// Finds a single movie matching the given (title, year). Year
            /// is used only to disambiguate when multiple movies share a
            /// normalized title. Accepts exact year, +/- 1 year, or no-year
            /// match in that order.
            /// </summary>
            public BaseItem? Find(string title, int? year, out bool ambiguous)
            {
                ambiguous = false;
                var norm = NormalizeTitle(title);
                if (string.IsNullOrEmpty(norm)) return null;

                if (!_byTitle.TryGetValue(norm, out var candidates) || candidates.Count == 0)
                    return null;

                if (candidates.Count == 1) return candidates[0];

                // Multiple movies with the same normalized title. Disambiguate by year.
                if (year.HasValue)
                {
                    // 1. Exact year match
                    var exact = candidates.Where(c => c.ProductionYear == year.Value).ToList();
                    if (exact.Count == 1) return exact[0];
                    if (exact.Count > 1)  { ambiguous = true; return null; }

                    // 2. +/- 1 year (Letterboxd vs TMDb release-year drift)
                    var near = candidates
                        .Where(c => c.ProductionYear.HasValue && Math.Abs(c.ProductionYear.Value - year.Value) <= 1)
                        .ToList();
                    if (near.Count == 1) return near[0];
                    if (near.Count > 1)  { ambiguous = true; return null; }
                }

                // No way to disambiguate — treat as ambiguous so the user
                // sees it in the unmatched list with context
                ambiguous = true;
                return null;
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
