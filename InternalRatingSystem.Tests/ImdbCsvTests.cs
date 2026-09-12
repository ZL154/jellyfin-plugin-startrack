using System;
using Jellyfin.Plugin.InternalRating.Controllers;
using Jellyfin.Plugin.InternalRating.ExternalSync;
using Xunit;

namespace Jellyfin.Plugin.InternalRating.Tests
{
    /// <summary>
    /// Reading an imdb.com ratings export, and reporting what the matching
    /// export could not carry.
    ///
    /// StarTrack could already WRITE the IMDb format but not read it, so a member
    /// with years of IMDb ratings had to launder them through a third service to
    /// get them in — which is how one reporter's 1300 ratings arrived via Simkl.
    /// These tests pin the shape of the file IMDb actually ships.
    /// </summary>
    public class ImdbCsvTests
    {
        // A real export's header, with the columns IMDb currently emits.
        private const string ImdbExport =
            "Const,Your Rating,Date Rated,Title,Original Title,URL,Title Type,IMDb Rating,Runtime (mins),Year,Genres,Num Votes,Release Date,Directors\n" +
            "tt0111161,10,2023-05-01,The Shawshank Redemption,The Shawshank Redemption,https://www.imdb.com/title/tt0111161/,Movie,9.3,142,1994,Drama,2800000,1994-09-23,Frank Darabont\n" +
            "tt0903747,9,2023-06-11,Breaking Bad,Breaking Bad,https://www.imdb.com/title/tt0903747/,TV Series,9.5,49,2008,Drama,2000000,2008-01-20,\n" +
            "tt2301451,8,2023-07-22,Ozymandias,Ozymandias,https://www.imdb.com/title/tt2301451/,TV Episode,10.0,47,2013,Drama,150000,2013-09-15,Rian Johnson\n";

        // ---- parsing ----

        [Fact]
        public void ReadsIdRatingTitleAndYear()
        {
            var rows = new FileExportService().ParseImdbCsv(ImdbExport);

            Assert.Equal(3, rows.Count);
            var shawshank = Assert.Single(rows, r => r.Imdb == "tt0111161");
            Assert.Equal("The Shawshank Redemption", shawshank.Title);
            Assert.Equal(1994, shawshank.Year);
            Assert.Equal("movie", shawshank.MediaType);
            Assert.Equal(new DateTime(2023, 5, 1), shawshank.RatedAt.Date);
        }

        [Fact]
        public void HalvesTheTenPointScaleExactly()
        {
            // IMDb 1-10 and StarTrack 0.5-5 are the same ten positions. A 10 must
            // not arrive as ten stars, and a 9 must be 4.5 — not 4, not 5.
            var rows = new FileExportService().ParseImdbCsv(ImdbExport);

            Assert.Equal(5.0, Assert.Single(rows, r => r.Imdb == "tt0111161").Stars);
            Assert.Equal(4.5, Assert.Single(rows, r => r.Imdb == "tt0903747").Stars);
            Assert.Equal(4.0, Assert.Single(rows, r => r.Imdb == "tt2301451").Stars);
        }

        [Fact]
        public void MapsTitleTypeToMediaType()
        {
            var rows = new FileExportService().ParseImdbCsv(ImdbExport);

            Assert.Equal("movie", Assert.Single(rows, r => r.Imdb == "tt0111161").MediaType);
            Assert.Equal("show",  Assert.Single(rows, r => r.Imdb == "tt0903747").MediaType);
            // "TV Episode" must not collapse into a show, or an episode rating
            // lands on the whole series.
            Assert.Equal("episode", Assert.Single(rows, r => r.Imdb == "tt2301451").MediaType);
        }

        [Fact]
        public void MatchesColumnsByNameNotPosition()
        {
            // IMDb has shipped more than one column order. Position-based parsing
            // would read the wrong field as the title here.
            var reordered =
                "Title,Year,Title Type,Const,Your Rating\n" +
                "Heat,1995,Movie,tt0113277,8\n";

            var row = Assert.Single(new FileExportService().ParseImdbCsv(reordered));
            Assert.Equal("tt0113277", row.Imdb);
            Assert.Equal("Heat", row.Title);
            Assert.Equal(1995, row.Year);
            Assert.Equal(4.0, row.Stars);
        }

        [Theory]
        [InlineData("Const,Your Rating\ntt1,0\n")]        // below IMDb's scale
        [InlineData("Const,Your Rating\ntt1,11\n")]       // above it
        [InlineData("Const,Your Rating\ntt1,\n")]         // seen but unrated
        [InlineData("Const,Your Rating\n,7\n")]           // no id
        [InlineData("Const,Your Rating\nnot-an-id,7\n")]  // malformed id
        public void SkipsRowsItCannotTrust(string csv)
        {
            Assert.Empty(new FileExportService().ParseImdbCsv(csv));
        }

