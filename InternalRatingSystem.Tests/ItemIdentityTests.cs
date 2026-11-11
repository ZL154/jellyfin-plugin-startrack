using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Data;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.InternalRating.Tests
{
    /// <summary>
    /// What a stored item id refers to once the id itself has changed. The
    /// match rule is the whole safety of the relink: too loose and a rating
    /// lands on the wrong title, too tight and it is lost at every upgrade.
    /// </summary>
    public sealed class ItemIdentityTests
    {
        private static Movie Film(string name, int? year, string? tmdb = null, string? imdb = null)
        {
            var m = new Movie { Name = name, ProductionYear = year, Id = Guid.NewGuid() };
            if (tmdb != null) m.ProviderIds["Tmdb"] = tmdb;
            if (imdb != null) m.ProviderIds["Imdb"] = imdb;
            return m;
        }

        private static Series Show(string name, string? tvdb = null, string? imdb = null)
        {
            var s = new Series { Name = name, Id = Guid.NewGuid() };
            if (tvdb != null) s.ProviderIds["Tvdb"] = tvdb;
            if (imdb != null) s.ProviderIds["Imdb"] = imdb;
            return s;
        }

        [Fact]
        public void Movie_SameTmdb_Matches_AcrossNewId()
        {
            var a = ItemIdentity.FromItem(Film("Heat", 1995, tmdb: "949"))!;
            var b = ItemIdentity.FromItem(Film("Heat", 1995, tmdb: "949", imdb: "tt0113277"))!;
            Assert.True(a.SameAs(b));
            Assert.True(b.SameAs(a));
        }

        [Fact]
        public void Movie_OnlyTitleAndYear_NeverMatches()
        {
            // No provider ids: an upgrade of an unidentified film is a miss, never a guess.
            var a = ItemIdentity.FromItem(Film("Heat", 1995))!;
            var b = ItemIdentity.FromItem(Film("Heat", 1995))!;
            Assert.True(a.IsEmpty());
            Assert.False(a.SameAs(b));
        }

        [Fact]
        public void Movie_And_Series_WithSameImdb_DoNotMatch()
        {
            var film = ItemIdentity.FromItem(Film("Fargo", 1996, imdb: "tt0116282"))!;
            var show = ItemIdentity.FromItem(Show("Fargo", imdb: "tt0116282"))!;
            Assert.False(film.SameAs(show));
        }

        [Fact]
        public void ProviderValues_CompareCaseInsensitively_AndTrimmed()
        {
            var a = ItemIdentity.FromItem(Film("X", 2000, imdb: "TT0000001 "))!;
            var b = ItemIdentity.FromItem(Film("X", 2000, imdb: "tt0000001"))!;
            Assert.True(a.SameAs(b));
        }

        [Fact]
        public void Season_IsSeriesIds_PlusIndex()
        {
            var show = Show("Mando", tvdb: "361753");
            var s2 = new Season { Name = "Season 2", IndexNumber = 2, Id = Guid.NewGuid() };
            var s3 = new Season { Name = "Season 3", IndexNumber = 3, Id = Guid.NewGuid() };

            var a = ItemIdentity.FromItem(s2, show)!;
            var again = ItemIdentity.FromItem(new Season { Name = "Season 2", IndexNumber = 2, Id = Guid.NewGuid() }, show)!;
            var other = ItemIdentity.FromItem(s3, show)!;

            Assert.Equal("season", a.Kind);
            Assert.True(a.SameAs(again));
            Assert.False(a.SameAs(other));
            Assert.Contains("season:tvdb:361753/s2", a.Keys());
        }

        [Fact]
        public void Season_WithoutSeries_IsEmpty()
        {
            var s = new Season { Name = "Season 1", IndexNumber = 1, Id = Guid.NewGuid() };
            Assert.True(ItemIdentity.FromItem(s, null)!.IsEmpty());
        }

        [Fact]
        public void Episode_OwnIds_Match_EvenWhenSeriesUnknown()
        {
            var e1 = new Episode { Name = "Chapter 9", ParentIndexNumber = 2, IndexNumber = 1, Id = Guid.NewGuid() };
            e1.ProviderIds["Tvdb"] = "7905099";
            var e2 = new Episode { Name = "Chapter 9", Id = Guid.NewGuid() };
            e2.ProviderIds["Tvdb"] = "7905099";
            Assert.True(ItemIdentity.FromItem(e1)!.SameAs(ItemIdentity.FromItem(e2)!));
        }

        [Fact]
        public void Episode_WithoutOwnIds_FallsBackToSeriesAndNumbers()
        {
            var show = Show("Mando", tvdb: "361753");
            var e1 = new Episode { Name = "a", ParentIndexNumber = 2, IndexNumber = 1, Id = Guid.NewGuid() };
            var same = new Episode { Name = "b", ParentIndexNumber = 2, IndexNumber = 1, Id = Guid.NewGuid() };
            var next = new Episode { Name = "c", ParentIndexNumber = 2, IndexNumber = 2, Id = Guid.NewGuid() };
            var a = ItemIdentity.FromItem(e1, show)!;
            Assert.True(a.SameAs(ItemIdentity.FromItem(same, show)!));
            Assert.False(a.SameAs(ItemIdentity.FromItem(next, show)!));
            Assert.Contains("episode:tvdb:361753/s2e1", a.Keys());
        }

        [Fact]
        public void OtherKinds_AreNotIdentified()
        {
            Assert.Null(ItemIdentity.FromItem(new MediaBrowser.Controller.Entities.Folder { Name = "x", Id = Guid.NewGuid() }));
        }

        [Fact]
        public void CollectionIds_AreNotRecorded()
        {
            var m = Film("Heat", 1995, tmdb: "949");
            m.ProviderIds["TmdbCollection"] = "123";
            var id = ItemIdentity.FromItem(m)!;
            Assert.Single(id.Ids);
            Assert.Equal("949", id.Ids["Tmdb"]);
        }
    }

    /// <summary>The ledger: a JSON file that follows the stores and survives a restart.</summary>
    public sealed class ItemIdentityLedgerTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "startrack-ledger-" + Guid.NewGuid().ToString("N"));
        private string File_ => Path.Combine(_dir, "item-identities.json");

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static ItemIdentity Heat() => new()
        {
            Kind = "movie", Name = "Heat", Year = 1995,
            Ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = "949" }
        };

        [Fact]
        public async Task Record_Rename_Prune_RoundTripThroughDisk()
        {
            using (var l = new ItemIdentityLedger(File_))
            {
                Assert.True(await l.RecordAsync("old", Heat()));
                Assert.False(await l.RecordAsync("old", Heat()), "an unchanged identity is not a write");
            }

            using (var l = new ItemIdentityLedger(File_))
            {
                var got = await l.GetAsync("OLD");
                Assert.NotNull(got);
                Assert.Equal("949", got!.Ids["Tmdb"]);

                await l.RenameAsync("old", "new");
                Assert.Null(await l.GetAsync("old"));
                Assert.NotNull(await l.GetAsync("new"));

                Assert.Equal(1, await l.PruneAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "something-else" }));
                Assert.Empty(await l.AllAsync());
            }
        }

        [Fact]
        public async Task Record_RefreshesWhenProviderIdsArrive()
        {
            using var l = new ItemIdentityLedger(File_);
            var bare = new ItemIdentity { Kind = "movie", Name = "Heat", Year = 1995 };
            Assert.True(await l.RecordAsync("id", bare));
            Assert.True(await l.RecordAsync("id", Heat()), "metadata arriving after the scan is a change worth writing");
            Assert.Equal("949", (await l.GetAsync("id"))!.Ids["Tmdb"]);
        }

        [Fact]
        public async Task RecordMany_CountsOnlyChanges_AndWritesOnce()
        {
            using var l = new ItemIdentityLedger(File_);
            await l.RecordAsync("a", Heat());
            var n = await l.RecordManyAsync(new Dictionary<string, ItemIdentity>
            {
                ["a"] = Heat(),
                ["b"] = new ItemIdentity { Kind = "series", Name = "Mando", Ids = new(StringComparer.OrdinalIgnoreCase) { ["Tvdb"] = "361753" } }
            });
            Assert.Equal(1, n);
            Assert.Equal(2, (await l.AllAsync()).Count);
        }

        [Fact]
        public void CorruptFile_StartsEmpty()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(File_, "{not json");
            using var l = new ItemIdentityLedger(File_);
            Assert.Empty(l.AllAsync().GetAwaiter().GetResult());
        }
    }
}
