using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Data;
using Jellyfin.Plugin.InternalRating.ExternalSync;
using Jellyfin.Plugin.InternalRating.Models;
using Xunit;

namespace Jellyfin.Plugin.InternalRating.Tests
{
    /// <summary>
    /// Fake IRatingReader that returns a fixed list of UserRatingEntry rows.
    /// </summary>
    internal sealed class FakeRatingReader : IRatingReader
    {
        private readonly List<UserRatingEntry> _rows;
        public FakeRatingReader(List<UserRatingEntry> rows) => _rows = rows;

        public Task<List<UserRatingEntry>> GetUserRatingsAsync(string userId, int limit = 10000)
            => Task.FromResult(_rows);
    }

    /// <summary>
    /// Fake IExternalIdResolver that maps a specific itemId to an ExternalRating,
    /// and returns null for any other itemId.
    /// </summary>
    internal sealed class FakeExternalIdResolver : IExternalIdResolver
    {
        private readonly string _resolveableItemId;
        private readonly ExternalRating _resolvedRating;

        public FakeExternalIdResolver(string resolveableItemId, ExternalRating resolvedRating)
        {
            _resolveableItemId = resolveableItemId;
            _resolvedRating = resolvedRating;
        }

        public ExternalRating? ResolveExternalIds(string itemId, double stars, DateTime ratedAt)
            => itemId == _resolveableItemId ? _resolvedRating : null;

        public string? FindItemId(ExternalRating r) => null;
    }

    public class RatingGathererTests
    {
        [Fact]
        public async Task GatherAsync_SkipsUnresolvableItems_ReturnsOnlyResolved()
        {
            // Arrange
            var resolvedItemId = "item-aaa";
            var unresolvedItemId = "item-bbb";

            var ratedAt1 = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc);
            var ratedAt2 = new DateTime(2024, 5, 2, 0, 0, 0, DateTimeKind.Utc);

            var rows = new List<UserRatingEntry>
            {
                new UserRatingEntry { ItemId = resolvedItemId,   Stars = 4.5, RatedAt = ratedAt1 },
                new UserRatingEntry { ItemId = unresolvedItemId, Stars = 3.0, RatedAt = ratedAt2 },
            };

            var expectedRating = new ExternalRating("tt0111161", null, null, "The Matrix", 1999, "movie", 4.5, ratedAt1);

            var reader   = new FakeRatingReader(rows);
            var resolver = new FakeExternalIdResolver(resolvedItemId, expectedRating);
            var gatherer = new RatingGatherer(reader, resolver);

            // Act
            var result = await gatherer.GatherAsync("user-1");

            // Assert
            Assert.Single(result);
        }

        [Fact]
        public async Task GatherAsync_PreservesStars()
        {
            var itemId  = "item-ccc";
            var ratedAt = new DateTime(2024, 6, 10, 0, 0, 0, DateTimeKind.Utc);

            var rows = new List<UserRatingEntry>
            {
                new UserRatingEntry { ItemId = itemId, Stars = 4.5, RatedAt = ratedAt },
            };

            var expectedRating = new ExternalRating(null, null, null, "Dune", 2021, "movie", 4.5, ratedAt);

            var reader   = new FakeRatingReader(rows);
            var resolver = new FakeExternalIdResolver(itemId, expectedRating);
            var gatherer = new RatingGatherer(reader, resolver);

            var result = await gatherer.GatherAsync("user-2");

            Assert.Single(result);
            Assert.Equal(4.5, result[0].Stars);
        }

        [Fact]
        public async Task GatherAsync_PreservesRatedAt()
        {
            var itemId  = "item-ddd";
            var ratedAt = new DateTime(2024, 7, 4, 12, 0, 0, DateTimeKind.Utc);

            var rows = new List<UserRatingEntry>
            {
                new UserRatingEntry { ItemId = itemId, Stars = 3.0, RatedAt = ratedAt },
            };

            var expectedRating = new ExternalRating(null, null, null, "Interstellar", 2014, "movie", 3.0, ratedAt);

            var reader   = new FakeRatingReader(rows);
            var resolver = new FakeExternalIdResolver(itemId, expectedRating);
            var gatherer = new RatingGatherer(reader, resolver);

            var result = await gatherer.GatherAsync("user-3");

            Assert.Single(result);
            Assert.Equal(ratedAt, result[0].RatedAt);
        }

        [Fact]
        public async Task GatherAsync_AllUnresolvable_ReturnsEmpty()
        {
            var rows = new List<UserRatingEntry>
            {
                new UserRatingEntry { ItemId = "no-match-1", Stars = 2.0, RatedAt = DateTime.UtcNow },
                new UserRatingEntry { ItemId = "no-match-2", Stars = 1.5, RatedAt = DateTime.UtcNow },
            };

            // Resolver that always returns null
            var reader   = new FakeRatingReader(rows);
            var resolver = new FakeExternalIdResolver("__none__", new ExternalRating(null, null, null, "X", null, "movie", 1.0, DateTime.UtcNow));
            var gatherer = new RatingGatherer(reader, resolver);

            var result = await gatherer.GatherAsync("user-4");

            Assert.Empty(result);
        }

        [Fact]
        public async Task GatherAsync_EmptyReader_ReturnsEmpty()
        {
            var reader   = new FakeRatingReader(new List<UserRatingEntry>());
            var resolver = new FakeExternalIdResolver("x", new ExternalRating(null, null, null, "X", null, "movie", 1.0, DateTime.UtcNow));
            var gatherer = new RatingGatherer(reader, resolver);

            var result = await gatherer.GatherAsync("user-5");

            Assert.Empty(result);
        }
    }
}
