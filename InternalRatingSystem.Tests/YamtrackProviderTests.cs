using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.ExternalSync;
using Jellyfin.Plugin.InternalRating.ExternalSync.Providers;
using Xunit;

namespace Jellyfin.Plugin.InternalRating.Tests
{
    // =========================================================================
    // YamtrackProvider tests (TASK D)
    // All tests use StubHandler (defined in DeviceCodeOAuthTests.cs, same assembly).
    // =========================================================================

    public class YamtrackProviderTests
    {
        // ---------- helpers --------------------------------------------------

        private static HttpClient MakeClient(Func<HttpRequestMessage, HttpResponseMessage> fn)
            => new HttpClient(new StubHandler(fn));

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
            => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

        private static ProviderConnection ConnWith(string baseUrl = "http://yamtrack.local", string apiToken = "mytoken") =>
            new ProviderConnection
            {
                Direction = SyncDirection.TwoWay,
                BaseUrl   = baseUrl,
                ApiToken  = apiToken
            };

        // =====================================================================
        // Id property
        // =====================================================================

        [Fact]
        public void Id_IsYamtrack()
        {
            var provider = new YamtrackProvider(new HttpClient());
            Assert.Equal(ProviderId.Yamtrack, provider.Id);
        }

        // =====================================================================
        // EnsureTokenAsync — always false, no HTTP call
        // =====================================================================

        [Fact]
        public async Task EnsureTokenAsync_ReturnsFalse_WithNoHttpCall()
        {
            int callCount = 0;
            var client = MakeClient(_ =>
            {
                callCount++;
                return Json("{}");
            });

            var provider = new YamtrackProvider(client);
            var result   = await provider.EnsureTokenAsync(ConnWith(), CancellationToken.None);

            Assert.False(result);
            Assert.Equal(0, callCount);   // static token — no HTTP call ever
        }

        // =====================================================================
        // PushRatingsAsync
        // =====================================================================

        [Fact]
        public async Task PushRatingsAsync_PostsToCorrectUrl_WithCorrectBody_AndAuthHeader()
        {
            HttpRequestMessage? captured     = null;
            string?             capturedBody = null;

            var client = MakeClient(req =>
            {
                captured     = req;
                capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("""{"id":1}""", HttpStatusCode.Created);
            });

            var provider = new YamtrackProvider(client);
            var conn     = ConnWith(baseUrl: "http://yamtrack.local", apiToken: "secret-token");

            var rating = new ExternalRating(
                Imdb:      null,
                Tmdb:      27205,
                Tvdb:      null,
                Title:     "Inception",
                Year:      2010,
                MediaType: "movie",
                Stars:     4.0,
                RatedAt:   DateTime.UtcNow);

            var count = await provider.PushRatingsAsync(conn, new[] { rating }, CancellationToken.None);

            Assert.Equal(1, count);

            // URL: POST /api/v1/media/movie/
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Post, captured!.Method);
            Assert.Contains("/api/v1/media/movie/", captured.RequestUri!.PathAndQuery);

            // Authorization: Token header
            Assert.NotNull(captured.Headers.Authorization);
            Assert.Equal("Token",        captured.Headers.Authorization!.Scheme);
            Assert.Equal("secret-token", captured.Headers.Authorization.Parameter);

            // Body: media_id + source + score
            Assert.NotNull(capturedBody);
            using var doc = JsonDocument.Parse(capturedBody!);
            Assert.Equal(27205,  doc.RootElement.GetProperty("media_id").GetInt32());
            Assert.Equal("tmdb", doc.RootElement.GetProperty("source").GetString());
            // 4.0 stars → RatingScale.ToService10 = 8
            Assert.Equal(8, doc.RootElement.GetProperty("score").GetInt32());
        }

