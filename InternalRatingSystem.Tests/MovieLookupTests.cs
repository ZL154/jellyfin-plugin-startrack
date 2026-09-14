using System;
using System.Collections.Generic;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.InternalRating.Tests
{
    /// <summary>
    /// Title-and-year matching from Letterboxd to the library. Pinned after a
    /// real corruption: the normaliser strips leading articles, so "Fall"
    /// (2022) and "The Fall" (2006) share a key, and the matcher returned the
    /// lone candidate regardless of a sixteen-year gap — writing a 1-star
    /// rating of the 2022 film over the user's own 4-star rating of the 2006
    /// one. A known year is a constraint, not a tie-breaker.
    /// </summary>
    public class MovieLookupTests
    {
        private static Movie M(string name, int? year) => new Movie { Name = name, ProductionYear = year, Id = Guid.NewGuid() };

        private static LetterboxdSyncService.MovieLookup Lookup(params BaseItem[] items)
            => new LetterboxdSyncService.MovieLookup(new List<BaseItem>(items), NullLogger.Instance);

        [Fact]
        public void ADifferentFilmWithTheSameTitleKeyAndAFarYearDoesNotMatch()
        {
            var lookup = Lookup(M("The Fall", 2006));
            Assert.Null(lookup.Find("Fall", 2022, out var ambiguous));
            Assert.False(ambiguous);
        }

        [Fact]
        public void TheRightFilmStillMatchesWhenTheYearAgrees()
        {
            var lookup = Lookup(M("The Fall", 2006));
            var hit = lookup.Find("The Fall", 2006, out _);
            Assert.NotNull(hit);
            Assert.Equal("The Fall", hit!.Name);
        }

        [Fact]
        public void AYearOffByOneStillMatches()
        {
            // Letterboxd and TMDb disagree by a year on festival releases.
            var lookup = Lookup(M("Parasite", 2019));
            Assert.NotNull(lookup.Find("Parasite", 2020, out _));
        }

        [Fact]
        public void WithTwoSameTitledFilmsTheYearPicksTheRightOne()
        {
            var lookup = Lookup(M("Heat", 1995), M("Heat", 1986));
            Assert.Equal(1986, lookup.Find("Heat", 1986, out _)!.ProductionYear);
            Assert.Equal(1995, lookup.Find("Heat", 1995, out _)!.ProductionYear);
            Assert.Null(lookup.Find("Heat", 2013, out _));   // a third Heat the library lacks
        }

        [Fact]
        public void ALibraryItemWithNoYearCanStillMatchOnTitle()
        {
            // Nothing to contradict the year with.
            var lookup = Lookup(M("Solaris", null));
            Assert.NotNull(lookup.Find("Solaris", 1972, out _));
        }

        [Fact]
        public void NoYearFromTheSourceFallsBackToTitleAsBefore()
        {
            var lookup = Lookup(M("The Fall", 2006));
            Assert.NotNull(lookup.Find("Fall", null, out _));
        }
    }
}
