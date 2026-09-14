using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating;
using Jellyfin.Plugin.InternalRating.Data;
using Jellyfin.Plugin.InternalRating.Models;
using Xunit;

namespace Jellyfin.Plugin.InternalRating.Tests
{
    /// <summary>
    /// The diary has three writers — playback, the rating panel, and imports —
    /// and a user reported the two ways they disagreed: a film watched (logged
    /// unrated by playback) then rated on Letterboxd appeared twice, and the
    /// 9-second pre-roll clips before each film were logged as viewings.
    /// </summary>
    public class DiaryDedupeTests
    {
        private const string User = "u1";
        private const string Item = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        // The reported case, exactly. The server runs in UTC (as every
        // container does); the user is in BST. Batman finished at 00:19 BST on
        // the 13th, which playback stored as 23:19Z on the 12th. The user then
        // rated it on Letterboxd with the watched-date "13 Sep", which arrives
        // as 00:00Z on the 13th. Forty-one minutes apart, different UTC days,
        // both rendered as "Sep 13" in the browser.
        private static readonly DateTime PlaybackAt    = new DateTime(2026, 9, 12, 23, 19, 4, DateTimeKind.Utc);
        private static readonly DateTime LetterboxdDate = new DateTime(2026, 9, 13,  0,  0, 0, DateTimeKind.Utc);

        // ---- the duplicate ----

        [Fact]
        public async Task ARatingImportedForTodaysUnratedPlaybackRowEnrichesItInstead()
        {
            using var paths = new TestPaths();
            var repo = new DiaryRepository(paths);
            await repo.AddEntryAsync(User, new DiaryEntry { ItemId = Item, WatchedAt = PlaybackAt, Stars = null });

            var changed = await repo.ImportEntriesAsync(User, new[]
            {
                new DiaryEntry { ItemId = Item, WatchedAt = LetterboxdDate, Stars = 4.5, Review = "Great.", Rewatch = false }
            });

            var entries = await repo.GetEntriesAsync(User);
            var one = Assert.Single(entries);          // not two
            Assert.Equal(1, changed);
            Assert.Equal(4.5, one.Stars);              // the rating landed
            Assert.Equal("Great.", one.Review);
            Assert.Equal(PlaybackAt, one.WatchedAt);   // the truer timestamp is kept
        }

        [Fact]
        public async Task ARatedRowOnTheSameDayIsLeftAlone()
        {
            using var paths = new TestPaths();
            var repo = new DiaryRepository(paths);
            await repo.AddEntryAsync(User, new DiaryEntry { ItemId = Item, WatchedAt = PlaybackAt, Stars = 3.0 });

            var changed = await repo.ImportEntriesAsync(User, new[]
            {
                new DiaryEntry { ItemId = Item, WatchedAt = LetterboxdDate, Stars = 4.5 }
            });

            var one = Assert.Single(await repo.GetEntriesAsync(User));
            Assert.Equal(0, changed);
            Assert.Equal(3.0, one.Stars);   // an import never overwrites a rating already in the diary
        }

        [Fact]
        public async Task ADifferentDayIsADifferentViewing()
        {
            using var paths = new TestPaths();
            var repo = new DiaryRepository(paths);

            // A RATED import for a different day than an existing RATED row is a
            // rewatch, and must be kept.
            await repo.ImportEntriesAsync(User, new[] { new DiaryEntry { ItemId = Item, WatchedAt = PlaybackAt, Stars = 3.0 } });
            var laterRewatch = PlaybackAt.AddDays(5);
            await repo.ImportEntriesAsync(User, new[] { new DiaryEntry { ItemId = Item, WatchedAt = laterRewatch, Stars = 4.0 } });

            Assert.Equal(2, (await repo.GetEntriesAsync(User)).Count);
        }

        [Fact]
        public void TheTwoTimestampsAreOneViewing()
        {
            // No calendar test can pair these from the server side — they are
            // on different UTC days and the server does not know the user's
            // timezone. The window does.
            Assert.True(DiaryRepository.WithinSameViewing(PlaybackAt, LetterboxdDate));
        }

        [Fact]
        public async Task ARatingImportedDaysLaterStillLandsOnTheUnratedRow()
        {
            // The user rates a film they watched last week. StarTrack holds one
            // rating per film; an unrated diary row for a rated film is just the
            // same film with the score missing. No window on this.
            using var paths = new TestPaths();
            var repo = new DiaryRepository(paths);
            await repo.AddEntryAsync(User, new DiaryEntry { ItemId = Item, WatchedAt = PlaybackAt, Stars = null });

            await repo.ImportEntriesAsync(User, new[] { new DiaryEntry { ItemId = Item, WatchedAt = PlaybackAt.AddDays(6), Stars = 4.0 } });

            var one = Assert.Single(await repo.GetEntriesAsync(User));
            Assert.Equal(4.0, one.Stars);
            Assert.Equal(PlaybackAt, one.WatchedAt);
        }