        [Fact]
        public async Task PushRatingsAsync_SkipsRatings_WithNoTmdbId()
        {
            int callCount = 0;
            var client = MakeClient(_ =>
            {
                callCount++;
                return Json("{}", HttpStatusCode.Created);
            });

            var provider = new YamtrackProvider(client);
            var conn     = ConnWith();

            var rating = new ExternalRating(
                Imdb:      "tt1234567",
                Tmdb:      null,   // no Tmdb — should be skipped
                Tvdb:      null,
                Title:     "No Tmdb",
                Year:      2020,
                MediaType: "movie",
                Stars:     3.0,
                RatedAt:   DateTime.UtcNow);

            var count = await provider.PushRatingsAsync(conn, new[] { rating }, CancellationToken.None);

            Assert.Equal(0, count);
            Assert.Equal(0, callCount);   // no HTTP call when all ratings are skipped
        }

        [Fact]
        public async Task PushRatingsAsync_ReturnsZero_WhenNullBaseUrl()
        {
            int callCount = 0;
            var client = MakeClient(_ => { callCount++; return Json("{}"); });

            var provider = new YamtrackProvider(client);
            var conn     = new ProviderConnection { BaseUrl = null, ApiToken = "tok" };

            var rating = new ExternalRating(
                Imdb: null, Tmdb: 123, Tvdb: null,
                Title: "Test", Year: 2020, MediaType: "movie",
                Stars: 3.0, RatedAt: DateTime.UtcNow);

            var count = await provider.PushRatingsAsync(conn, new[] { rating }, CancellationToken.None);

            Assert.Equal(0, count);
            Assert.Equal(0, callCount);
        }

        [Fact]
        public async Task PushRatingsAsync_ReturnsZero_WhenNullApiToken()
        {
            int callCount = 0;
            var client = MakeClient(_ => { callCount++; return Json("{}"); });

            var provider = new YamtrackProvider(client);
            var conn     = new ProviderConnection { BaseUrl = "http://yamtrack.local", ApiToken = null };

            var rating = new ExternalRating(
                Imdb: null, Tmdb: 123, Tvdb: null,
                Title: "Test", Year: 2020, MediaType: "movie",
                Stars: 3.0, RatedAt: DateTime.UtcNow);

            var count = await provider.PushRatingsAsync(conn, new[] { rating }, CancellationToken.None);

            Assert.Equal(0, count);
            Assert.Equal(0, callCount);
        }

        [Fact]
        public async Task PushRatingsAsync_ReturnsZero_OnEmptyList()
        {
            int callCount = 0;
            var client = MakeClient(_ => { callCount++; return Json("{}"); });

            var provider = new YamtrackProvider(client);
            var count    = await provider.PushRatingsAsync(ConnWith(), Array.Empty<ExternalRating>(), CancellationToken.None);

            Assert.Equal(0, count);
            Assert.Equal(0, callCount);
        }

        [Fact]
        public async Task PushRatingsAsync_MapsShowToTvType()
        {
            HttpRequestMessage? captured = null;
            var client = MakeClient(req =>
            {
                captured = req;
                return Json("{}", HttpStatusCode.Created);
            });

            var provider = new YamtrackProvider(client);
            var conn     = ConnWith();

            var rating = new ExternalRating(
                Imdb: null, Tmdb: 1396, Tvdb: null,
                Title: "Breaking Bad", Year: 2008, MediaType: "show",
                Stars: 5.0, RatedAt: DateTime.UtcNow);

            await provider.PushRatingsAsync(conn, new[] { rating }, CancellationToken.None);

            Assert.NotNull(captured);
            // "show" maps to "tv" path segment
            Assert.Contains("/api/v1/media/tv/", captured!.RequestUri!.PathAndQuery);
        }

        // =====================================================================
        // PullRatingsAsync
        // =====================================================================

