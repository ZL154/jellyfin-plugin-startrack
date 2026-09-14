using System;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using Xunit;

namespace Jellyfin.Plugin.InternalRating.Tests
{
    /// <summary>
    /// Letterboxd's ratings and likes lists are newest-first but undated. A
    /// full sync once stamped "now" on two hundred never-logged films: the
    /// My Ratings page went alphabetical and the activity feed became a wall
    /// of "rated today". The order on the page was the clue all along.
    /// </summary>
    public class ListOrderDatesTests
    {
        private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
        private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void KnownDatesAreKeptExactly()
        {
            var r = ListOrderDates.Fill(new DateTime?[] { D(2026, 1, 1), D(2025, 1, 1) }, Now);
            Assert.Equal(new[] { D(2026, 1, 1), D(2025, 1, 1) }, r);
        }

        [Fact]
        public void UndatedFilmsBetweenTwoDatedOnesLandBetweenThemInOrder()
        {
            var r = ListOrderDates.Fill(new DateTime?[] { D(2026, 1, 1), null, null, D(2025, 1, 1) }, Now);
            Assert.True(r[0] > r[1] && r[1] > r[2] && r[2] > r[3]);
            Assert.InRange(r[1], D(2025, 1, 1), D(2026, 1, 1));
            Assert.InRange(r[2], D(2025, 1, 1), D(2026, 1, 1));
        }

        [Fact]
        public void UndatedFilmsAboveTheNewestDatedOneAreBetweenItAndNow()
        {
            // Rebel, rated most recently, never logged: after the newest diary date, not after now.
            var r = ListOrderDates.Fill(new DateTime?[] { null, null, D(2026, 5, 23) }, Now);
            Assert.True(r[0] > r[1] && r[1] > r[2]);
            Assert.True(r[0] <= Now);
        }

        [Fact]
        public void UndatedFilmsBelowTheOldestDatedOneAreOlderThanIt()
        {
            var r = ListOrderDates.Fill(new DateTime?[] { D(2024, 1, 1), null, null }, Now);
            Assert.True(r[0] > r[1] && r[1] > r[2]);
        }

        [Fact]
        public void WithNoDatesAtAllTheOrderStillSurvives()
        {
            var r = ListOrderDates.Fill(new DateTime?[] { null, null, null }, Now);
            Assert.Equal(Now, r[0]);
            Assert.True(r[0] > r[1] && r[1] > r[2]);
        }

        [Fact]
        public void ARewatchLoggedOutOfOrderDoesNotPushUndatedNeighboursAboveNewerFilms()
        {
            // Position 1 was rated before position 0 but logged (rewatched) later.
            // The undated film at 2 must still sort below position 0.
            var r = ListOrderDates.Fill(new DateTime?[] { D(2025, 1, 1), D(2026, 1, 1), null, D(2024, 1, 1) }, Now);
            Assert.True(r[2] < r[0]);
            Assert.True(r[2] > r[3]);
        }

        [Fact]
        public void EmptyListIsFine()
        {
            Assert.Empty(ListOrderDates.Fill(Array.Empty<DateTime?>(), Now));
        }
    }
}
