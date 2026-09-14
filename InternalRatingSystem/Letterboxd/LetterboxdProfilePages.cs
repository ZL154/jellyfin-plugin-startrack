using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.InternalRating.Letterboxd
{
    /// <summary>One row of a Letterboxd diary page.</summary>
    public readonly record struct LetterboxdDiaryPageRow(
        string Title, int? Year, string Slug, DateTime WatchedOn, double? Stars, bool Rewatch, bool Liked, bool HasReview);

    /// <summary>One film on a Letterboxd ratings page.</summary>
    public readonly record struct LetterboxdRatedFilm(string Title, int? Year, string Slug, double Stars);

    /// <summary>
    /// Readers for the two profile pages that together hold a member's entire
    /// history: <c>/{user}/films/diary/</c> (every logged viewing, with its
    /// date) and <c>/{user}/films/ratings/</c> (every rated film, including
    /// the ones that were rated but never logged — "watched, not sure when").
    ///
    /// WHY: the RSS feed carries only the most recent ~50 diary entries, so a
    /// member who rated 800 films on Letterboxd arrived in StarTrack with a
    /// few hundred and no way to get the rest short of exporting a ZIP by hand
    /// and uploading it. These two pages are the ZIP, live.
    ///
    /// Output is rendered as the same CSV text Letterboxd's own export uses,
    /// so it goes through the existing, tested importers unchanged — same
    /// overwrite policy, same pending queue, same diary merge.
    /// </summary>
    public static class LetterboxdProfilePages
    {
        private static readonly Regex DiaryRow = new(
            "<tr\\b[^>]*\\bclass=\"[^\"]*diary-entry-row[^\"]*\"[^>]*>(.*?)</tr>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex GridItem = new(
            "<li\\b[^>]*\\bclass=\"[^\"]*griditem[^\"]*\"[^>]*>(.*?)</li>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ItemName  = new("\\bdata-item-name=\"([^\"]*)\"", RegexOptions.Compiled);
        private static readonly Regex ItemSlug  = new("\\bdata-item-slug=\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex Rated     = new("\\brated-(\\d{1,2})\\b", RegexOptions.Compiled);
        private static readonly Regex DiaryDate = new("/diary/films/for/(\\d{4})/(\\d{2})/(\\d{2})/", RegexOptions.Compiled);
        private static readonly Regex TrailingYear = new("^(.*?)\\s*\\((\\d{4})\\)\\s*$", RegexOptions.Compiled);

        private static (string Title, int? Year) SplitName(string raw)
        {
            raw = WebUtility.HtmlDecode(raw).Trim();
            var m = TrailingYear.Match(raw);
            if (!m.Success) return (raw, null);
            return (m.Groups[1].Value.Trim(), int.TryParse(m.Groups[2].Value, out var y) ? y : null);
        }

        /// <summary>Letterboxd encodes a rating as <c>rated-N</c>, N = stars × 2.</summary>
        private static double? StarsOf(string html)
        {
            var m = Rated.Match(html);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out var n) || n < 1 || n > 10) return null;
            return n / 2.0;
        }

        /// <summary>Parse one diary page. Rows without a film or a date are skipped.</summary>
        public static List<LetterboxdDiaryPageRow> ParseDiaryPage(string html)
        {
            var rows = new List<LetterboxdDiaryPageRow>();
            if (string.IsNullOrEmpty(html)) return rows;
            foreach (Match row in DiaryRow.Matches(html))
            {
                var r = row.Groups[1].Value;
                var slug = ItemSlug.Match(r).Groups[1].Value;
                var name = ItemName.Match(r).Groups[1].Value;
                var date = DiaryDate.Match(r);
                if (slug.Length == 0 || name.Length == 0 || !date.Success) continue;

                var (title, year) = SplitName(name);
                var watched = new DateTime(
                    int.Parse(date.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(date.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(date.Groups[3].Value, CultureInfo.InvariantCulture),
                    0, 0, 0, DateTimeKind.Utc);

                rows.Add(new LetterboxdDiaryPageRow(
                    title, year, slug, watched, StarsOf(r),
                    Rewatch:   r.Contains("icon-rewatch", StringComparison.OrdinalIgnoreCase),
                    Liked:     r.Contains("icon-liked",   StringComparison.OrdinalIgnoreCase),
                    HasReview: r.Contains("icon-review",  StringComparison.OrdinalIgnoreCase)));
            }
            return rows;
        }

        /// <summary>Parse one ratings page. Films with no rating mark are skipped.</summary>
        public static List<LetterboxdRatedFilm> ParseRatingsPage(string html)
        {
            var films = new List<LetterboxdRatedFilm>();
            if (string.IsNullOrEmpty(html)) return films;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match li in GridItem.Matches(html))
            {
                var block = li.Groups[1].Value;
                var slug = ItemSlug.Match(block).Groups[1].Value;
                var name = ItemName.Match(block).Groups[1].Value;
                var stars = StarsOf(block);
                if (slug.Length == 0 || name.Length == 0 || stars is null || !seen.Add(slug)) continue;
                var (title, year) = SplitName(name);
                films.Add(new LetterboxdRatedFilm(title, year, slug, stars.Value));
            }
            return films;
        }

        // ---- CSV rendering, in Letterboxd's own export layout ----

        private static string Csv(string s)
            => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        private static string Uri(string slug) => "https://letterboxd.com/film/" + slug + "/";

        /// <summary>
        /// <c>ratings.csv</c>: Date, Name, Year, Letterboxd URI, Rating.
        /// The ratings page carries no date, so each film takes the date of its
        /// latest diary entry if it has one, else <paramref name="fallbackDate"/>.
        /// </summary>
        public static string RenderRatingsCsv(IEnumerable<LetterboxdRatedFilm> films, IEnumerable<LetterboxdDiaryPageRow> diary, DateTime fallbackDate)
        {
            var latest = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in diary)
                if (!latest.TryGetValue(d.Slug, out var have) || d.WatchedOn > have) latest[d.Slug] = d.WatchedOn;

            // The films list is read newest-rated first, so a film with no diary
            // entry still has a place in time: between its dated neighbours.
            // Those slotted dates are ESTIMATES and the column says so — the
            // importer uses one only for a rating it does not already have, so
            // nothing real is overwritten and nothing drifts on the next sync.
            var list = films as IList<LetterboxdRatedFilm> ?? films.ToList();
            var known = new DateTime?[list.Count];
            for (var i = 0; i < list.Count; i++)
                known[i] = latest.TryGetValue(list[i].Slug, out var dt) ? dt : null;
            var dates = ListOrderDates.Fill(known, fallbackDate);

            var sb = new StringBuilder("Date,Name,Year,Letterboxd URI,Rating,Date Source\n");
            for (var i = 0; i < list.Count; i++)
            {
                var f = list[i];
                var source = known[i].HasValue ? "diary" : "estimate";
                sb.Append(dates[i].ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append(',')
                  .Append(Csv(f.Title)).Append(',')
                  .Append(f.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                  .Append(Uri(f.Slug)).Append(',')
                  .Append(f.Stars.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(source).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary><c>diary.csv</c>: Date, Name, Year, Letterboxd URI, Rating, Rewatch, Tags, Watched Date.</summary>
        public static string RenderDiaryCsv(IEnumerable<LetterboxdDiaryPageRow> diary)
        {
            var sb = new StringBuilder("Date,Name,Year,Letterboxd URI,Rating,Rewatch,Tags,Watched Date\n");
            foreach (var d in diary)
            {
                var day = d.WatchedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                sb.Append(day).Append(',')
                  .Append(Csv(d.Title)).Append(',')
                  .Append(d.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                  .Append(Uri(d.Slug)).Append(',')
                  .Append(d.Stars?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                  .Append(d.Rewatch ? "Yes" : "No").Append(",,")
                  .Append(day).Append('\n');
            }
            return sb.ToString();
        }
    }

    /// <summary>Where a full profile sync is, for the UI to show and the button to poll.</summary>
    public sealed class LetterboxdFullSyncProgress
    {
        // camelCase on the wire, like every other StarTrack DTO — the widget
        // reads p.running / p.phase, and the first live test broke on this.
        [System.Text.Json.Serialization.JsonPropertyName("running")]      public bool     Running      { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("phase")]        public string   Phase        { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("pagesDone")]    public int      PagesDone    { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("pagesTotal")]   public int      PagesTotal   { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("ratingsFound")] public int      RatingsFound { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("diaryFound")]   public int      DiaryFound   { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("startedAt")]    public DateTime StartedAt    { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("finishedAt")]   public DateTime? FinishedAt  { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("error")]        public string?  Error        { get; set; }
        /// <summary>Set when some pages could not be read: the sync finished, but on part of the profile.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("warning")]      public string?  Warning      { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("result")]       public LetterboxdImportResult? Result { get; set; }

        /// <summary>One in-flight or last-finished sync per user.</summary>
        public static readonly ConcurrentDictionary<string, LetterboxdFullSyncProgress> ByUser = new(StringComparer.OrdinalIgnoreCase);
    }
}
