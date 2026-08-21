using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.ExternalSync;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InternalRatingSystem.Tests
{
    // ---------------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------------

    internal sealed class FakeRatingGatherer : IRatingGatherer
    {
        private readonly IReadOnlyList<ExternalRating> _items;
        public FakeRatingGatherer(params ExternalRating[] items) => _items = items;
        public Task<IReadOnlyList<ExternalRating>> GatherAsync(string userId) => Task.FromResult(_items);
    }

    internal sealed class FakeLikedGatherer : ILikedGatherer
    {
        private readonly IReadOnlyList<ExternalRating> _items;
        public FakeLikedGatherer(params ExternalRating[] items) => _items = items;
        public Task<IReadOnlyList<ExternalRating>> GatherLikedAsync(string userId) => Task.FromResult(_items);
    }

    /// <summary>Records every call so tests can assert on write traffic.</summary>
    internal sealed class FakeWriter : ILetterboxdWriter
    {
        public List<int> Resolved { get; } = new();
        public List<(string Slug, double? Stars)> Rated { get; } = new();
        public List<string> Watched { get; } = new();
        public List<(string Slug, DateTime Date)> Diary { get; } = new();

        /// <summary>TMDb ids Letterboxd doesn't know about.</summary>
        public HashSet<int> Unknown { get; } = new();

        /// <summary>Status returned by every write call.</summary>
        public LetterboxdWriteStatus WriteStatus { get; set; } = LetterboxdWriteStatus.Ok;

        public Task<LetterboxdFilm?> ResolveFilmAsync(int tmdbId, CancellationToken ct = default)
        {
            Resolved.Add(tmdbId);
            return Task.FromResult<LetterboxdFilm?>(
                Unknown.Contains(tmdbId) ? null : new LetterboxdFilm($"film-{tmdbId}", tmdbId.ToString(), null));
        }

        private Task<LetterboxdWriteResult> R() =>
            Task.FromResult(new LetterboxdWriteResult(WriteStatus, WriteStatus.ToString()));

        public Task<LetterboxdWriteResult> SetRatingAsync(LetterboxdFilm film, double? stars, CancellationToken ct = default)
        {
            if (WriteStatus == LetterboxdWriteStatus.Ok) Rated.Add((film.Slug, stars));
            return R();
        }

        public Task<LetterboxdWriteResult> SetWatchedAsync(LetterboxdFilm film, CancellationToken ct = default)
        {
            if (WriteStatus == LetterboxdWriteStatus.Ok) Watched.Add(film.Slug);
            return R();
        }

        public List<string> LikedFilms { get; } = new();

        /// <summary>Set to simulate a Letterboxd build with no standalone like endpoint.</summary>
        public bool LikeUnsupported { get; set; }

        public Task<LetterboxdWriteResult> SetLikedAsync(LetterboxdFilm film, CancellationToken ct = default)
        {
            if (LikeUnsupported)
                return Task.FromResult(new LetterboxdWriteResult(LetterboxdWriteStatus.FilmNotFound));
            if (WriteStatus == LetterboxdWriteStatus.Ok) LikedFilms.Add(film.Slug);
            return R();
        }

        public Task<LetterboxdWriteResult> LogEntryAsync(
            LetterboxdFilm film, DateTime watchedAt, double? rating, bool liked, bool rewatch,
            string? review = null, bool containsSpoilers = false, CancellationToken ct = default)
        {
            if (WriteStatus == LetterboxdWriteStatus.Ok) Diary.Add((film.Slug, watchedAt));
            return R();
        }
    }

    internal sealed class FakePaths : IApplicationPaths
    {
        public FakePaths(string root)
        {
            DataPath = root;
            Directory.CreateDirectory(root);
        }
        public string ProgramDataPath => DataPath;
        public string WebPath => DataPath;
        public string ProgramSystemPath => DataPath;
        public string DataPath { get; }
        public string ImageCachePath => DataPath;
        public string PluginsPath => DataPath;
        public string PluginConfigurationsPath => DataPath;
        public string LogDirectoryPath => DataPath;
        public string ConfigurationDirectoryPath => DataPath;
        public string SystemConfigurationFilePath => Path.Combine(DataPath, "system.xml");
        public string CachePath { get; set; } = string.Empty;
        public string TempDirectory => Path.Combine(DataPath, "tmp");
        public string VirtualDataPath => DataPath;
        public string TrickplayPath => DataPath;
        public string BackupPath => Path.Combine(DataPath, "backup");

        // Host-startup concerns the ledger never touches.
        public void MakeSanityCheckOrThrow() { }
        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false) { }
    }

    // ---------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------

    public class LetterboxdPushTests : IDisposable
    {
        private readonly string _dir;
        private readonly LetterboxdPushLedger _ledger;
        private const string User = "user-1";

        public LetterboxdPushTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "startrack-lb-push-" + Guid.NewGuid().ToString("N"));
            _ledger = new LetterboxdPushLedger(new FakePaths(_dir));
        }

        public void Dispose()
        {
            _ledger.Dispose();
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
            GC.SuppressFinalize(this);
        }

        private static ExternalRating Movie(int tmdb, double stars, DateTime? at = null) =>
            new(null, tmdb, null, $"Movie {tmdb}", 2020, "movie", stars, at ?? new DateTime(2026, 8, 1, 20, 0, 0, DateTimeKind.Utc));

        private static LetterboxdUserSettings Settings(
            LetterboxdDirection dir = LetterboxdDirection.TwoWay,
            bool ratings = true, bool watched = true, bool liked = true,
            DateTime? diarySince = null) => new()
            {
                Username = "someone", Direction = dir,
                PushRatings = ratings, PushWatched = watched, PushLiked = liked,
                PushDiary = true,
                // Diary entries only apply at/after the switch-on point. Tests that
                // exercise diary writing need a cutoff in the past; the default here
                // is deliberately old so the existing cases keep testing what they
                // were written to test.
                DiaryLoggingSince = diarySince ?? new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };

        private LetterboxdPushService Service(IRatingGatherer g, ILikedGatherer? l = null) =>
            new(g, _ledger, NullLogger<LetterboxdPushService>.Instance, l);

        // ---- direction gating ----

        [Theory]
        [InlineData(LetterboxdDirection.Off)]
        [InlineData(LetterboxdDirection.ImportOnly)]
        public async Task DoesNothing_WhenDirectionIsNotExporting(LetterboxdDirection dir)
        {
            var w = new FakeWriter();
            var res = await Service(new FakeRatingGatherer(Movie(1, 4.0)))
                .PushAsync(User, w, Settings(dir), writeDiaryEntries: true);

            Assert.Empty(w.Resolved);      // must not even look films up
            Assert.Equal(0, res.TotalWritten);
        }

        // ---- the duplicate-diary guard ----

        [Fact]
        public async Task DiaryEntry_IsWrittenOnce_ThenNeverAgain()
        {
            // The whole point of the ledger. A timer-driven push must not add a
            // fresh diary entry on every tick.
            var w = new FakeWriter();
            var svc = Service(new FakeRatingGatherer(Movie(27205, 4.0)));

            var first = await svc.PushAsync(User, w, Settings(), writeDiaryEntries: true);
            Assert.Equal(1, first.DiaryEntries);
            Assert.Single(w.Diary);

            // Simulate the next four scheduled runs.
            for (var i = 0; i < 4; i++)
            {
                var again = await svc.PushAsync(User, w, Settings(), writeDiaryEntries: true);
                Assert.Equal(0, again.DiaryEntries);
                Assert.Equal(1, again.SkippedAlreadyLogged);
            }

            Assert.Single(w.Diary);   // still exactly one entry on Letterboxd
        }

        [Fact]
        public async Task DiaryEntry_IsNotRecorded_WhenTheWriteFailed()
        {
            // Recording optimistically would lose the entry forever.
            var w = new FakeWriter { WriteStatus = LetterboxdWriteStatus.Failed };
            var svc = Service(new FakeRatingGatherer(Movie(500, 3.0)));

            await svc.PushAsync(User, w, Settings(), writeDiaryEntries: true);
            Assert.False(await _ledger.HasAsync(User, 500, new DateTime(2026, 8, 1)));

            // Recovers on a later run once Letterboxd is reachable again.
            w.WriteStatus = LetterboxdWriteStatus.Ok;
            var res = await svc.PushAsync(User, w, Settings(), writeDiaryEntries: true);
            Assert.Equal(1, res.DiaryEntries);
        }

        [Fact]
        public async Task DiaryEntries_AreNotWritten_UnlessExplicitlyEnabled()
        {
            var w = new FakeWriter();
            var res = await Service(new FakeRatingGatherer(Movie(1, 4.0)))
                .PushAsync(User, w, Settings(), writeDiaryEntries: false);

            Assert.Empty(w.Diary);
            Assert.Equal(0, res.DiaryEntries);
            Assert.Single(w.Rated);      // ...but the idempotent writes still happen
        }

        // ---- idempotent writes repeat freely ----

        [Fact]
        public async Task RatingAndWatched_AreResentEveryRun_BecauseTheyAreIdempotent()
        {
            var w = new FakeWriter();
            var svc = Service(new FakeRatingGatherer(Movie(42, 4.5)));

            await svc.PushAsync(User, w, Settings(), writeDiaryEntries: false);
            await svc.PushAsync(User, w, Settings(), writeDiaryEntries: false);

            Assert.Equal(2, w.Rated.Count);
            Assert.Equal(2, w.Watched.Count);
            Assert.All(w.Rated, r => Assert.Equal(4.5, r.Stars));
        }

        // ---- matching safety ----

        [Fact]
        public async Task SkipsItemsWithNoTmdbId_RatherThanTitleMatching()
        {
            // Title-matching across catalogues is how you rate the wrong film.
            var noId = new ExternalRating("tt123", null, null, "Ambiguous", 2020, "movie", 4.0, DateTime.UtcNow);
            var w = new FakeWriter();

            var res = await Service(new FakeRatingGatherer(noId)).PushAsync(User, w, Settings(), false);

            Assert.Empty(w.Resolved);
            Assert.Equal(1, res.Unmatched);
        }

        [Fact]
        public async Task SkipsNonMovies()
        {
            // Letterboxd is films only; a series would 404 or match a same-named film.
            var show = new ExternalRating(null, 1396, null, "Breaking Bad", 2008, "show", 5.0, DateTime.UtcNow);
            var w = new FakeWriter();

            await Service(new FakeRatingGatherer(show)).PushAsync(User, w, Settings(), false);

            Assert.Empty(w.Resolved);
        }

        [Fact]
        public async Task CountsFilmsLetterboxdDoesNotHave_AsUnmatched()
        {
            var w = new FakeWriter();
            w.Unknown.Add(999);

            var res = await Service(new FakeRatingGatherer(Movie(999, 4.0))).PushAsync(User, w, Settings(), false);

            Assert.Equal(1, res.Unmatched);
            Assert.Empty(w.Rated);
        }

        // ---- liked films ----

        [Fact]
        public async Task LikedFilm_IsActuallyWritten_EvenWhenNeverRated()
        {
            // Liked and rated are separate lists — a union, not a filter.
            // This previously asserted only on the COUNTER, which passed while
            // no like was ever sent: likes rode along as a field on a diary
            // entry, so with diary logging off nothing reached Letterboxd.
            // Assert on the write, not the tally.
            var w = new FakeWriter();
            var res = await Service(new FakeRatingGatherer(), new FakeLikedGatherer(Movie(77, 0)))
                .PushAsync(User, w, Settings(), writeDiaryEntries: false);

            Assert.Contains(77, w.Resolved);
            Assert.Single(w.LikedFilms);
            Assert.Equal(1, res.Liked);
        }

        [Fact]
        public async Task LikedFilm_IsWritten_EvenWithDiaryLoggingOff()
        {
            var w = new FakeWriter();
            await Service(new FakeRatingGatherer(Movie(5, 4.0)), new FakeLikedGatherer(Movie(5, 4.0)))
                .PushAsync(User, w, Settings(), writeDiaryEntries: false);

            Assert.Single(w.LikedFilms);
            Assert.Empty(w.Diary);
        }

        [Fact]
        public async Task LikeCount_StaysZero_WhenLetterboxdRejectsTheEndpoint()
        {
            // Reporting a success we cannot prove is worse than reporting zero.
            var w = new FakeWriter { LikeUnsupported = true };
            var res = await Service(new FakeRatingGatherer(), new FakeLikedGatherer(Movie(88, 0)))
                .PushAsync(User, w, Settings(), writeDiaryEntries: false);

            Assert.Empty(w.LikedFilms);
            Assert.Equal(0, res.Liked);
            Assert.Null(res.Error);       // unsupported is not a run-ending failure
        }

        [Fact]
        public async Task LikedFilm_IsNotWritten_WhenPushLikedIsOff()
        {
            var w = new FakeWriter();
            await Service(new FakeRatingGatherer(), new FakeLikedGatherer(Movie(99, 0)))
                .PushAsync(User, w, Settings(liked: false), writeDiaryEntries: false);

            Assert.Empty(w.LikedFilms);
        }

        // ---- abort behaviour ----

        [Theory]
        [InlineData(LetterboxdWriteStatus.Cloudflare)]
        [InlineData(LetterboxdWriteStatus.NeedsReauth)]
        public async Task AbortsWholeRun_OnSessionLevelFailure(LetterboxdWriteStatus status)
        {
            // Continuing would fire one doomed request per film in the library,
            // which on Cloudflare looks exactly like the abuse that blocked us.
            var w = new FakeWriter { WriteStatus = status };
            var svc = Service(new FakeRatingGatherer(Movie(1, 4.0), Movie(2, 4.0), Movie(3, 4.0)));

            var res = await svc.PushAsync(User, w, Settings(), false);

            Assert.NotNull(res.Error);
            Assert.Single(w.Resolved);   // stopped after the first film, not all three
        }

        // ---- diary cutoff ----

        [Fact]
        public async Task DiaryEntry_IsSkipped_ForWatchesOlderThanTheCutoff()
        {
            // Enabling diary logging must NOT backfill. A user turning this on
            // usually has years of their own Letterboxd diary that StarTrack
            // cannot recognise as "the same watch", so writing history would
            // dump hundreds of duplicates into the one place that is painful to
            // clean up by hand.
            var old = Movie(1, 4.0, new DateTime(2020, 5, 1, 12, 0, 0, DateTimeKind.Utc));
            var w = new FakeWriter();

            var res = await Service(new FakeRatingGatherer(old)).PushAsync(
                User, w, Settings(diarySince: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                writeDiaryEntries: true);

            Assert.Empty(w.Diary);
            Assert.Equal(0, res.DiaryEntries);
            Assert.Single(w.Rated);   // the idempotent rating still goes out
        }

        [Fact]
        public async Task DiaryEntry_IsWritten_ForWatchesAfterTheCutoff()
        {
            var recent = Movie(2, 4.0, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));
            var w = new FakeWriter();

            var res = await Service(new FakeRatingGatherer(recent)).PushAsync(
                User, w, Settings(diarySince: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                writeDiaryEntries: true);

            Assert.Single(w.Diary);
            Assert.Equal(1, res.DiaryEntries);
        }

        [Fact]
        public async Task DiaryEntry_IsSkipped_WhenLoggingWasNeverEnabled()
        {
            // No cutoff stamped means diary logging was never switched on.
            var st = Settings();
            st.DiaryLoggingSince = null;
            var w = new FakeWriter();

            await Service(new FakeRatingGatherer(Movie(3, 4.0))).PushAsync(User, w, st, writeDiaryEntries: true);

            Assert.Empty(w.Diary);
        }

        [Fact]
        public async Task ContinuesPastAnOrdinaryPerFilmFailure()
        {
            var w = new FakeWriter { WriteStatus = LetterboxdWriteStatus.Failed };
            var svc = Service(new FakeRatingGatherer(Movie(1, 4.0), Movie(2, 4.0)));

            var res = await svc.PushAsync(User, w, Settings(), false);

            Assert.Null(res.Error);
            Assert.Equal(2, w.Resolved.Count);   // both attempted
        }
    }

    /// <summary>The 0–10 scale of the /s/film:{id}/rate/ endpoint.</summary>
    public class LetterboxdRateScaleTests
    {
        [Theory]
        [InlineData(0.5,  1)]
        [InlineData(1.0,  2)]
        [InlineData(2.5,  5)]
        [InlineData(4.5,  9)]
        [InlineData(5.0, 10)]
        public void HalfStars_AreDoubled_ForTheRateEndpoint(double stars, int expected)
        {
            // NB: the OTHER endpoint (/api/v0/log-entries) takes 0.5-5.0 raw.
            // These two must not be confused — see LetterboxdWriteService docs.
            Assert.Equal(expected, LetterboxdWriteService.ToRateEndpointScale(stars));
        }

        [Fact]
        public void NullOrZero_ClearsTheRating()
        {
            Assert.Equal(0, LetterboxdWriteService.ToRateEndpointScale(null));
            Assert.Equal(0, LetterboxdWriteService.ToRateEndpointScale(0));
        }

        [Fact]
        public void OutOfRangeValues_AreClamped()
        {
            Assert.Equal(10, LetterboxdWriteService.ToRateEndpointScale(7.0));
            Assert.Equal(1,  LetterboxdWriteService.ToRateEndpointScale(0.1));
        }
    }

    /// <summary>Ledger persistence.</summary>
    public class LetterboxdPushLedgerTests : IDisposable
    {
        private readonly string _dir;
        public LetterboxdPushLedgerTests() =>
            _dir = Path.Combine(Path.GetTempPath(), "startrack-lb-ledger-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
            GC.SuppressFinalize(this);
        }

        [Fact]
        public async Task SurvivesARestart()
        {
            var day = new DateTime(2026, 8, 11);
            using (var l = new LetterboxdPushLedger(new FakePaths(_dir)))
                await l.AddAsync("u", 27205, day);

            // Fresh instance = simulated server restart. Losing this would
            // re-log everything the next time the task runs.
            using var reloaded = new LetterboxdPushLedger(new FakePaths(_dir));
            Assert.True(await reloaded.HasAsync("u", 27205, day));
        }

        [Fact]
        public async Task IsScopedPerUser()
        {
            var day = new DateTime(2026, 8, 11);
            using var l = new LetterboxdPushLedger(new FakePaths(_dir));
            await l.AddAsync("alice", 1, day);

            Assert.True(await l.HasAsync("alice", 1, day));
            Assert.False(await l.HasAsync("bob", 1, day));
        }

        [Fact]
        public async Task TreatsDifferentDatesAsDifferentEntries()
        {
            // A genuine rewatch on another day is a separate diary entry.
            using var l = new LetterboxdPushLedger(new FakePaths(_dir));
            await l.AddAsync("u", 1, new DateTime(2026, 8, 11));

            Assert.False(await l.HasAsync("u", 1, new DateTime(2026, 8, 12)));
        }

        [Fact]
        public async Task IgnoresTimeOfDay()
        {
            // Letterboxd diary entries are a day, not an instant.
            using var l = new LetterboxdPushLedger(new FakePaths(_dir));
            await l.AddAsync("u", 1, new DateTime(2026, 8, 11, 9, 0, 0));

            Assert.True(await l.HasAsync("u", 1, new DateTime(2026, 8, 11, 23, 30, 0)));
        }

        [Fact]
        public async Task Seed_PreventsDuplicatingAManuallyBuiltDiary()
        {
            using var l = new LetterboxdPushLedger(new FakePaths(_dir));
            await l.SeedAsync("u", new[] { (27205, new DateTime(2026, 1, 1)), (1396, new DateTime(2026, 2, 2)) });

            Assert.True(await l.HasAsync("u", 27205, new DateTime(2026, 1, 1)));
            Assert.Equal(2, await l.CountAsync("u"));
        }

        [Fact]
        public async Task Clear_ForgetsAUser()
        {
            using var l = new LetterboxdPushLedger(new FakePaths(_dir));
            await l.AddAsync("u", 1, new DateTime(2026, 8, 11));
            await l.ClearAsync("u");

            Assert.Equal(0, await l.CountAsync("u"));
        }
    }
}
