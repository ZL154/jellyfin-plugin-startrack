using System;
using System.Collections.Generic;
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

                var matched = FindMovie(name, year, out var ambiguous);
                if (matched == null)
                {
                    if (ambiguous) result.Ambiguous++;
                    else           result.Unmatched++;
                    if (result.UnmatchedTitles.Count < 100)
                        result.UnmatchedTitles.Add($"{name}{(year.HasValue ? $" ({year})" : "")}");
                    continue;
                }

                // Check if already rated by this user — counts as Update vs Imported
                var existing = await _ratingRepo.GetRatingsAsync(matched.Id.ToString("N")).ConfigureAwait(false);
                var userHad  = existing.UserRatings?.Any(r => r.UserId == userId) == true;

                await _ratingRepo.SaveRatingAsync(matched.Id.ToString("N"), userId, userName, stars).ConfigureAwait(false);

                if (userHad) result.Updated++;
                else         result.Imported++;
            }

            _logger.LogInformation("[StarTrack] Letterboxd CSV import done: imported={I}, updated={U}, unmatched={N}, ambiguous={A}",
                result.Imported, result.Updated, result.Unmatched, result.Ambiguous);

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

            foreach (var entry in toImport)
            {
                var matched = FindMovie(entry.FilmTitle, entry.FilmYear, out var ambiguous);
                if (matched == null)
                {
                    if (ambiguous) result.Ambiguous++;
                    else           result.Unmatched++;
                    if (result.UnmatchedTitles.Count < 100)
                        result.UnmatchedTitles.Add($"{entry.FilmTitle}{(entry.FilmYear.HasValue ? $" ({entry.FilmYear})" : "")}");
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
        /// Finds a single Movie in the Jellyfin library matching the given
        /// (title, year). Returns null if no match or multiple matches (in
        /// which case <paramref name="ambiguous"/> is set).
        /// </summary>
        private BaseItem? FindMovie(string title, int? year, out bool ambiguous)
        {
            ambiguous = false;
            if (string.IsNullOrWhiteSpace(title)) return null;

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive        = true
            };
            if (year.HasValue)
                query.Years = new[] { year.Value };

            var candidates = _libraryManager.GetItemList(query);

            // Exact case-insensitive title match
            var exact = candidates
                .Where(c => c != null && string.Equals(NormalizeTitle(c.Name), NormalizeTitle(title), StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (exact.Count == 1) return exact[0];
            if (exact.Count > 1)
            {
                ambiguous = true;
                return null;
            }

            // No exact match — try without year, if a year was specified
            if (year.HasValue)
            {
                var query2 = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie },
                    Recursive        = true
                };
                var allMovies = _libraryManager.GetItemList(query2);
                var byTitle = allMovies
                    .Where(c => c != null && string.Equals(NormalizeTitle(c.Name), NormalizeTitle(title), StringComparison.OrdinalIgnoreCase))
                    .ToList();
                // Accept only if year is within +/- 1 (Letterboxd sometimes disagrees with TMDb)
                var nearYear = byTitle
                    .Where(c => c.ProductionYear.HasValue && Math.Abs(c.ProductionYear.Value - year.Value) <= 1)
                    .ToList();
                if (nearYear.Count == 1) return nearYear[0];
                if (nearYear.Count > 1)  { ambiguous = true; return null; }
            }

            return null;
        }

        private static string NormalizeTitle(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            // Strip common punctuation + collapse whitespace for more forgiving matches
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (char.IsLetterOrDigit(ch) || ch == ' ') sb.Append(ch);
                else if (ch == '-' || ch == '_' || ch == ':' || ch == ',' || ch == '.') sb.Append(' ');
            }
            return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
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