        [Fact]
        public async Task PullRatingsAsync_ParsesMoviesAndShows()
        {
            const string movieJson = """
            [
              {
                "media_id": 27205,
                "source": "tmdb",
                "score": 8,
                "media": { "title": "Inception", "year": 2010 }
              }
            ]
            """;

            const string tvJson = """
            [
              {
                "media_id": 1396,
                "source": "tmdb",
                "score": 6,
                "media": { "title": "Breaking Bad", "year": 2008 }
              }
            ]
            """;

            var client = MakeClient(req =>
            {
                if (req.RequestUri!.PathAndQuery.Contains("/media/movie/"))
                    return Json(movieJson);
                if (req.RequestUri.PathAndQuery.Contains("/media/tv/"))
                    return Json(tvJson);
                return Json("[]");
            });

            var provider = new YamtrackProvider(client);
            var ratings  = await provider.PullRatingsAsync(ConnWith(), CancellationToken.None);

            Assert.Equal(2, ratings.Count);

            var movie = ratings[0];
            Assert.Equal("movie",     movie.MediaType);
            Assert.Equal(27205,       movie.Tmdb);
            Assert.Equal(4.0,         movie.Stars);   // score 8 → 4.0
            Assert.Equal("Inception", movie.Title);
            Assert.Equal(2010,        movie.Year);

            var show = ratings[1];
            Assert.Equal("show",         show.MediaType);
            Assert.Equal(1396,           show.Tmdb);
            Assert.Equal(3.0,            show.Stars);    // score 6 → 3.0
            Assert.Equal("Breaking Bad", show.Title);
        }

        [Fact]
        public async Task PullRatingsAsync_SkipsNullScoreItems()
        {
            const string movieJson = """
            [
              {
                "media_id": 27205,
                "source": "tmdb",
                "score": null,
                "media": { "title": "Not Yet Rated", "year": 2010 }
              },
              {
                "media_id": 550,
                "source": "tmdb",
                "score": 9,
                "media": { "title": "Fight Club", "year": 1999 }
              }
            ]
            """;

            var client = MakeClient(req =>
            {
                if (req.RequestUri!.PathAndQuery.Contains("/media/movie/"))
                    return Json(movieJson);
                return Json("[]");
            });

            var provider = new YamtrackProvider(client);
            var ratings  = await provider.PullRatingsAsync(ConnWith(), CancellationToken.None);

            // Only the rated item is returned
            Assert.Single(ratings);
            Assert.Equal("Fight Club", ratings[0].Title);
            Assert.Equal(4.5, ratings[0].Stars);  // score 9 → 4.5
        }

        [Fact]
        public async Task PullRatingsAsync_ReturnsEmpty_WhenNullBaseUrl()
        {
            int callCount = 0;
            var client   = MakeClient(_ => { callCount++; return Json("[]"); });
            var provider = new YamtrackProvider(client);
            var conn     = new ProviderConnection { BaseUrl = null, ApiToken = "tok" };

            var ratings = await provider.PullRatingsAsync(conn, CancellationToken.None);

            Assert.Empty(ratings);
            Assert.Equal(0, callCount);
        }

        [Fact]
        public async Task PullRatingsAsync_ReturnsEmpty_WhenNullApiToken()
        {
            int callCount = 0;
            var client   = MakeClient(_ => { callCount++; return Json("[]"); });
            var provider = new YamtrackProvider(client);
            var conn     = new ProviderConnection { BaseUrl = "http://yamtrack.local", ApiToken = null };

            var ratings = await provider.PullRatingsAsync(conn, CancellationToken.None);

            Assert.Empty(ratings);
            Assert.Equal(0, callCount);
        }

        [Fact]
        public async Task PullRatingsAsync_NullTmdb_WhenSourceIsNotTmdb()
        {
            // Items with source other than "tmdb" should have Tmdb = null
            const string movieJson = """
            [
              {
                "media_id": 12345,
                "source": "imdb",
                "score": 7,
                "media": { "title": "Some Movie", "year": 2005 }
              }
            ]
            """;

            var client = MakeClient(req =>
            {
                if (req.RequestUri!.PathAndQuery.Contains("/media/movie/"))
                    return Json(movieJson);
                return Json("[]");
            });

            var provider = new YamtrackProvider(client);
            var ratings  = await provider.PullRatingsAsync(ConnWith(), CancellationToken.None);

            Assert.Single(ratings);
            // Tmdb should be null because source is "imdb" not "tmdb"
            Assert.Null(ratings[0].Tmdb);
        }
    }
}
