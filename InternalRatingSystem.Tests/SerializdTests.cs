using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using Jellyfin.Plugin.InternalRating.Serializd;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InternalRatingSystem.Tests
{
    /// <summary>
    /// Records every write so the tests can assert on TRAFFIC, not on the
    /// result counters. The Letterboxd work shipped a "likes pushed" counter
    /// that incremented without a like ever being sent, and the test that was
    /// supposed to catch it asserted the counter.
    /// </summary>
    internal sealed class FakeSerializdWriter : ISerializdWriter
    {
        public List<(int Show, double Stars, string? Review)> Shows { get; } = new();
        public List<(int Show, int Season, int Episode, double Stars, string? Review)> Episodes { get; } = new();

        /// <summary>Shows Serializd does not have.</summary>
        public HashSet<int> Unknown { get; } = new();

        /// <summary>Status returned by every write.</summary>
        public SerializdWriteStatus Status { get; set; } = SerializdWriteStatus.Ok;

        public Task<SerializdWriteResult> RateShowAsync(int showTmdbId, double stars, string? review, CancellationToken ct = default)
        {
            if (Unknown.Contains(showTmdbId))
                return Task.FromResult(new SerializdWriteResult(SerializdWriteStatus.NotFound));
            if (Status == SerializdWriteStatus.Ok) Shows.Add((showTmdbId, stars, review));
            return Task.FromResult(new SerializdWriteResult(Status));
        }

        public Task<SerializdWriteResult> RateEpisodeAsync(
            int showTmdbId, int seasonNumber, int episodeNumber, double stars, string? review, CancellationToken ct = default)
        {
            if (Unknown.Contains(showTmdbId))
                return Task.FromResult(new SerializdWriteResult(SerializdWriteStatus.NotFound));
            if (Status == SerializdWriteStatus.Ok) Episodes.Add((showTmdbId, seasonNumber, episodeNumber, stars, review));
            return Task.FromResult(new SerializdWriteResult(Status));
        }
    }

    internal sealed class FakeSerializdGatherer : ISerializdGatherer
    {
        private readonly IReadOnlyList<SerializdRating> _items;
        public FakeSerializdGatherer(params SerializdRating[] items) => _items = items;
        public Task<IReadOnlyList<SerializdRating>> GatherAsync(string userId) => Task.FromResult(_items);
    }

    /// <summary>Serves canned responses so the session can be exercised offline.</summary>
    internal sealed class StubHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly System.Net.HttpStatusCode _code;
        private readonly string _body;
        public System.Collections.Generic.List<string> Paths { get; } = new();

        public StubHandler(System.Net.HttpStatusCode code, string body) { _code = code; _body = body; }

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(_code)
            {
                Content = new System.Net.Http.StringContent(_body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    public class SerializdTests : IDisposable
    {
        private readonly string _dir;
        private readonly LetterboxdPushLedger _ledger;
        private const string User = "user-1";

        public SerializdTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "startrack-szd-" + Guid.NewGuid().ToString("N"));
            _ledger = new LetterboxdPushLedger(new FakePaths(_dir));
        }

        public void Dispose()
        {
            _ledger.Dispose();
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
            GC.SuppressFinalize(this);
        }

        private SerializdPushService Service(ISerializdGatherer g) =>
            new(g, _ledger, NullLogger<SerializdPushService>.Instance);

        private static SerializdUserSettings Settings(
            bool series = true, bool episodes = true, bool reviews = false,
            SerializdDirection dir = SerializdDirection.ExportOnly) => new()
            {
                Email = "a@b.c", Direction = dir,
                PushSeries = series, PushEpisodes = episodes, PushReviews = reviews
            };

        // -----------------------------------------------------------------
        // Scale. This is the FOURTH rating scale in the plugin and the
        // opposite of Letterboxd's diary API, which takes 0.5-5.0 raw. Issue
        // #19 was exactly a scale getting flipped, so it is pinned here.
        // -----------------------------------------------------------------

        [Theory]
        [InlineData(0.5, 1)]
        [InlineData(1.0, 2)]
        [InlineData(2.5, 5)]
        [InlineData(3.5, 7)]
        [InlineData(5.0, 10)]
        public void Scale_doubles_half_stars(double stars, int expected) =>
            Assert.Equal(expected, SerializdWriteService.ToSerializdScale(stars));

        [Fact]
        public void Scale_treats_zero_and_below_as_unrated()
        {
            Assert.Equal(0, SerializdWriteService.ToSerializdScale(0));
            Assert.Equal(0, SerializdWriteService.ToSerializdScale(-1));
        }

        [Fact]
        public void Scale_never_exceeds_ten()
        {
            // A corrupted 10-star row must not become a 20 Serializd rejects.
            Assert.Equal(10, SerializdWriteService.ToSerializdScale(10));
        }

        // -----------------------------------------------------------------
        // Payload shape. Transcribed from a working client, so the parts that
        // are easy to "tidy" into a break are asserted.
        // -----------------------------------------------------------------

        [Fact]
        public void Payload_always_carries_rating_even_when_unrated()
        {
            // Omitting the field returns HTTP 500 from Serializd.
            var p = SerializdWriteService.BuildPayload(1399, null, null, 0, null, isLog: false, isRewatch: false);
            Assert.True(p.ContainsKey("rating"));
            Assert.Equal(0, p["rating"]);
        }

        [Fact]
        public void Payload_uses_snake_case_keys()
        {
            var p = SerializdWriteService.BuildPayload(1399, 3624, 5, 4.0, "good", isLog: true, isRewatch: false);
            foreach (var key in new[] { "show_id", "season_id", "episode_number", "review_text", "is_log", "is_rewatch", "contains_spoiler", "allows_comments" })
                Assert.True(p.ContainsKey(key), $"missing {key}");

            Assert.Equal(1399, p["show_id"]);
            Assert.Equal(3624, p["season_id"]);
            Assert.Equal(5, p["episode_number"]);
            Assert.Equal(8, p["rating"]);
        }

        [Fact]
        public void Payload_serialises_a_bare_rating_with_null_season_and_episode()
        {
            // A whole-series rating must send nulls rather than omitting the
            // keys — the API distinguishes the two.
            var json = JsonSerializer.Serialize(
                SerializdWriteService.BuildPayload(1399, null, null, 4.5, null, isLog: false, isRewatch: false));
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("season_id").ValueKind);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("episode_number").ValueKind);
            Assert.Equal(9, doc.RootElement.GetProperty("rating").GetInt32());
        }

        [Fact]
        public void Review_text_forces_a_log_entry()
        {
            // review_text is silently discarded unless is_log is true.
            var withText = SerializdWriteService.BuildPayload(1399, null, null, 4.0, "loved it", isLog: true, isRewatch: false);
            Assert.Equal(true, withText["is_log"]);
            Assert.Equal("loved it", withText["review_text"]);

            var bare = SerializdWriteService.BuildPayload(1399, null, null, 4.0, null, isLog: false, isRewatch: false);
            Assert.Equal(false, bare["is_log"]);
            Assert.Equal(string.Empty, bare["review_text"]);
        }

        // -----------------------------------------------------------------
        // Push orchestration
        // -----------------------------------------------------------------

        [Fact]
        public async Task Pushes_series_and_episode_ratings()
        {
            var w = new FakeSerializdWriter();
            var svc = Service(new FakeSerializdGatherer(
                new SerializdRating(1399, null, null, 4.5, null),
                new SerializdRating(1399, 1, 9, 5.0, null)));

            var r = await svc.PushAsync(User, w, Settings(), delayMs: 0);

            Assert.Equal((1399, 4.5, (string?)null), w.Shows.Single());
            Assert.Equal((1399, 1, 9, 5.0, (string?)null), w.Episodes.Single());
            Assert.Equal(1, r.Series);
            Assert.Equal(1, r.Episodes);
        }

        [Fact]
        public async Task Episode_toggle_off_sends_no_episode_writes()
        {
            var w = new FakeSerializdWriter();
            var svc = Service(new FakeSerializdGatherer(
                new SerializdRating(1399, 1, 9, 5.0, null)));

            await svc.PushAsync(User, w, Settings(episodes: false), delayMs: 0);

            Assert.Empty(w.Episodes);
            Assert.Empty(w.Shows);
        }

        [Fact]
        public async Task Series_toggle_off_sends_no_series_writes()
        {
            var w = new FakeSerializdWriter();
            var svc = Service(new FakeSerializdGatherer(
                new SerializdRating(1399, null, null, 4.0, null)));

            await svc.PushAsync(User, w, Settings(series: false), delayMs: 0);

            Assert.Empty(w.Shows);
        }

        [Fact]
        public async Task Reviews_are_only_sent_when_the_toggle_is_on()
        {
            var w = new FakeSerializdWriter();
            var rating = new SerializdRating(1399, null, null, 4.0, "loved it");

            await Service(new FakeSerializdGatherer(rating)).PushAsync(User, w, Settings(reviews: false), delayMs: 0);
            Assert.Null(w.Shows.Single().Review);

            // A fresh ledger key: same rating, toggle on.
            var w2 = new FakeSerializdWriter();
            await Service(new FakeSerializdGatherer(rating)).PushAsync("user-2", w2, Settings(reviews: true), delayMs: 0);
            Assert.Equal("loved it", w2.Shows.Single().Review);
        }

        [Fact]
        public async Task Unchanged_ratings_are_not_re_sent()
        {
            var w = new FakeSerializdWriter();
            var g = new FakeSerializdGatherer(new SerializdRating(1399, null, null, 4.0, null));

            await Service(g).PushAsync(User, w, Settings(), delayMs: 0);
            var second = await Service(g).PushAsync(User, w, Settings(), delayMs: 0);

            Assert.Single(w.Shows);          // still one write, not two
            Assert.Equal(1, second.Unchanged);
            Assert.Equal(0, second.Series);
        }

        [Fact]
        public async Task A_changed_star_rating_is_re_sent()
        {
            var w = new FakeSerializdWriter();
            await Service(new FakeSerializdGatherer(new SerializdRating(1399, null, null, 4.0, null)))
                .PushAsync(User, w, Settings(), delayMs: 0);
            await Service(new FakeSerializdGatherer(new SerializdRating(1399, null, null, 5.0, null)))
                .PushAsync(User, w, Settings(), delayMs: 0);

            Assert.Equal(2, w.Shows.Count);
            Assert.Equal(5.0, w.Shows.Last().Stars);
        }

        [Fact]
        public async Task An_episode_does_not_mask_its_own_series_in_the_ledger()
        {
            // Both carry the same TMDb id. A flat ledger key would let the
            // series write mark the episode as already done.
            var w = new FakeSerializdWriter();
            var svc = Service(new FakeSerializdGatherer(
                new SerializdRating(1399, null, null, 4.0, null),
                new SerializdRating(1399, 1, 1, 4.0, null),
                new SerializdRating(1399, 1, 2, 4.0, null)));

            var r = await svc.PushAsync(User, w, Settings(), delayMs: 0);

            Assert.Equal(1, r.Series);
            Assert.Equal(2, r.Episodes);
            Assert.Equal(0, r.Unchanged);
        }

        [Fact]
        public async Task A_failed_write_is_retried_next_run()
        {
            var w = new FakeSerializdWriter { Status = SerializdWriteStatus.Failed };
            var g = new FakeSerializdGatherer(new SerializdRating(1399, null, null, 4.0, null));

            var first = await Service(g).PushAsync(User, w, Settings(), delayMs: 0);
            Assert.Equal(0, first.Series);

            w.Status = SerializdWriteStatus.Ok;
            var second = await Service(g).PushAsync(User, w, Settings(), delayMs: 0);

            Assert.Equal(1, second.Series);
            Assert.Single(w.Shows);
        }

        [Fact]
        public async Task An_expired_token_stops_the_run_rather_than_hammering()
        {
            var w = new FakeSerializdWriter { Status = SerializdWriteStatus.NeedsReauth };
            var svc = Service(new FakeSerializdGatherer(
                new SerializdRating(1, null, null, 4.0, null),
                new SerializdRating(2, null, null, 4.0, null),
                new SerializdRating(3, null, null, 4.0, null)));

            var r = await svc.PushAsync(User, w, Settings(), delayMs: 0);

            Assert.NotNull(r.Error);
            Assert.Equal(0, r.Series);
        }

        [Fact]
        public async Task Shows_serializd_does_not_have_are_counted_not_retried_forever()
        {
            var w = new FakeSerializdWriter();
            w.Unknown.Add(1399);

            var r = await Service(new FakeSerializdGatherer(new SerializdRating(1399, null, null, 4.0, null)))
                .PushAsync(User, w, Settings(), delayMs: 0);

            Assert.Equal(1, r.Unmatched);
            Assert.Equal(0, r.Series);
        }

        [Fact]
        public async Task Direction_off_writes_nothing()
        {
            var w = new FakeSerializdWriter();
            var r = await Service(new FakeSerializdGatherer(new SerializdRating(1399, null, null, 4.0, null)))
                .PushAsync(User, w, Settings(dir: SerializdDirection.Off), delayMs: 0);

            Assert.Empty(w.Shows);
            Assert.Equal(0, r.TotalWritten);
        }

        [Fact]
        public async Task Per_run_cap_defers_the_rest_instead_of_dropping_it()
        {
            var w = new FakeSerializdWriter();
            var items = Enumerable.Range(1, 10)
                .Select(i => new SerializdRating(i, null, null, 4.0, null)).ToArray();

            var r = await Service(new FakeSerializdGatherer(items))
                .PushAsync(User, w, Settings(), maxItems: 4, delayMs: 0);

            Assert.Equal(4, w.Shows.Count);
            Assert.Equal(6, r.Remaining);
        }

        // -----------------------------------------------------------------
        // Settings storage
        // -----------------------------------------------------------------


        // -----------------------------------------------------------------
        // Session + season lookup, against the real response shape observed
        // live on 2026-08-30 rather than an invented one. The Simkl bug was
        // a test that agreed with the code instead of with the service.
        // -----------------------------------------------------------------

        // Trimmed from a live GET /api/show/1399. Note that every season
        // carries the id under BOTH "id" and "seasonId".
        private const string ShowJson = """
        {"id":1399,"name":"Game of Thrones","numSeasons":8,
         "seasons":[{"id":3627,"seasonId":3627,"seasonNumber":0,"name":"Specials"},
                    {"id":3624,"seasonId":3624,"seasonNumber":1,"name":"Season 1"},
                    {"id":3625,"seasonId":3625,"seasonNumber":2,"name":"Season 2"}]}
        """;

        [Fact]
        public async Task Every_request_lands_under_the_api_prefix()
        {
            // A BaseAddress without a trailing slash, or a path with a leading
            // one, silently resolves against the host root and drops /api. The
            // bench caught this as a 404 on sign-in where a direct curl to the
            // same endpoint returned 401, so the resolved path is pinned here.
            var handler = new StubHandler(System.Net.HttpStatusCode.OK, "{\"token\":\"t\"}");
            using var session = new SerializdSession(NullLogger<SerializdTests>.Instance, handler);

            await session.AuthenticateAsync("a@b.c", "pw");
            await session.GetAsync("/show/1399");
            await session.PostJsonAsync("/show/reviews/add", "{}");

            Assert.Equal(
                new[] { "/api/login", "/api/show/1399", "/api/show/reviews/add" },
                handler.Paths.ToArray());
        }

        [Fact]
        public async Task Season_number_resolves_to_the_season_id()
        {
            using var session = new SerializdSession(NullLogger<SerializdTests>.Instance,
                new StubHandler(System.Net.HttpStatusCode.OK, ShowJson));
            var w = new SerializdWriteService(session, NullLogger<SerializdTests>.Instance);

            Assert.Equal(3624, await w.ResolveSeasonIdAsync(1399, 1, default));
            Assert.Equal(3625, await w.ResolveSeasonIdAsync(1399, 2, default));
            Assert.Null(await w.ResolveSeasonIdAsync(1399, 9, default));
        }

        [Fact]
        public async Task Season_id_falls_back_to_seasonId_when_id_is_absent()
        {
            const string json = """
            {"seasons":[{"seasonId":3624,"seasonNumber":1}]}
            """;
            using var session = new SerializdSession(NullLogger<SerializdTests>.Instance,
                new StubHandler(System.Net.HttpStatusCode.OK, json));
            var w = new SerializdWriteService(session, NullLogger<SerializdTests>.Instance);

            Assert.Equal(3624, await w.ResolveSeasonIdAsync(1399, 1, default));
        }

        [Fact]
        public async Task Season_lookup_is_cached_per_show()
        {
            var handler = new StubHandler(System.Net.HttpStatusCode.OK, ShowJson);
            using var session = new SerializdSession(NullLogger<SerializdTests>.Instance, handler);
            var w = new SerializdWriteService(session, NullLogger<SerializdTests>.Instance);

            await w.ResolveSeasonIdAsync(1399, 1, default);
            await w.ResolveSeasonIdAsync(1399, 2, default);

            // Every episode of a series would otherwise repeat this request.
            Assert.Single(handler.Paths);
        }

        [Fact]
        public async Task Login_rejection_is_reported_as_bad_credentials()
        {
            // Serializd answers 401 {"message":"User does not exist."} - confirmed live.
            using var session = new SerializdSession(NullLogger<SerializdTests>.Instance,
                new StubHandler(System.Net.HttpStatusCode.Unauthorized, "{\"message\":\"User does not exist.\"}"));

            var r = await session.AuthenticateAsync("a@b.c", "wrong");

            Assert.False(r.Ok);
            Assert.Equal(SerializdAuthStatus.BadCredentials, r.Status);
            Assert.False(session.IsAuthenticated);
        }

        [Fact]
        public async Task A_login_with_no_token_is_not_treated_as_signed_in()
        {
            using var session = new SerializdSession(NullLogger<SerializdTests>.Instance,
                new StubHandler(System.Net.HttpStatusCode.OK, "{\"username\":\"someone\"}"));

            var r = await session.AuthenticateAsync("a@b.c", "pw");

            Assert.False(r.Ok);
            Assert.False(session.IsAuthenticated);
        }

        [Fact]
        public async Task Writing_without_a_session_asks_for_reauth_rather_than_posting()
        {
            var handler = new StubHandler(System.Net.HttpStatusCode.OK, "{}");
            using var session = new SerializdSession(NullLogger<SerializdTests>.Instance, handler);
            var w = new SerializdWriteService(session, NullLogger<SerializdTests>.Instance);

            var r = await w.RateShowAsync(1399, 4.0, null);

            Assert.Equal(SerializdWriteStatus.NeedsReauth, r.Status);
            Assert.Empty(handler.Paths);
        }

        [Fact]
        public async Task Password_is_never_stored_in_plain_text()
        {
            var path = Path.Combine(_dir, "settings");
            using var repo = new SerializdSettingsRepository(new FakePaths(path));

            var ok = await repo.SetAccountAsync(User, "a@b.c", "hunter2", SerializdDirection.ExportOnly, true, false, false);
            Assert.True(ok);

            var file = Path.Combine(path, "InternalRating", "serializd.json");
            Assert.DoesNotContain("hunter2", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_null_password_keeps_the_stored_one_and_an_empty_string_clears_it()
        {
            using var repo = new SerializdSettingsRepository(new FakePaths(Path.Combine(_dir, "settings2")));

            await repo.SetAccountAsync(User, "a@b.c", "hunter2", SerializdDirection.ExportOnly, true, false, false);
            var enc = (await repo.GetAsync(User)).PasswordEnc;
            Assert.False(string.IsNullOrEmpty(enc));

            // Changing a toggle must not require re-sending the secret.
            await repo.SetAccountAsync(User, "a@b.c", null, SerializdDirection.ExportOnly, true, true, false);
            var after = await repo.GetAsync(User);
            Assert.Equal(enc, after.PasswordEnc);
            Assert.True(after.PushEpisodes);

            await repo.SetAccountAsync(User, "a@b.c", string.Empty, SerializdDirection.Off, true, false, false);
            Assert.True(string.IsNullOrEmpty((await repo.GetAsync(User)).PasswordEnc));
        }

        [Fact]
        public async Task Settings_survive_a_restart()
        {
            var path = Path.Combine(_dir, "settings3");
            using (var repo = new SerializdSettingsRepository(new FakePaths(path)))
                await repo.SetAccountAsync(User, "a@b.c", "hunter2", SerializdDirection.ExportOnly, true, true, true);

            using var reloaded = new SerializdSettingsRepository(new FakePaths(path));
            var s = await reloaded.GetAsync(User);

            Assert.Equal("a@b.c", s.Email);
            Assert.Equal(SerializdDirection.ExportOnly, s.Direction);
            Assert.True(s.PushSeries);
            Assert.True(s.PushEpisodes);
            Assert.True(s.PushReviews);
            Assert.Equal("hunter2", LetterboxdSecretProtector.Unprotect(s.PasswordEnc));
        }
    }
}
