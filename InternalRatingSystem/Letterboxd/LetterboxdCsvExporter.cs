using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Jellyfin.Plugin.InternalRating.ExternalSync;

namespace Jellyfin.Plugin.InternalRating.Letterboxd
{
    /// <summary>
    /// Produces a CSV in the format letterboxd.com/import accepts.
    ///
    /// This is the credential-free route to two-way sync: the user downloads
    /// the file and uploads it at letterboxd.com/import. It cannot be automated,
    /// but it also cannot break, cannot be blocked by Cloudflare, works for
    /// accounts with two-factor authentication (which the session path genuinely
    /// cannot support), and requires nobody to hand StarTrack their Letterboxd
    /// password. Every install gets this; only people who want automation need
    /// to link an account.
    ///
    /// Columns are the ones Letterboxd's importer documents. tmdbID comes first
    /// because it is the only field that identifies a film unambiguously —
    /// Title/Year are a fallback for rows where the library has no TMDb id.
    ///
    /// SCALE: Rating10 is Letterboxd's 0-10 import column, so StarTrack's
    /// 0.5-5.0 half-stars are DOUBLED here. That differs from the diary API
    /// (0.5-5.0 raw) and matches the /rate/ endpoint. See LetterboxdWriteService
    /// for the full table — mixing these up silently halves or doubles ratings.
    /// </summary>
    public static class LetterboxdCsvExporter
    {
        private const string Header = "tmdbID,imdbID,Title,Year,Rating10,WatchedDate,Rewatch,Review";

        /// <summary>
        /// Builds the CSV body. Only movies are included: Letterboxd is a film
        /// service and importing a TV series would either fail or match an
        /// unrelated film with the same name.
        /// </summary>
        /// <param name="ratings">The user's ratings, already resolved to external ids.</param>
        /// <param name="reviews">Optional itemId-independent review lookup keyed by TMDb id.</param>
        public static string Build(
            IReadOnlyList<ExternalRating> ratings,
            IReadOnlyDictionary<int, string>? reviews = null)
        {
            var sb = new StringBuilder();
            sb.Append(Header).Append('\n');

            foreach (var r in ratings)
            {
                if (r.MediaType != "movie") continue;

                // A row with neither a TMDb id nor a title is unusable to the
                // importer; skip rather than emitting a blank line.
                if (r.Tmdb is null && string.IsNullOrWhiteSpace(r.Title)) continue;

                string? review = null;
                if (reviews != null && r.Tmdb is int t) reviews.TryGetValue(t, out review);

                sb.Append(r.Tmdb?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                  .Append(Csv(r.Imdb)).Append(',')
                  .Append(Csv(r.Title)).Append(',')
                  .Append(r.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                  .Append(ToRating10(r.Stars).ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.RatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                  .Append("false").Append(',')
                  .Append(Csv(review))
                  .Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>StarTrack half-stars to Letterboxd's 0-10 import column.</summary>
        internal static int ToRating10(double stars)
        {
            if (stars <= 0) return 0;
            return (int)Math.Clamp(Math.Round(stars * 2, MidpointRounding.AwayFromZero), 1, 10);
        }

        /// <summary>
        /// RFC 4180 quoting. Reviews are free text written by users and routinely
        /// contain commas, quotes and newlines; getting this wrong would shift
        /// every following column and corrupt the import.
        /// </summary>
        internal static string Csv(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var needsQuotes = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            if (!needsQuotes) return value;

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
