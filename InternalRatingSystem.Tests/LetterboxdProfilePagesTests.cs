using System;
using System.Linq;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using Xunit;

namespace Jellyfin.Plugin.InternalRating.Tests
{
    /// <summary>
    /// The readers behind "Sync everything". Fixtures are trimmed from real
    /// /films/diary/ and /films/ratings/ pages fetched on 2026-09-14, with the
    /// long JSON poster attributes removed and every attribute we read intact.
    /// </summary>
    public class LetterboxdProfilePagesTests
    {
        // A diary row with a rating, a like, a rewatch and a review link.
        private const string DiaryRowFull =
            "<tr class=\"diary-entry-row viewing-poster-container\" data-viewing-id=\"1\">" +
            "<td class=\"col-monthdate -align-center\"><a href=\"/h201ha/diary/films/for/2026/06/\">Jun 2026</a></td>" +
            "<td class=\"col-daydate -align-center -p\"><a class=\"daydate\" href=\"/h201ha/diary/films/for/2026/06/04/\">04</a></td>" +
            "<td class=\"col-production js-td-product\"><div class=\"react-component\" data-component-class=\"LazyPoster\" data-item-name=\"Death Note (2006)\" data-item-slug=\"death-note-2006\" data-item-link=\"/film/death-note-2006/\"></div></td>" +
            "<td class=\"col-releaseyear -align-center\">2006</td>" +
            "<td class=\"col-rating -padding-inline-l\"><div class=\"rating-green\"><div class=\"hide-for-owner\" data-owner=\"h201ha\"><span class=\"rating rated-9\"> ★★★★½ </span></div></div></td>" +
            "<td class=\"col-like -align-center -padd\"><span class=\"has-icon icon-16 icon-liked hide-for-owner\" data-owner=\"h201ha\"><span class=\"icon\"></span></span></td>" +
            "<td class=\"col-rewatch -align-center -p\"><span class=\"has-icon icon-rewatch icon-16\"><span class=\"icon\"></span></span></td>" +
            "<td class=\"col-review -align-center -pa\"><a href=\"/h201ha/film/death-note-2006/\" class=\"has-icon icon-review icon-16 tooltip\">Read the review</a></td>" +
            "</tr>";

        // A plain row: no rating, no like, no rewatch, no review.
        private const string DiaryRowBare =
            "<tr class=\"diary-entry-row\">" +
            "<td class=\"col-daydate -align-center -p\"><a class=\"daydate\" href=\"/h201ha/diary/films/for/2026/09/11/\">11</a></td>" +
            "<td class=\"col-production js-td-product\"><div class=\"react-component\" data-item-name=\"Real Steel &amp; Friends (2011)\" data-item-slug=\"real-steel\"></div></td>" +
            "<td class=\"col-rating -padding-inline-l\"><div class=\"rating-green\"></div></td>" +
            "<td class=\"col-like -align-center -padd\"></td><td class=\"col-rewatch -align-center -p\"></td><td class=\"col-review -align-center -pa\"></td>" +
            "</tr>";

        private const string DiaryPage = "<html><table>" + DiaryRowFull + DiaryRowBare + "</table><a href=\"/h201ha/films/diary/page/3/\">3</a></html>";

        private const string RatingsPage =
            "<html><ul class=\"grid -p70\">" +
            "<li class=\"griditem\"><div class=\"react-component\" data-component-class=\"LazyPoster\" data-item-name=\"Spider-Man: Brand New Day (2026)\" data-item-slug=\"spider-man-brand-new-day\"></div>" +
            "<p class=\"poster-viewingdata\" data-item-uid=\"film:872871\"><span class=\"rating -micro -darker rated-7\">★★★½</span></p></li>" +
            "<li class=\"griditem\"><div class=\"react-component\" data-item-name=\"Heat (1995)\" data-item-slug=\"heat-1995\"></div>" +
            "<p class=\"poster-viewingdata\"><span class=\"rating -micro -darker rated-10\">★★★★★</span></p></li>" +
            "<li class=\"griditem\"><div class=\"react-component\" data-item-name=\"Unrated Thing (2001)\" data-item-slug=\"unrated-thing\"></div><p class=\"poster-viewingdata\"></p></li>" +
            "<li class=\"griditem\"><div class=\"react-component\" data-item-name=\"Heat (1995)\" data-item-slug=\"heat-1995\"></div><p><span class=\"rating rated-10\">★★★★★</span></p></li>" +
            "</ul></html>";

        // ---- diary ----

        [Fact]
        public void DiaryRowReadsDateFilmRatingAndFlags()
        {
            var rows = LetterboxdProfilePages.ParseDiaryPage(DiaryPage);
            var dn = Assert.Single(rows, r => r.Slug == "death-note-2006");

            Assert.Equal("Death Note", dn.Title);
            Assert.Equal(2006, dn.Year);
            Assert.Equal(new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc), dn.WatchedOn);
            Assert.Equal(4.5, dn.Stars);          // rated-9 → 4.5
            Assert.True(dn.Rewatch);
            Assert.True(dn.Liked);
            Assert.True(dn.HasReview);
        }

