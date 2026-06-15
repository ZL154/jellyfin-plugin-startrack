using System;
using System.Collections.Generic;
using Jellyfin.Plugin.InternalRating.ExternalSync;
using Xunit;

namespace Jellyfin.Plugin.InternalRating.Tests
{
    public class ExternalIdResolverTests
    {
        // ---- MapToExternalRating ----

        [Fact]
        public void MapToExternalRating_ReadsImdbAndTmdb()
        {
            var ids = new Dictionary<string, string> { ["Imdb"] = "tt0111161", ["Tmdb"] = "278" };
            var r = ExternalIdResolver.MapToExternalRating(ids, "The Shawshank Redemption", 1994, "movie", 5.0, new DateTime(2024, 1, 1));
            Assert.Equal("tt0111161", r.Imdb);
            Assert.Equal(278, r.Tmdb);
            Assert.Null(r.Tvdb);
            Assert.Equal("movie", r.MediaType);
            Assert.Equal(5.0, r.Stars);
        }

        [Fact]
        public void MapToExternalRating_MissingIds_NullsThem()
        {
            var r = ExternalIdResolver.MapToExternalRating(new Dictionary<string, string>(), "X", null, "show", 3.0, DateTime.UtcNow);
            Assert.Null(r.Imdb);
            Assert.Null(r.Tmdb);
            Assert.Null(r.Tvdb);
        }

        [Fact]
        public void MapToExternalRating_ReadsTvdb()
        {
            var ids = new Dictionary<string, string> { ["Tvdb"] = "81189", ["Imdb"] = "tt0903747" };
            var r = ExternalIdResolver.MapToExternalRating(ids, "Breaking Bad", 2008, "show", 5.0, DateTime.UtcNow);
            Assert.Equal("tt0903747", r.Imdb);
            Assert.Null(r.Tmdb);
            Assert.Equal(81189, r.Tvdb);
            Assert.Equal("show", r.MediaType);
        }

        [Fact]
        public void MapToExternalRating_PreservesTitle_Year_Stars_RatedAt()
        {
            var ratedAt = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
            var ids = new Dictionary<string, string> { ["Tmdb"] = "99" };
            var r = ExternalIdResolver.MapToExternalRating(ids, "Dune", 2021, "movie", 4.5, ratedAt);
            Assert.Equal("Dune", r.Title);
            Assert.Equal(2021, r.Year);
            Assert.Equal(4.5, r.Stars);
            Assert.Equal(ratedAt, r.RatedAt);
            Assert.Equal(99, r.Tmdb);
        }

        [Fact]
        public void MapToExternalRating_NonIntegerTmdb_ReturnsNullTmdb()
        {
            var ids = new Dictionary<string, string> { ["Tmdb"] = "not-a-number" };
            var r = ExternalIdResolver.MapToExternalRating(ids, "Test", null, "movie", 3.0, DateTime.UtcNow);
            Assert.Null(r.Tmdb);
        }

        [Fact]
        public void MapToExternalRating_EmptyImdb_ReturnsNullImdb()
        {
            var ids = new Dictionary<string, string> { ["Imdb"] = "   " };
            var r = ExternalIdResolver.MapToExternalRating(ids, "Test", null, "movie", 3.0, DateTime.UtcNow);
            Assert.Null(r.Imdb);
        }

        // ---- NormalizeTitle ----

        [Fact]
        public void NormalizeTitle_LowercasesAndTrims()
            => Assert.Equal("matrix", ExternalIdResolver.NormalizeTitle("  The Matrix "));

        [Fact]
        public void NormalizeTitle_StripsDiacritics()
            => Assert.Equal("amelie", ExternalIdResolver.NormalizeTitle("Amélie"));

        [Fact]
        public void NormalizeTitle_StripsPunctuation()
            // "A" is in the middle (not a leading/trailing article), so it stays.
            // The colon becomes a space; leading/trailing whitespace collapse.
            => Assert.Equal("star wars a new hope", ExternalIdResolver.NormalizeTitle("Star Wars: A New Hope"));

        [Fact]
        public void NormalizeTitle_NullOrEmpty_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, ExternalIdResolver.NormalizeTitle(null));
            Assert.Equal(string.Empty, ExternalIdResolver.NormalizeTitle(""));
        }

        [Fact]
        public void NormalizeTitle_TrailingArticle_Stripped()
            => Assert.Equal("matrix", ExternalIdResolver.NormalizeTitle("Matrix, The"));

        [Fact]
        public void NormalizeTitle_SingleWord_ArticleNotStripped()
            // "The" alone → only one word after split, so article-strip condition
            // (parts.Length > 1) is false → returns "the"
            => Assert.Equal("the", ExternalIdResolver.NormalizeTitle("The"));
    }
}