        [Fact]
        public void WithoutTheRequiredColumnsReturnsNothing()
        {
            // Never guess at a file that is not an IMDb export.
            var svc = new FileExportService();
            Assert.Empty(svc.ParseImdbCsv("date,title,year,rating\n2024-01-01,Heat,1995,4.0\n"));
            Assert.Empty(svc.ParseImdbCsv(string.Empty));
            Assert.Empty(svc.ParseImdbCsv("Const,Title\ntt1,Heat\n"));
        }

        [Fact]
        public void RoundTripsThroughBuildAndParse()
        {
            var svc = new FileExportService();
            var original = new[]
            {
                new ExternalRating("tt100", null, null, "Heat", 1995, "movie", 4.0, new DateTime(2023, 1, 2)),
                new ExternalRating("tt200", null, null, "Breaking Bad", 2008, "show", 5.0, new DateTime(2023, 2, 3)),
            };

            var back = svc.ParseImdbCsv(svc.BuildImdbCsv(original));

            Assert.Equal(2, back.Count);
            Assert.Equal(4.0, Assert.Single(back, r => r.Imdb == "tt100").Stars);
            Assert.Equal(5.0, Assert.Single(back, r => r.Imdb == "tt200").Stars);
            Assert.Equal("show", Assert.Single(back, r => r.Imdb == "tt200").MediaType);
        }

        // ---- detection: an upload must route itself ----

        [Fact]
        public void AnImdbExportIsRecognisedFromItsHeader()
        {
            Assert.True(ExternalSyncController.LooksLikeImdbCsv(ImdbExport));
            Assert.True(ExternalSyncController.LooksLikeImdbCsv("Const,Your Rating\ntt1,7\n"));
        }

        [Fact]
        public void StarTracksOwnExportIsNotMistakenForOne()
        {
            // Routing a StarTrack CSV through the IMDb parser would silently
            // import nothing, which looks exactly like a broken upload.
            Assert.False(ExternalSyncController.LooksLikeImdbCsv("date,title,year,rating\n2024-01-01,Heat,1995,4.0\n"));
            Assert.False(ExternalSyncController.LooksLikeImdbCsv(string.Empty));
            Assert.False(ExternalSyncController.LooksLikeImdbCsv("Const,Title\ntt1,Heat\n"));
        }

        // ---- the export must say what it could not carry ----

        [Fact]
        public void ExportReportsWhatItHadToLeaveOut()
        {
            // The silent-loss bug: four ratings in, one row out, and nothing
            // anywhere explaining the other three.
            var ratings = new[]
            {
                new ExternalRating("tt100", null, null, "Heat",          1995, "movie",   4.0, new DateTime(2023, 1, 2)),
                new ExternalRating("tt300", null, null, "Ozymandias",    2013, "episode", 5.0, new DateTime(2023, 3, 4)),
                new ExternalRating("tt301", null, null, "Felina",        2013, "episode", 5.0, new DateTime(2023, 3, 5)),
                new ExternalRating(null,    1,    null, "No Imdb Movie", 2000, "movie",   2.0, new DateTime(2023, 4, 5)),
            };

            new FileExportService().BuildImdbCsv(ratings, out var summary);

            Assert.Equal(1, summary.Written);
            Assert.Equal(2, summary.SkippedEpisodes);
            Assert.Equal(1, summary.SkippedNoImdbId);
            Assert.Equal(4, summary.Total);
            Assert.True(summary.AnySkipped);
        }

        [Fact]
        public void WhenNothingIsDroppedNothingIsReportedSkipped()
        {
            var ratings = new[]
            {
                new ExternalRating("tt100", null, null, "Heat", 1995, "movie", 4.0, new DateTime(2023, 1, 2)),
            };

            var csv = new FileExportService().BuildImdbCsv(ratings, out var summary);

            Assert.Equal(1, summary.Written);
            Assert.False(summary.AnySkipped);
            Assert.Contains("tt100", csv);
        }

        [Fact]
        public void TheOldSingleArgumentOverloadStillBehavesIdentically()
        {
            var ratings = new[]
            {
                new ExternalRating("tt100", null, null, "Heat",       1995, "movie",   4.0, new DateTime(2023, 1, 2)),
                new ExternalRating("tt300", null, null, "Ozymandias", 2013, "episode", 5.0, new DateTime(2023, 3, 4)),
            };
            var svc = new FileExportService();

            Assert.Equal(svc.BuildImdbCsv(ratings, out _), svc.BuildImdbCsv(ratings));
        }
    }
}