        [Fact]
        public void DiaryRowWithoutARatingIsKeptAsUnrated()
        {
            // A logged viewing with no score is still a viewing.
            var rows = LetterboxdProfilePages.ParseDiaryPage(DiaryPage);
            var rs = Assert.Single(rows, r => r.Slug == "real-steel");

            Assert.Equal("Real Steel & Friends", rs.Title);   // entity decoded
            Assert.Null(rs.Stars);
            Assert.False(rs.Rewatch);
            Assert.False(rs.Liked);
            Assert.False(rs.HasReview);
            Assert.Equal(new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc), rs.WatchedOn);
        }

        [Fact]
        public void DiaryDateComesFromTheDayLinkNotTheMonthCell()
        {
            // The month cell is only rendered on the first row of a month;
            // the day link carries the full date on every row.
            var rows = LetterboxdProfilePages.ParseDiaryPage(DiaryPage);
            Assert.All(rows, r => Assert.NotEqual(default, r.WatchedOn));
        }

        // ---- ratings ----

        [Fact]
        public void RatingsPageReadsEveryRatedFilmOnce()
        {
            var films = LetterboxdProfilePages.ParseRatingsPage(RatingsPage);

            Assert.Equal(2, films.Count);                              // Heat listed twice → once; unrated skipped
            Assert.Equal(3.5, Assert.Single(films, f => f.Slug == "spider-man-brand-new-day").Stars);
            Assert.Equal(5.0, Assert.Single(films, f => f.Slug == "heat-1995").Stars);
            Assert.Equal(2026, Assert.Single(films, f => f.Slug == "spider-man-brand-new-day").Year);
        }

        [Fact]
        public void NothingToReadReturnsEmpty()
        {
            Assert.Empty(LetterboxdProfilePages.ParseDiaryPage(string.Empty));
            Assert.Empty(LetterboxdProfilePages.ParseRatingsPage("<html><title>Just a moment...</title></html>"));
        }

        // ---- rendered in Letterboxd's own CSV layouts ----

        [Fact]
        public void RatingsCsvDatesDiariedFilmsFromTheDiaryAndSlotsTheRestInListOrder()
        {
            var films = LetterboxdProfilePages.ParseRatingsPage(RatingsPage);   // newest-rated first: Spider-Man, Heat, ...
            var diary = new[]
            {
                new LetterboxdDiaryPageRow("Heat", 1995, "heat-1995", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 5.0, false, false, false),
                new LetterboxdDiaryPageRow("Heat", 1995, "heat-1995", new DateTime(2025, 3, 3, 0, 0, 0, DateTimeKind.Utc), 5.0, true,  false, false),
            };
            var now = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
            var csv = LetterboxdProfilePages.RenderRatingsCsv(films, diary, now);

            var lines = csv.TrimEnd('\n').Split('\n');
            Assert.Equal("Date,Name,Year,Letterboxd URI,Rating,Date Source", lines[0]);
            Assert.Contains("2025-03-03T00:00:00Z,Heat,1995,https://letterboxd.com/film/heat-1995/,5,diary", lines);   // latest diary date, real

            // Never diaried, but rated more recently than Heat (it is above Heat in
            // the rated-date list): an ESTIMATE between Heat's date and now, flagged
            // so the importer never moves a rating the user already has with it.
            var spidey = Assert.Single(lines, l => l.Contains("Spider-Man: Brand New Day"));
            Assert.EndsWith(",3.5,estimate", spidey);
            var when = DateTime.Parse(spidey.Split(',')[0], null, System.Globalization.DateTimeStyles.AdjustToUniversal);
            Assert.InRange(when, new DateTime(2025, 3, 3, 0, 0, 1, DateTimeKind.Utc), now);
        }

        [Fact]
        public void DiaryCsvMatchesTheExportLayout()
        {
            var rows = LetterboxdProfilePages.ParseDiaryPage(DiaryPage);
            var csv = LetterboxdProfilePages.RenderDiaryCsv(rows);
            var lines = csv.TrimEnd('\n').Split('\n');

            Assert.Equal("Date,Name,Year,Letterboxd URI,Rating,Rewatch,Tags,Watched Date", lines[0]);
            Assert.Contains("2026-06-04,Death Note,2006,https://letterboxd.com/film/death-note-2006/,4.5,Yes,,2026-06-04", lines);
            // No comma or quote in the title, so no quoting; empty rating; not a rewatch.
            Assert.Contains("2026-09-11,Real Steel & Friends,2011,https://letterboxd.com/film/real-steel/,,No,,2026-09-11", lines);
        }

        [Fact]
        public void TitlesWithCommasAreQuoted()
        {
            var csv = LetterboxdProfilePages.RenderRatingsCsv(
                new[] { new LetterboxdRatedFilm("Me, Myself & Irene", 2000, "me-myself-irene", 3.0) },
                Array.Empty<LetterboxdDiaryPageRow>(), new DateTime(2026, 1, 1));
            Assert.Contains("\"Me, Myself & Irene\"", csv);
        }
    }
}
