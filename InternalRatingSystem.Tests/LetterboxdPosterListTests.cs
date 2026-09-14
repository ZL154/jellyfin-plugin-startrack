using Jellyfin.Plugin.InternalRating.Letterboxd;
using Xunit;

namespace Jellyfin.Plugin.InternalRating.Tests
{
    /// <summary>
    /// The poster-list reader that replaced the watchlist RSS (which Letterboxd
    /// removed) and the likes alt-text scrape (which predated the React
    /// markup). The fixtures are trimmed from real pages fetched on
    /// 2026-09-14, attribute order and entity escaping intact.
    /// </summary>
    public class LetterboxdPosterListTests
    {
        // One real LazyPoster island from /h201ha/watchlist/, with the long
        // JSON attributes shortened but every attribute we read left exact.
        private const string Poster1 =
            "<div class=\"react-component\" data-component-class=\"LazyPoster\" data-request-poster-metadata=\"true\" " +
            "data-likeable=\"true\" data-watchable=\"true\" data-rateable=\"true\" data-image-width=\"125\" data-image-height=\"187\" " +
            "data-item-name=\"Spider-Man: Beyond the Spider-Verse (2027)\" data-item-slug=\"spider-man-beyond-the-spider-verse\" " +
            "data-item-link=\"/film/spider-man-beyond-the-spider-verse/\" data-item-full-display-name=\"Spider-Man: Beyond the Spider-Verse (2027)\" " +
            "data-postered-identifier='{&quot;lid&quot;:&quot;ykgU&quot;,&quot;uid&quot;:&quot;film:818108&quot;}' data-target-link=\"/film/spider-man-beyond-the-spider-verse/\">";

        // Attribute order differs page to page; slug before name here, and an
        // HTML entity in the title.
        private const string Poster2 =
            "<div class=\"react-component\" data-item-slug=\"real-steel\" data-component-class=\"LazyPoster\" " +
            "data-item-name=\"Real Steel &amp; Friends (2011)\" data-target-link=\"/film/real-steel/\">";

        // No year in the name — some entries have none.
        private const string Poster3 =
            "<div class=\"react-component\" data-component-class=\"LazyPoster\" data-item-name=\"Untitled Project\" data-item-slug=\"untitled-project\">";

        private const string Page =
            "<html><body><ul class=\"poster-list\">" +
            "<li>" + Poster1 + "</div></li>" +
            "<li>" + Poster2 + "</div></li>" +
            "<li>" + Poster3 + "</div></li>" +
            "<li>" + Poster1 + "</div></li>" +            // duplicate island (Letterboxd renders some twice)
            "</ul>" +
            "<div class=\"paginate-pages\"><ul>" +
            "<li><a href=\"/h201ha/watchlist/page/2/\">2</a></li>" +
            "<li><a href=\"/h201ha/watchlist/page/3/\">3</a></li>" +
            "<li><a href=\"/h201ha/watchlist/page/4/\">4</a></li>" +
            "<li class=\"paginate-nextprev\"><a href=\"/h201ha/watchlist/page/2/\">Next</a></li>" +
            "</ul></div></body></html>";

        [Fact]
        public void ReadsTitleYearAndSlug()
        {
            var items = LetterboxdSyncService.ParsePosterList(Page);

            var sm = Assert.Single(items, i => i.Slug == "spider-man-beyond-the-spider-verse");
            Assert.Equal("Spider-Man: Beyond the Spider-Verse", sm.Title);
            Assert.Equal(2027, sm.Year);
        }

        [Fact]
        public void AttributeOrderDoesNotMatter_AndEntitiesAreDecoded()
        {
            var items = LetterboxdSyncService.ParsePosterList(Page);

            var rs = Assert.Single(items, i => i.Slug == "real-steel");
            Assert.Equal("Real Steel & Friends", rs.Title);
            Assert.Equal(2011, rs.Year);
        }

        [Fact]
        public void AMissingYearIsNullNotZero()
        {
            var items = LetterboxdSyncService.ParsePosterList(Page);

            var up = Assert.Single(items, i => i.Slug == "untitled-project");
            Assert.Equal("Untitled Project", up.Title);
            Assert.Null(up.Year);
        }

        [Fact]
        public void DuplicatesCollapseOnSlug()
        {
            // Poster1 appears twice in the fixture; it must count once, or a
            // film gets "added" to the watchlist twice per sync.
            Assert.Equal(3, LetterboxdSyncService.ParsePosterList(Page).Count);
        }

        [Fact]
        public void NothingToReadReturnsEmpty()
        {
            Assert.Empty(LetterboxdSyncService.ParsePosterList(string.Empty));
            Assert.Empty(LetterboxdSyncService.ParsePosterList("<html><title>Letterboxd - Not Found</title></html>"));
        }

        [Fact]
        public void LastPageComesFromPagination()
        {
            Assert.Equal(4, LetterboxdSyncService.LastPageNumber(Page, "/h201ha/watchlist/"));
        }

        [Fact]
        public void APageWithoutPaginationIsPageOne()
        {
            Assert.Equal(1, LetterboxdSyncService.LastPageNumber("<html>" + Poster1 + "</html>", "/h201ha/watchlist/"));
        }

        [Fact]
        public void PagingFollowsThePagesOwnLinksNotTheRequestedPath()
        {
            // Letterboxd serves /films/ratings/ as the films list and pages it as
            // /{user}/films/page/N/, lower-cased. Asking for "ratings/page/N/"
            // found nothing and a member's 800 ratings were read as one page.
            // The page's own paginate block is the authority, prefix included.
            var (last, prefix) = LetterboxdSyncService.Pagination(
                "<div class=\"paginate-pages\"><ul><li><a href=\"/zl154/films/page/2/\">2</a></li><li><a href=\"/zl154/films/page/6/\">6</a></li></ul></div>",
                "/ZL154/films/ratings/");
            Assert.Equal(6, last);
            Assert.Equal("/zl154/films/", prefix);
        }

        [Fact]
        public void WithoutAPaginateBlockTheRequestedPathIsUsed()
        {
            var (last, prefix) = LetterboxdSyncService.Pagination("<html>" + Poster1 + "</html>", "/h201ha/likes/films/");
            Assert.Equal(1, last);
            Assert.Equal("/h201ha/likes/films/", prefix);
        }
    }
}
