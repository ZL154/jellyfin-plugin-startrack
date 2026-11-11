using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Jellyfin.Plugin.InternalRating.Data;
using Jellyfin.Plugin.InternalRating.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.InternalRating.Tests
{
    /// <summary>
    /// Moving a title's entries from the item id Jellyfin dropped to the one it
    /// now uses, in each of the four stores. The edge in every store is the same:
    /// a user who already has the new id must end up with one entry, not two.
    /// </summary>
    public sealed class RelinkRepositoryTests
    {
        private const string Old = "0123456789abcdef0123456789abcdef";
        private const string New = "fedcba9876543210fedcba9876543210";

        [Fact]
        public async Task Ratings_Move_AndTheLaterRatingWinsOnConflict()
        {
            using var paths = new TestPaths();
            var repo = new RatingRepository(paths);
            await repo.SaveRatingAsync(Old, "u1", "U1", 4.0, null, new DateTime(2026, 1, 1));
            await repo.SaveRatingAsync(Old, "u2", "U2", 3.0, "old review", new DateTime(2026, 1, 1));
            await repo.SaveRatingAsync(New, "u2", "U2", 5.0, null, new DateTime(2026, 2, 1));

            Assert.Equal(1, await repo.RenameItemAsync(Old, New));

            Assert.Equal(0, (await repo.GetRatingsAsync(Old)).TotalRatings);
            var now = await repo.GetRatingsAsync(New);
            Assert.Equal(2, now.TotalRatings);
            Assert.Equal(4.0, now.UserRatings.Single(r => r.UserId == "u1").Stars);
            Assert.Equal(5.0, now.UserRatings.Single(r => r.UserId == "u2").Stars);
            Assert.DoesNotContain(Old, await repo.GetAllItemIdsAsync());
        }

        [Fact]
        public async Task Ratings_Rename_OfUnknownId_IsNoop()
        {
            using var paths = new TestPaths();
            var repo = new RatingRepository(paths);
            Assert.Equal(0, await repo.RenameItemAsync(Old, New));
        }

        [Fact]
        public async Task Diary_KeepsEveryRewatchRow()
        {
            using var paths = new TestPaths();
            var repo = new DiaryRepository(paths);
            await repo.AddEntryAsync("u1", new DiaryEntry { ItemId = Old, WatchedAt = new DateTime(2025, 1, 1) });
            await repo.AddEntryAsync("u1", new DiaryEntry { ItemId = Old, WatchedAt = new DateTime(2026, 1, 1), Rewatch = true });
            await repo.AddEntryAsync("u2", new DiaryEntry { ItemId = Old, WatchedAt = new DateTime(2026, 1, 2) });

            Assert.Equal(3, await repo.RenameItemAsync(Old, New));
            Assert.All(await repo.GetEntriesAsync("u1"), e => Assert.Equal(New, e.ItemId));
            Assert.Equal(2, (await repo.GetEntriesAsync("u1")).Count);
            Assert.Equal(new[] { New }, (await repo.GetAllItemIdsAsync()).ToArray());
        }

        [Fact]
        public async Task Interactions_MoveWatchlistLikesAndFavourites_WithoutDuplicates()
        {
            using var paths = new TestPaths();
            var repo = new UserInteractionsRepository(paths);
            await repo.AddToWatchlistAsync("u1", Old);
            await repo.AddLikeAsync("u1", Old);
            await repo.AddLikeAsync("u1", New);                       // already likes the new id
            await repo.SetFavoritesAsync("u1", new[] { "other", Old });
            await repo.SetFavoritesAsync("u2", new[] { Old, New });   // both in the top list

            Assert.Equal(4, await repo.RenameItemAsync(Old, New)); // watchlist, like (dropped), two favourites

            var s = await repo.GetStatusAsync("u1", New);
            Assert.True(s.Watchlisted);
            Assert.True(s.Liked);
            Assert.Single((await repo.GetLikedAsync("u1")));
            Assert.Equal(new[] { "other", New }, (await repo.GetFavoritesAsync("u1")).ToArray());
            Assert.Equal(new[] { New }, (await repo.GetFavoritesAsync("u2")).ToArray());
            Assert.False((await repo.GetStatusAsync("u1", Old)).Watchlisted);
            Assert.DoesNotContain(Old, await repo.GetAllItemIdsAsync());
        }

        [Fact]
        public async Task Lists_MoveEntries_AndDropTheOldWhenTheListAlreadyHasTheNew()
        {
            using var paths = new TestPaths();
            var repo = new ListsRepository(paths);
            var a = await repo.CreateAsync("u1", "U1", "A", null, true);
            var b = await repo.CreateAsync("u1", "U1", "B", null, true);
            await repo.AddItemAsync(a.Id, "u1", "U1", Old);
            await repo.AddItemAsync(b.Id, "u1", "U1", Old);
            await repo.AddItemAsync(b.Id, "u1", "U1", New);

            Assert.Equal(2, await repo.RenameItemAsync(Old, New));

            Assert.Equal(new[] { New }, (await repo.GetByIdAsync(a.Id))!.Items.Select(i => i.ItemId).ToArray());
            Assert.Equal(new[] { New }, (await repo.GetByIdAsync(b.Id))!.Items.Select(i => i.ItemId).ToArray());
            Assert.Equal(new[] { New }, (await repo.GetAllItemIdsAsync()).ToArray());
        }
    }

    /// <summary>
    /// Which item a lookup may move entries to. The library can answer a
    /// provider-id query with more than one row: alternate versions carry the
    /// primary's ids, a film can exist in two libraries. Only one unambiguous
    /// primary is a target; anything else is a miss, never a guess.
    /// </summary>
    public sealed class RelinkCandidateTests
    {
        private static Movie Film() => new() { Name = "Heat", Id = Guid.NewGuid() };

        [Fact]
        public void SingleOtherItem_IsTheCandidate()
        {
            var old = Guid.NewGuid();
            var fresh = Film();
            var pick = LibraryRelinkService.PickCandidate(new List<BaseItem> { fresh }, old, out var ambiguous);
            Assert.Same(fresh, pick);
            Assert.False(ambiguous);
        }

        [Fact]
        public void TheIdBeingReplaced_IsNeverTheCandidate()
        {
            var self = Film();
            var pick = LibraryRelinkService.PickCandidate(new List<BaseItem> { self }, self.Id, out var ambiguous);
            Assert.Null(pick);
            Assert.False(ambiguous);
        }

        [Fact]
        public void AlternateVersions_AndExtras_AreSkipped()
        {
            var primary = Film();
            var alternate = Film();
            alternate.PrimaryVersionId = primary.Id.ToString("N");
            var extra = Film();
            extra.OwnerId = primary.Id;
            var pick = LibraryRelinkService.PickCandidate(new List<BaseItem> { alternate, extra, primary }, Guid.NewGuid(), out var ambiguous);
            Assert.Same(primary, pick);
            Assert.False(ambiguous);
        }

        [Fact]
        public void AlternateOfTheVanishedId_IsTheCandidate()
        {
            // 10.11 never clears the link when a primary is deleted, so the
            // alternate left behind is what the client shows.
            var gone = Guid.NewGuid();
            var orphanAlternate = Film();
            orphanAlternate.PrimaryVersionId = gone.ToString("N");
            var pick = LibraryRelinkService.PickCandidate(new List<BaseItem> { orphanAlternate }, gone, out var ambiguous);
            Assert.Same(orphanAlternate, pick);
            Assert.False(ambiguous);
        }

        [Fact]
        public void TwoPrimaries_IsAmbiguous_AndNothingMoves()
        {
            var pick = LibraryRelinkService.PickCandidate(new List<BaseItem> { Film(), Film() }, Guid.NewGuid(), out var ambiguous);
            Assert.Null(pick);
            Assert.True(ambiguous);
        }

        [Fact]
        public void TheSameItemTwice_IsNotAmbiguous()
        {
            var one = Film();
            var pick = LibraryRelinkService.PickCandidate(new List<BaseItem> { one, one }, Guid.NewGuid(), out var ambiguous);
            Assert.Same(one, pick);
            Assert.False(ambiguous);
        }
    }
}
