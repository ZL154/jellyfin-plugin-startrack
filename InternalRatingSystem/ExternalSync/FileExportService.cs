using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.InternalRating.ExternalSync
{
    /// <summary>
    /// Converts a list of <see cref="ExternalRating"/> records to/from
    /// Letterboxd-compatible CSV and JSON, with no I/O or DI dependencies.
    /// </summary>
    public sealed class FileExportService
    {
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // ------------------------------------------------------------------ //
        // CSV
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Serialises <paramref name="ratings"/> as a Letterboxd-format CSV string.
        /// Header: <c>Date,Name,Year,Rating</c>
        /// </summary>
        public string BuildLetterboxdCsv(IReadOnlyList<ExternalRating> ratings)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Date,Name,Year,Rating");

            foreach (var r in ratings)
            {
                var date = r.RatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var name = CsvEscape(r.Title);
                var year = r.Year.HasValue
                    ? r.Year.Value.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;
                var stars = FormatStars(r.Stars);

                sb.AppendLine($"{date},{name},{year},{stars}");
            }

            // Trim the final trailing newline so callers get a clean string.
            return sb.ToString().TrimEnd('\r', '\n');
        }

        /// <summary>
        /// Parses a Letterboxd-format CSV string back into <see cref="ExternalRating"/> records.
        /// The header row is skipped automatically.
        /// Provider IDs (Imdb/Tmdb/Tvdb) will be <c>null</c>; MediaType will be "movie".
        /// </summary>
        public IReadOnlyList<ExternalRating> ParseCsv(string csv)
        {
            var result = new List<ExternalRating>();
            var lines = csv.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

            bool firstLine = true;
            foreach (var line in lines)
            {
                if (firstLine)
                {
                    // Skip the header row
                    firstLine = false;
                    continue;
                }

                var fields = SplitCsvLine(line);
                if (fields.Count < 4)
                    continue;

                if (!DateTime.TryParseExact(fields[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    continue;

                var title = fields[1];

                int? year = null;
                if (!string.IsNullOrWhiteSpace(fields[2]) &&
                    int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedYear))
                {
                    year = parsedYear;
                }

                if (!double.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var stars))
                    continue;

                result.Add(new ExternalRating(null, null, null, title, year, "movie", stars, date));
            }

            return result;
        }

        /// <summary>
        /// Serialises <paramref name="ratings"/> as an IMDb-ratings-format CSV that
        /// Yamtrack's "Import from IMDb" reads. Columns: <c>Const,Title,Title Type,
        /// Your Rating,Date Rated,Created,Modified,Year</c>. Rating is on IMDb's 1–10
        /// scale; dates are <c>yyyy-MM-dd</c>. Items without an IMDb id are skipped
        /// (the importer keys on <c>Const</c>), and episodes are skipped (Yamtrack's
        /// IMDb importer doesn't accept TV Episode).
        /// </summary>
        public string BuildImdbCsv(IReadOnlyList<ExternalRating> ratings)
            => BuildImdbCsv(ratings, out _);

        /// <summary>
        /// What an IMDb export actually contained, and what it could not carry.
        ///
        /// WHY THIS EXISTS: the export drops rows for two unavoidable reasons —
        /// no IMDb id (the format keys on <c>Const</c>, so a row without one
        /// cannot be written at all) and episodes (Yamtrack's IMDb importer
        /// rejects <c>TV Episode</c>). Both are correct. Both were also
        /// completely silent: a member could export 900 ratings, receive 400,
        /// and have nothing anywhere telling them why the other 500 vanished —
        /// which reads as data loss rather than a format limit.
        /// </summary>
        /// <param name="Written">Rows actually in the CSV.</param>
        /// <param name="SkippedNoImdbId">Dropped because the item has no IMDb id in Jellyfin.</param>
        /// <param name="SkippedEpisodes">Dropped because the target importer has no episode row type.</param>
        public readonly record struct ImdbExportSummary(int Written, int SkippedNoImdbId, int SkippedEpisodes)
        {
            /// <summary>Total rows offered to the exporter.</summary>
            public int Total => Written + SkippedNoImdbId + SkippedEpisodes;

            /// <summary>True when anything at all was left out.</summary>
            public bool AnySkipped => SkippedNoImdbId > 0 || SkippedEpisodes > 0;
        }

        /// <summary>
        /// As <see cref="BuildImdbCsv(IReadOnlyList{ExternalRating})"/>, but also
        /// reports what was left out so the caller can say so instead of handing
        /// back a quietly shorter file.
        /// </summary>
        public string BuildImdbCsv(IReadOnlyList<ExternalRating> ratings, out ImdbExportSummary summary)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Const,Title,Title Type,Your Rating,Date Rated,Created,Modified,Year");

            int written = 0, noId = 0, episodes = 0;

            foreach (var r in ratings)
            {
                // Order matters for honest counting: an episode with no IMDb id
                // is reported once, as an episode, because that is the reason the
                // user can do nothing about.
                string? titleType = r.MediaType switch
                {
                    "movie" => "Movie",
                    "show"  => "TV Series",
                    _        => null // episodes/other not supported by Yamtrack's IMDb import
                };
                if (titleType == null)
                {
                    episodes++;
                    continue;
                }

                if (string.IsNullOrEmpty(r.Imdb))
                {
                    noId++;       // IMDb import matches on Const (the IMDb id)
                    continue;
                }

                var date     = r.RatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var rating10 = RatingScale.ToService10(r.Stars).ToString(CultureInfo.InvariantCulture);
                var year     = r.Year.HasValue ? r.Year.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;

                sb.AppendLine($"{r.Imdb},{CsvEscape(r.Title)},{titleType},{rating10},{date},{date},{date},{year}");
                written++;
            }

            summary = new ImdbExportSummary(written, noId, episodes);
            return sb.ToString().TrimEnd('\r', '\n');
        }

        /// <summary>
        /// Parses an IMDb ratings export (<c>ratings.csv</c> from
        /// imdb.com → Your Ratings → Export) into <see cref="ExternalRating"/> rows.
        ///
        /// The inverse of <see cref="BuildImdbCsv(IReadOnlyList{ExternalRating})"/>,
        /// and the gap it closes is real: StarTrack could already WRITE this
        /// format but not read it, so a member with years of IMDb ratings had to
        /// launder them through a third service to get them in. One reporter did
        /// exactly that with 1300 ratings.
        ///
        /// Column names are matched from the header rather than by position:
        /// IMDb has shipped at least two column orders, and Yamtrack and Simkl
        /// both emit near-miss variants of the same file. Required: <c>Const</c>
        /// and <c>Your Rating</c>. Everything else is optional.
        ///
        /// Ratings are IMDb's 1-10 and are halved onto StarTrack's 0.5-5, which
        /// is exact. Rows with no id, no rating, or an out-of-range rating are
        /// skipped rather than guessed at.
        /// </summary>
        public IReadOnlyList<ExternalRating> ParseImdbCsv(string csv)
        {
            var result = new List<ExternalRating>();
            if (string.IsNullOrWhiteSpace(csv)) return result;

            var lines = csv.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2) return result;

            // Header -> index, case/space-insensitive so "Your Rating",
            // "your rating" and "YourRating" all resolve.
            var header = SplitCsvLine(lines[0]);
            var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count; i++)
            {
                var key = header[i].Replace(" ", string.Empty).Trim();
                if (key.Length > 0 && !col.ContainsKey(key)) col[key] = i;
            }

            int Idx(params string[] names)
            {
                foreach (var n in names)
                    if (col.TryGetValue(n, out var i)) return i;
                return -1;
            }

            var iConst  = Idx("Const", "imdbID", "IMDbID", "tconst");
            var iRating = Idx("YourRating", "Rating", "MyRating");
            var iTitle  = Idx("Title", "OriginalTitle", "PrimaryTitle", "Name");
            var iType   = Idx("TitleType", "Type");
            var iYear   = Idx("Year", "StartYear", "ReleaseYear");
            var iDate   = Idx("DateRated", "Created", "Modified", "Date");

            // Without an id and a score there is nothing to import.
            if (iConst < 0 || iRating < 0) return result;

            string? Get(IReadOnlyList<string> f, int i)
                => (i >= 0 && i < f.Count) ? f[i] : null;

            for (var li = 1; li < lines.Length; li++)
            {
                var f = SplitCsvLine(lines[li]);
                if (f.Count == 0) continue;

                var imdb = Get(f, iConst)?.Trim();
                if (string.IsNullOrWhiteSpace(imdb) || !imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                    continue;

                var rawRating = Get(f, iRating)?.Trim();
                if (!double.TryParse(rawRating, NumberStyles.Float, CultureInfo.InvariantCulture, out var ten))
                    continue;
                if (ten < 1 || ten > 10) continue;

                // IMDb 1-10 -> StarTrack 0.5-5. Exact: the two scales are the
                // same ten positions.
                var stars = RatingScale.FromService10((int)Math.Round(ten, MidpointRounding.AwayFromZero));

                var title = Get(f, iTitle)?.Trim() ?? string.Empty;

                int? year = null;
                if (int.TryParse(Get(f, iYear), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
                    year = y;

                // IMDb writes "TV Series", "TV Mini Series", "TV Episode",
                // "Movie", "TV Movie", "Short", "Video"... Anything that is a
                // series-shaped thing becomes "show"; the rest are treated as
                // films, which is also the right default when the column is
                // missing entirely.
                var rawType = Get(f, iType)?.Trim() ?? string.Empty;
                var mediaType =
                    rawType.Contains("Episode", StringComparison.OrdinalIgnoreCase) ? "episode" :
                    rawType.Contains("Series",  StringComparison.OrdinalIgnoreCase) ? "show"    :
                    "movie";

                var ratedAt = DateTime.UtcNow;
                var rawDate = Get(f, iDate);
                if (!string.IsNullOrWhiteSpace(rawDate) &&
                    DateTime.TryParse(rawDate, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
                {
                    ratedAt = d;
                }

                result.Add(new ExternalRating(imdb, null, null, title, year, mediaType, stars, ratedAt));
            }

            return result;
        }

        // ------------------------------------------------------------------ //
        // JSON
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Serialises <paramref name="ratings"/> as an indented camelCase JSON array.
        /// </summary>
        public string BuildJson(IReadOnlyList<ExternalRating> ratings)
            => JsonSerializer.Serialize(ratings, _jsonOptions);

        /// <summary>
        /// Deserialises a JSON array produced by <see cref="BuildJson"/> back into
        /// a list of <see cref="ExternalRating"/> records.
        /// </summary>
        public IReadOnlyList<ExternalRating> ParseJson(string json)
            => JsonSerializer.Deserialize<List<ExternalRating>>(json, _jsonOptions)
               ?? new List<ExternalRating>();

        // ------------------------------------------------------------------ //
        // Private helpers
        // ------------------------------------------------------------------ //

        /// <summary>
        /// RFC 4180 CSV escaping: if the value contains a comma, double-quote, or
        /// newline it is wrapped in double-quotes and any internal double-quotes are
        /// doubled.
        /// Additionally, values starting with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>,
        /// or a tab are prefixed with a single quote to neutralise spreadsheet
        /// formula injection (CSV injection / formula injection guard).
        /// </summary>
        private static string CsvEscape(string value)
        {
            // Formula-injection guard: prefix dangerous leading characters with a single quote
            // so spreadsheet applications treat the cell as literal text.
            if (value.Length > 0 && (value[0] == '=' || value[0] == '+' || value[0] == '-' || value[0] == '@' || value[0] == '\t'))
            {
                value = "'" + value;
            }

            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
        }

        /// <summary>
        /// Splits a single CSV line into fields, correctly handling quoted fields
        /// that may contain commas or doubled double-quotes.
        /// </summary>
        private static List<string> SplitCsvLine(string line)
        {
            var fields = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false;
            int i = 0;

            while (i < line.Length)
            {
                char c = line[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        // Peek ahead: doubled quote → escaped literal quote
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            field.Append('"');
                            i += 2;
                        }
                        else
                        {
                            // Closing quote
                            inQuotes = false;
                            i++;
                        }
                    }
                    else
                    {
                        field.Append(c);
                        i++;
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        inQuotes = true;
                        i++;
                    }
                    else if (c == ',')
                    {
                        fields.Add(field.ToString());
                        field.Clear();
                        i++;
                    }
                    else
                    {
                        field.Append(c);
                        i++;
                    }
                }
            }

            fields.Add(field.ToString());
            return fields;
        }

        /// <summary>
        /// Formats a stars value using invariant culture with no unnecessary trailing
        /// zeros (e.g. 4.5 → "4.5", 3.0 → "3").
        /// </summary>
        private static string FormatStars(double stars)
        {
            // G format removes trailing zeros automatically.
            return stars.ToString("G", CultureInfo.InvariantCulture);
        }
    }
}