        // ---- a rating written anywhere reaches the diary ----

        [Fact]
        public async Task ApplyingARatingFillsEveryUnratedRowForThatFilmAndNoOther()
        {
            using var paths = new TestPaths();
            var repo = new DiaryRepository(paths);
            await repo.AddEntryAsync(User, new DiaryEntry { ItemId = Item, WatchedAt = PlaybackAt, Stars = null });
            await repo.AddEntryAsync(User, new DiaryEntry { ItemId = Item, WatchedAt = PlaybackAt.AddDays(-30), Stars = null });
            await repo.AddEntryAsync(User, new DiaryEntry { ItemId = Item, WatchedAt = PlaybackAt.AddDays(-60), Stars = 2.0 });
            await repo.AddEntryAsync(User, new DiaryEntry { ItemId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", WatchedAt = PlaybackAt, Stars = null });

            var n = await repo.ApplyRatingAsync(User, Item, 4.5, "Loved it");

            Assert.Equal(2, n);
            var rows = await repo.GetEntriesAsync(User);
            Assert.All(rows.Where(r => r.ItemId == Item && r.WatchedAt != PlaybackAt.AddDays(-60)), r => Assert.Equal(4.5, r.Stars));
            Assert.Equal(2.0, rows.Single(r => r.WatchedAt == PlaybackAt.AddDays(-60)).Stars);   // an existing rating is never overwritten
            Assert.Null(rows.Single(r => r.ItemId.StartsWith("bbbb")).Stars);                    // other films untouched
        }

        [Fact]
        public async Task SavingARatingAnywhereRaisesTheHookTheDiaryListensTo()
        {
            // The seven rating writers all go through RatingRepository; this is
            // the one place the diary has to be told.
            using var paths = new TestPaths();
            var ratings = new RatingRepository(paths);
            var diary = new DiaryRepository(paths);
            ratings.RatingSaved += (u, i, st, rv) => diary.ApplyRatingAsync(u, i, st, rv);
            await diary.AddEntryAsync(User, new DiaryEntry { ItemId = Item, WatchedAt = PlaybackAt, Stars = null });

            await ratings.SaveRatingAsync(Item, User, "Zack", 4.0);

            Assert.Equal(4.0, Assert.Single(await diary.GetEntriesAsync(User)).Stars);
        }

        [Fact]
        public async Task StartupBackfillRepairsRowsWrittenBeforeTheHookExisted()
        {
            // The reporting user's diary: nine unrated rows for films rated the
            // same day, written by playback before any of this existed.
            using var paths = new TestPaths();
            var diary = new DiaryRepository(paths);
            await diary.AddEntryAsync(User, new DiaryEntry { ItemId = Item, WatchedAt = PlaybackAt, Stars = null });
            await diary.AddEntryAsync(User, new DiaryEntry { ItemId = "cccccccccccccccccccccccccccccccc", WatchedAt = PlaybackAt, Stars = null });

            var fixedRows = await diary.BackfillFromRatingsAsync((u, i) => Task.FromResult<double?>(i == Item ? 3.5 : null));

            Assert.Equal(1, fixedRows);
            var rows = await diary.GetEntriesAsync(User);
            Assert.Equal(3.5, rows.Single(r => r.ItemId == Item).Stars);
            Assert.Null(rows.Single(r => r.ItemId.StartsWith("cccc")).Stars);   // never rated: stays unrated, honestly
        }

        // ---- the pre-rolls ----

        [Theory]
        [InlineData(9)]      // a cinema ident
        [InlineData(45)]     // a trailer
        [InlineData(119)]    // just under the line
        public void ClipsShorterThanTwoMinutesAreNotViewings(int seconds)
        {
            Assert.False(PlaybackDiaryService.IsLoggableRuntime(TimeSpan.FromSeconds(seconds).Ticks));
        }

        [Theory]
        [InlineData(120)]    // the line itself
        [InlineData(3 * 60)] // a three-minute anime short
        [InlineData(133 * 60)]
        public void AnythingFromTwoMinutesUpIs(int seconds)
        {
            Assert.True(PlaybackDiaryService.IsLoggableRuntime(TimeSpan.FromSeconds(seconds).Ticks));
        }

        [Fact]
        public void UnknownRuntimeIsAllowedThrough()
        {
            // Missing runtime is a metadata gap, not evidence of a clip.
            Assert.True(PlaybackDiaryService.IsLoggableRuntime(null));
            Assert.True(PlaybackDiaryService.IsLoggableRuntime(0));
        }
    }
}
