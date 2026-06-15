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
        /// </summary>
        private static string CsvEscape(string value)
        {
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
