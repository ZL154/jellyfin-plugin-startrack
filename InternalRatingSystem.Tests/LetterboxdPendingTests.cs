using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InternalRatingSystem.Tests
{
    /// <summary>
    /// The queue behind issue #25: Letterboxd rows that matched nothing in the
    /// library, kept so a later sync can apply them once the film arrives.
    ///
    /// The dedupe key carries the weight here. Get it too loose and a member's
    /// rewatches collapse into a single diary entry; too tight and every
    /// re-import of the same export stacks another copy of a backlog that is
    /// already thousands of rows long. Most of these tests pin that boundary.
    /// </summary>
    public sealed class LetterboxdPendingTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _file;

        public LetterboxdPendingTests()
        {
            _dir  = Path.Combine(Path.GetTempPath(), "startrack-pending-" + Guid.NewGuid().ToString("N"));
            _file = Path.Combine(_dir, "letterboxd-pending.json");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch { /* a leftover temp dir is not worth failing a test run over */ }
        }

        private LetterboxdPendingStore NewStore() => new(_file);

        private const string User = "user-1";

        private static (string, LetterboxdPendingRow) Rating(
            string name, int? year = 2010, double stars = 4.0, DateTime? date = null)
            => (LetterboxdSyncService.NormalizeTitle(name), new LetterboxdPendingRow
            {
                Kind   = LetterboxdPendingKind.Rating,
                Name   = name,
                Year   = year,
                Rating = stars,
                Date   = date
            });

        private static (string, LetterboxdPendingRow) Diary(
            string name, DateTime watched, bool rewatch = false, int? year = 2010)
            => (LetterboxdSyncService.NormalizeTitle(name), new LetterboxdPendingRow
            {
                Kind    = LetterboxdPendingKind.Diary,
                Name    = name,
                Year    = year,
                Date    = watched,
                Rewatch = rewatch
            });

        // ---- merge + dedupe ----

        [Fact]
        public async Task QueuesAnUnmatchedRow()
        {
            var store = NewStore();
            var added = await store.MergeAsync(User, new[] { Rating("Inception") });

            Assert.Equal(1, added);
            Assert.Equal(1, await store.CountAsync(User));
        }

        [Fact]
        public async Task ReimportingTheSameExportDoesNotDuplicate()
        {
            // The issue asks for re-importing an export to rebuild/merge the
            // pending set rather than stack a second copy of it.
            var store = NewStore();
            await store.MergeAsync(User, new[] { Rating("Inception") });
            var added = await store.MergeAsync(User, new[] { Rating("Inception") });

            Assert.Equal(0, added);
            Assert.Equal(1, await store.CountAsync(User));
        }

        [Fact]
        public async Task TitlePunctuationDoesNotCreateASecondRow()
        {
            // Keys are built from the normalized title, so a re-export that
            // writes the title differently still lands on the same row.
            var store = NewStore();
            await store.MergeAsync(User, new[] { Rating("WALL·E") });
            await store.MergeAsync(User, new[] { Rating("WALL-E") });

            Assert.Equal(1, await store.CountAsync(User));
        }

        [Fact]
        public async Task ReimportRefreshesTheRatingButKeepsFirstSeen()
        {
            var store = NewStore();
            await store.MergeAsync(User, new[] { Rating("Inception", stars: 3.0) });
            var firstSeen = (await store.GetAsync(User)).Single().FirstSeenAt;

            await store.MergeAsync(User, new[] { Rating("Inception", stars: 5.0) });

            var row = Assert.Single(await store.GetAsync(User));
            Assert.Equal(5.0, row.Rating);                 // corrected rating wins
            Assert.Equal(firstSeen, row.FirstSeenAt);      // but "waiting since" does not reset
        }

        [Fact]
        public async Task SameTitleDifferentYearsAreSeparateRows()
        {
            var store = NewStore();
            await store.MergeAsync(User, new[] { Rating("Dune", year: 1984), Rating("Dune", year: 2021) });

            Assert.Equal(2, await store.CountAsync(User));
        }

        [Fact]
        public async Task SameFilmInDifferentStreamsAreSeparateRows()
        {
            // A film can be rated AND on the watchlist; resolving one must not
            // silently discard the other.
            var store = NewStore();
            await store.MergeAsync(User, new[]
            {
                Rating("Inception"),
                (LetterboxdSyncService.NormalizeTitle("Inception"), new LetterboxdPendingRow
                {
                    Kind = LetterboxdPendingKind.Watchlist, Name = "Inception", Year = 2010
                })
            });

            Assert.Equal(2, await store.CountAsync(User));
        }

        [Fact]
        public async Task RewatchesOnDifferentDaysAreSeparateDiaryRows()
        {
            // Diary keys include the watched date precisely so this holds — a
            // rewatch is a distinct entry, not a duplicate of the first watch.
            var store = NewStore();
            await store.MergeAsync(User, new[]
            {
                Diary("Inception", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                Diary("Inception", new DateTime(2025, 6, 9, 0, 0, 0, DateTimeKind.Utc), rewatch: true)
            });

            Assert.Equal(2, await store.CountAsync(User));
        }

        [Fact]
        public async Task TwoWatchesOnTheSameDayCollapseToOneDiaryRow()
        {
            // The flip side: the key is date-only, matching how the diary store
            // itself dedupes on (item, watched-day).
            var store = NewStore();
            var day = new DateTime(2025, 6, 9, 0, 0, 0, DateTimeKind.Utc);
            await store.MergeAsync(User, new[] { Diary("Inception", day), Diary("Inception", day.AddHours(9)) });

            Assert.Equal(1, await store.CountAsync(User));
        }

        [Fact]
        public async Task QueuesAreSeparatePerUser()
        {
            var store = NewStore();
            await store.MergeAsync(User, new[] { Rating("Inception") });
            await store.MergeAsync("user-2", new[] { Rating("Dune", year: 2021) });

            Assert.Equal(1, await store.CountAsync(User));
            Assert.Equal(1, await store.CountAsync("user-2"));
        }

        // ---- removal ----

        [Fact]
        public async Task RemovingAResolvedRowDropsIt()
        {
            var store = NewStore();
            await store.MergeAsync(User, new[] { Rating("Inception"), Rating("Dune", year: 2021) });

            var key = LetterboxdPendingStore.Key(
                LetterboxdPendingKind.Rating, LetterboxdSyncService.NormalizeTitle("Inception"), 2010, null);
            await store.RemoveAsync(User, new[] { key });

            var remaining = Assert.Single(await store.GetAsync(User));
            Assert.Equal("Dune", remaining.Name);
        }

        [Fact]
        public async Task MarkTriedStampsWithoutRemoving()
        {
            var store = NewStore();
            await store.MergeAsync(User, new[] { Rating("Inception") });
            var at = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);

            var key = LetterboxdPendingStore.Key(
                LetterboxdPendingKind.Rating, LetterboxdSyncService.NormalizeTitle("Inception"), 2010, null);
            await store.MarkTriedAsync(User, new[] { key }, at);

            var row = Assert.Single(await store.GetAsync(User));
            Assert.Equal(at, row.LastTriedAt);
        }

        [Fact]
        public async Task ClearEmptiesOneUsersQueueOnly()
        {
            var store = NewStore();
            await store.MergeAsync(User, new[] { Rating("Inception") });
            await store.MergeAsync("user-2", new[] { Rating("Dune", year: 2021) });

            await store.ClearAsync(User);

            Assert.Equal(0, await store.CountAsync(User));
            Assert.Equal(1, await store.CountAsync("user-2"));
        }

        // ---- durability ----

        [Fact]
        public async Task SurvivesARestart()
        {
            // The whole feature rests on this: the queue has to outlive the
            // server, since the film it is waiting for may arrive months later.
            var watched = new DateTime(2025, 6, 9, 0, 0, 0, DateTimeKind.Utc);
            var store = NewStore();
            await store.MergeAsync(User, new[] { Rating("Inception", stars: 4.5), Diary("Dune", watched, year: 2021) });

            var reloaded = NewStore();          // fresh instance over the same file
            var rows = await reloaded.GetAsync(User);

            Assert.Equal(2, rows.Count);
            var rating = rows.Single(r => r.Kind == LetterboxdPendingKind.Rating);
            Assert.Equal("Inception", rating.Name);
            Assert.Equal(4.5, rating.Rating);
            var diary = rows.Single(r => r.Kind == LetterboxdPendingKind.Diary);
            Assert.Equal(watched, diary.Date);
        }

        [Fact]
        public async Task ACorruptQueueStartsEmptyRatherThanThrowing()
        {
            // Losing a backlog puts the user back where they were before the
            // feature existed. Failing to construct would take Jellyfin's whole
            // plugin startup down with it.
            Directory.CreateDirectory(_dir);
            await File.WriteAllTextAsync(_file, "{ this is not json");

            var store = NewStore();

            Assert.Equal(0, await store.CountAsync(User));
        }

        [Fact]
        public async Task GetReturnsCopiesNotLiveRows()
        {
            var store = NewStore();
            await store.MergeAsync(User, new[] { Rating("Inception", stars: 4.0) });

            (await store.GetAsync(User)).Single().Rating = 1.0;

            Assert.Equal(4.0, (await store.GetAsync(User)).Single().Rating);
        }

        // ---- cap ----

        [Fact]
        public async Task StopsQueueingAtTheCap()
        {
            var store = NewStore();
            var many = Enumerable.Range(0, LetterboxdPendingStore.MaxRowsPerUser + 50)
                                 .Select(n => Rating("Film " + n, year: null))
                                 .ToList();

            var added = await store.MergeAsync(User, many);

            Assert.Equal(LetterboxdPendingStore.MaxRowsPerUser, added);
            Assert.Equal(LetterboxdPendingStore.MaxRowsPerUser, await store.CountAsync(User));
        }

        [Fact]
        public async Task AtTheCapExistingRowsStillUpdate()
        {
            // Rows already queued have been retried and are cheap to keep, so a
            // full queue must not stop a re-import correcting one of them.
            var store = NewStore();
            var many = Enumerable.Range(0, LetterboxdPendingStore.MaxRowsPerUser)
                                 .Select(n => Rating("Film " + n, year: null))
                                 .ToList();
            await store.MergeAsync(User, many);

            await store.MergeAsync(User, new[] { Rating("Film 0", year: null, stars: 1.5) });

            var row = (await store.GetAsync(User)).Single(r => r.Name == "Film 0");
            Assert.Equal(1.5, row.Rating);
        }

        [Fact]
        public async Task AtTheCapAnExistingRowLaterInTheBatchStillUpdates()
        {
            // Regression: the cap used to `break` out of the merge, so a full
            // queue stopped processing at the first NEW row in the batch. Any
            // correction to an already-queued row that happened to sort after it
            // was silently dropped — whether a re-import fixed your rating came
            // down to where the film sat in the export.
            var store = NewStore();
            await store.MergeAsync(User, Enumerable.Range(0, LetterboxdPendingStore.MaxRowsPerUser)
                                                   .Select(n => Rating("Film " + n, year: null))
                                                   .ToList());

            var added = await store.MergeAsync(User, new[]
            {
                Rating("Brand New Film", year: null),          // refused: queue is full
                Rating("Film 7", year: null, stars: 0.5)       // must still update
            });

            Assert.Equal(0, added);
            Assert.Equal(LetterboxdPendingStore.MaxRowsPerUser, await store.CountAsync(User));
            Assert.Equal(0.5, (await store.GetAsync(User)).Single(r => r.Name == "Film 7").Rating);
        }

        // ---- HasAnyAsync: the gate that keeps the feature free when unused ----

        [Fact]
        public async Task HasAnyIsFalseUntilSomethingIsQueuedAndFalseAgainOnceDrained()
        {
            var store = NewStore();
            Assert.False(await store.HasAnyAsync());

            await store.MergeAsync(User, new[] { Rating("Heat", 1995) });
            Assert.True(await store.HasAnyAsync());

            await store.ClearAsync(User);
            Assert.False(await store.HasAnyAsync());
        }

        [Fact]
        public async Task HasAnySeesOtherUsersBacklogs()
        {
            // The scheduled task takes ONE fingerprint for the whole tick, so the
            // gate has to answer for the server, not for whoever it asked about.
            var store = NewStore();
            await store.MergeAsync("someone-else", new[] { Rating("Heat", 1995) });

            Assert.Equal(0, await store.CountAsync(User));
            Assert.True(await store.HasAnyAsync());
        }

        // ---- the matched/unmatched boundary ----
        //
        // Every capture site — the four CSV importers and the RSS sync — queues a
        // row on exactly one condition: MovieLookup.Find returned null. That makes
        // Find the hinge the whole feature turns on. Too eager and a rating is
        // written against the wrong film; too strict and rows pile up in the queue
        // for films the server already has.

        private static Movie InLibrary(string name, int? year) =>
            new() { Name = name, ProductionYear = year, Path = "/library/" + name + ".mkv" };

        private static LetterboxdSyncService.MovieLookup Library(params Movie[] movies) =>
            new(movies, NullLogger.Instance);

        [Fact]
        public void AFilmInTheLibraryIsMatchedAndSoNeverQueued()
        {
            var lookup = Library(InLibrary("Inception", 2010));

            Assert.NotNull(lookup.Find("Inception", 2010, out _));
        }

        [Fact]
        public void AFilmMissingFromTheLibraryIsWhatGetsQueued()
        {
            var lookup = Library(InLibrary("Inception", 2010));

            Assert.Null(lookup.Find("Dune", 2021, out _));
        }

        [Fact]
        public void PunctuationAndAccentsStillMatch()
        {
            // These would otherwise queue rows for films the server already has,
            // and no later retry would ever clear them.
            var lookup = Library(InLibrary("WALL·E", 2008), InLibrary("Amélie", 2001));

            Assert.NotNull(lookup.Find("WALL-E", 2008, out _));
            Assert.NotNull(lookup.Find("Amelie", 2001, out _));
        }

        [Fact]
        public void LeadingArticlesMatchInEitherDirection()
        {
            var lookup = Library(InLibrary("The Matrix", 1999));

            Assert.NotNull(lookup.Find("Matrix, The", 1999, out _));
            Assert.NotNull(lookup.Find("Matrix", 1999, out _));
        }

        [Fact]
        public void AYearDriftOfOneStillMatches()
        {
            // Letterboxd and Jellyfin disagree on release year often enough that
            // exact-year matching alone would queue plenty of false misses.
            var lookup = Library(InLibrary("Blade Runner 2049", 2017));

            Assert.NotNull(lookup.Find("Blade Runner 2049", 2018, out _));
        }

        [Fact]
        public void TheRightYearWinsAmongDuplicateTitles()
        {
            var lookup = Library(InLibrary("Dune", 1984), InLibrary("Dune", 2021));

            Assert.Equal(2021, lookup.Find("Dune", 2021, out _)?.ProductionYear);
            Assert.Equal(1984, lookup.Find("Dune", 1984, out _)?.ProductionYear);
        }
    }
}
