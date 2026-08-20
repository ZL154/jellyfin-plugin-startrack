using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.InternalRating.ExternalSync;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using Xunit;

namespace InternalRatingSystem.Tests
{
    /// <summary>
    /// The credential-free export path: a CSV the user uploads at
    /// letterboxd.com/import.
    ///
    /// The quoting tests are the important ones. Reviews are free text and
    /// routinely contain commas, quotes and newlines; a quoting bug shifts every
    /// following column, so the import silently lands ratings against the wrong
    /// films rather than failing loudly.
    /// </summary>
    public class LetterboxdCsvExportTests
    {
        private static ExternalRating Movie(
            double stars, string title = "Inception", int? tmdb = 27205,
            string? imdb = "tt1375666", int? year = 2010, DateTime? at = null, string type = "movie")
            => new(imdb, tmdb, null, title, year, type, stars,
                   at ?? new DateTime(2026, 8, 11, 20, 0, 0, DateTimeKind.Utc));

        private static string[] Rows(string csv) =>
            csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        [Fact]
        public void EmitsTheHeaderLetterboxdExpects()
        {
            var csv = LetterboxdCsvExporter.Build(new[] { Movie(4.0) });
            Assert.StartsWith("tmdbID,imdbID,Title,Year,Rating10,WatchedDate,Rewatch,Review", csv);
        }

        [Fact]
        public void WritesOneRowPerMovie()
        {
            var csv = LetterboxdCsvExporter.Build(new[] { Movie(4.0), Movie(3.0, "Dune", 438631, "tt1160419", 2021) });
            Assert.Equal(3, Rows(csv).Length);   // header + 2
        }

        // ---- rating scale ----

        [Theory]
        [InlineData(0.5,  1)]
        [InlineData(2.5,  5)]
        [InlineData(4.0,  8)]
        [InlineData(5.0, 10)]
        public void Rating10_IsDoubledFromHalfStars(double stars, int expected)
        {
            // The import column is 0-10, like the /rate/ endpoint and UNLIKE the
            // diary API which takes 0.5-5.0 raw. Mixing these up halves or
            // doubles every rating a user imports.
            Assert.Equal(expected, LetterboxdCsvExporter.ToRating10(stars));
        }

        [Fact]
        public void Rating10_ClampsAndFloorsSanely()
        {
            Assert.Equal(0,  LetterboxdCsvExporter.ToRating10(0));
            Assert.Equal(10, LetterboxdCsvExporter.ToRating10(9));
        }

        // ---- CSV quoting ----

        [Fact]
        public void Quoting_LeavesPlainValuesAlone()
        {
            Assert.Equal("Inception", LetterboxdCsvExporter.Csv("Inception"));
            Assert.Equal(string.Empty, LetterboxdCsvExporter.Csv(null));
        }

        [Fact]
        public void Quoting_WrapsValuesContainingCommas()
        {
            Assert.Equal("\"Lock, Stock and Two Smoking Barrels\"",
                LetterboxdCsvExporter.Csv("Lock, Stock and Two Smoking Barrels"));
        }

        [Fact]
        public void Quoting_DoublesEmbeddedQuotes()
        {
            // RFC 4180: a literal quote is escaped by doubling it.
            Assert.Equal("\"He said \"\"hi\"\"\"", LetterboxdCsvExporter.Csv("He said \"hi\""));
        }

        [Fact]
        public void Quoting_HandlesNewlinesInsideReviews()
        {
            var quoted = LetterboxdCsvExporter.Csv("line one\nline two");
            Assert.StartsWith("\"", quoted);
            Assert.EndsWith("\"", quoted);
            Assert.Contains("\n", quoted);   // preserved inside the quoted field
        }

        [Fact]
        public void Review_WithCommas_DoesNotShiftColumns()
        {
            var reviews = new Dictionary<int, string> { [27205] = "Great, if long, and loud" };
            var csv = LetterboxdCsvExporter.Build(new[] { Movie(4.0) }, reviews);
            var row = Rows(csv)[1];

            // The unquoted prefix must still be exactly 7 commas' worth of columns
            // before the quoted review begins.
            Assert.Contains("\"Great, if long, and loud\"", row);
            Assert.StartsWith("27205,tt1375666,Inception,2010,8,2026-08-11,false,", row);
        }

        // ---- filtering ----

        [Fact]
        public void SkipsNonMovies()
        {
            // Letterboxd is a film service; a series would fail to import or
            // match an unrelated film of the same name.
            var csv = LetterboxdCsvExporter.Build(new[]
            {
                Movie(4.0),
                Movie(5.0, "Breaking Bad", 1396, "tt0903747", 2008, type: "show")
            });
            Assert.Equal(2, Rows(csv).Length);   // header + the movie only
            Assert.DoesNotContain("Breaking Bad", csv);
        }

        [Fact]
        public void SkipsRowsWithNothingToMatchOn()
        {
            var useless = new ExternalRating(null, null, null, "   ", null, "movie", 4.0, DateTime.UtcNow);
            var csv = LetterboxdCsvExporter.Build(new[] { useless });
            Assert.Single(Rows(csv));   // header only
        }

        [Fact]
        public void KeepsRowsWithATitleButNoTmdbId()
        {
            // Title+Year is a weaker match but Letterboxd's importer accepts it,
            // so it beats silently dropping the rating.
            var noId = new ExternalRating(null, null, null, "Obscure Film", 1974, "movie", 3.5,
                                          new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            var csv = LetterboxdCsvExporter.Build(new[] { noId });
            var row = Rows(csv)[1];

            Assert.StartsWith(",,Obscure Film,1974,7,2026-01-02,false,", row);
        }

        [Fact]
        public void WatchedDate_IsIsoDateOnly()
        {
            var csv = LetterboxdCsvExporter.Build(new[] { Movie(4.0) });
            Assert.Contains("2026-08-11", Rows(csv)[1]);
            Assert.DoesNotContain("20:00", Rows(csv)[1]);
        }
    }
}
