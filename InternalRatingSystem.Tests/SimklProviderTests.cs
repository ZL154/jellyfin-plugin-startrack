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
    // SimklProvider tests (TASK B)
    // All tests use StubHandler (defined in DeviceCodeOAuthTests.cs, same assembly).
    // =========================================================================

    public class SimklProviderTests
    {
        // ---------- helpers --------------------------------------------------

        private static HttpClient MakeClient(Func<HttpRequestMessage, HttpResponseMessage> fn)
            => new HttpClient(new StubHandler(fn));

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
            => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

        private static ProviderConnection FreshConn(string accessToken = "simkl-tok") =>
            new ProviderConnection
            {
                Direction   = SyncDirection.TwoWay,
                AccessToken = accessToken
                // No RefreshToken or TokenExpiresAt — Simkl tokens do not expire
            };

        // =====================================================================
        // Id property
        // =====================================================================

        [Fact]
        public void Id_IsSimkl()
        {
            var provider = new SimklProvider(new HttpClient(), "cid", "csec");
            Assert.Equal(ProviderId.Simkl, provider.Id);
        }

        // =====================================================================
        // EnsureTokenAsync — must return false with NO HTTP call
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

            var provider = new SimklProvider(client, "cid", "csec");
            var conn     = FreshConn();

            var result = await provider.EnsureTokenAsync(conn, CancellationToken.None);

            Assert.False(result);
            Assert.Equal(0, callCount);   // Simkl tokens do not expire — no HTTP call ever
        }

        // =====================================================================
        // PushRatingsAsync
        // =====================================================================

        [Fact]
        public async Task PushRatingsAsync_PostsCorrectBody_AndReturnsCount()
        {
            HttpRequestMessage? captured     = null;
            string?             capturedBody = null;

            var client = MakeClient(req =>
            {
                captured     = req;
                capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("""{"added":{"movies":1}}""");
            });

            var provider = new SimklProvider(client, clientId: "mycid", clientSecret: "mysec");
            var conn     = FreshConn("mytoken");

            var rating = new ExternalRating(
                Imdb:      "tt1234567",
                Tmdb:      999,
                Tvdb:      null,
                Title:     "Test Movie",
                Year:      2022,
                MediaType: "movie",
                Stars:     4.0,
                RatedAt:   new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc));

            var count = await provider.PushRatingsAsync(conn, new[] { rating }, CancellationToken.None);

            // Returns the count we sent (Simkl response doesn't reliably echo counts)
            Assert.Equal(1, count);

            // Method and endpoint
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Post, captured!.Method);
            Assert.Contains("/sync/ratings", captured.RequestUri!.PathAndQuery);

            // Simkl-specific headers present
            Assert.True(captured.Headers.Contains("simkl-api-key"));
            Assert.True(captured.Headers.Contains("Authorization"));

            // Body correctness
            Assert.NotNull(capturedBody);
            using var doc    = JsonDocument.Parse(capturedBody!);
            var       movies = doc.RootElement.GetProperty("movies");
            Assert.Equal(1, movies.GetArrayLength());

            var m = movies[0];
            Assert.Equal("tt1234567", m.GetProperty("ids").GetProperty("imdb").GetString());
            Assert.Equal(999,         m.GetProperty("ids").GetProperty("tmdb").GetInt32());
            // 4.0 stars → RatingScale.ToService10 = 8
            Assert.Equal(8, m.GetProperty("rating").GetInt32());
        }

        [Fact]
        public async Task PushRatingsAsync_GroupsShowsIntoShowsArray()
        {
            string? capturedBody = null;

            var client = MakeClient(req =>
            {
                capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("""{"added":{"shows":1}}""");
            });

            var provider = new SimklProvider(client, "cid", "csec");
            var conn     = FreshConn();

            var rating = new ExternalRating(
                Imdb:      "tt9999999",
                Tmdb:      456,
                Tvdb:      null,
                Title:     "Test Show",
                Year:      2020,
                MediaType: "show",
                Stars:     3.0,
                RatedAt:   DateTime.UtcNow);

            var count = await provider.PushRatingsAsync(conn, new[] { rating }, CancellationToken.None);

            Assert.Equal(1, count);
            Assert.NotNull(capturedBody);
            using var doc   = JsonDocument.Parse(capturedBody!);
            var       shows = doc.RootElement.GetProperty("shows");
            Assert.Equal(1, shows.GetArrayLength());
            // 3.0 stars → rating 6
            Assert.Equal(6, shows[0].GetProperty("rating").GetInt32());
        }

        [Fact]
        public async Task PushRatingsAsync_OmitsImdbWhenNull()
        {
            string? capturedBody = null;

            var client = MakeClient(req =>
            {
                capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("""{"added":{"movies":1}}""");
            });

            var provider = new SimklProvider(client, "cid", "csec");
            var conn     = FreshConn();

            var rating = new ExternalRating(
                Imdb:      null,
                Tmdb:      123,
                Tvdb:      null,
                Title:     "No IMDB",
                Year:      2021,
                MediaType: "movie",
                Stars:     2.5,
                RatedAt:   DateTime.UtcNow);

            await provider.PushRatingsAsync(conn, new[] { rating }, CancellationToken.None);

            Assert.NotNull(capturedBody);
            using var doc = JsonDocument.Parse(capturedBody!);
            var ids = doc.RootElement.GetProperty("movies")[0].GetProperty("ids");
            Assert.False(ids.TryGetProperty("imdb", out _), "imdb should be omitted when null");
            Assert.Equal(123, ids.GetProperty("tmdb").GetInt32());
        }

        [Fact]
        public async Task PushRatingsAsync_ReturnsZero_OnEmptyList()
        {
            int callCount = 0;
            var client = MakeClient(_ =>
            {
                callCount++;
                return Json("{}");
            });

            var provider = new SimklProvider(client, "cid", "csec");
            var result   = await provider.PushRatingsAsync(FreshConn(), Array.Empty<ExternalRating>(), CancellationToken.None);

            Assert.Equal(0, callCount);   // no HTTP call when nothing to push
            Assert.Equal(0, result);
        }

        // =====================================================================
        // PullRatingsAsync
        // =====================================================================

        [Fact]
        public async Task PullRatingsAsync_ParsesMoviesAndShows()
        {
            // Simkl returns an envelope object with "movies" and "shows" arrays
            const string ratingsJson = """
            {
              "movies": [
                {
                  "rating": 8,
                  "rated_at": "2024-06-01T10:00:00.000Z",
                  "movie": {
                    "title": "Inception",
                    "year": 2010,
                    "ids": { "imdb": "tt1375666", "tmdb": 27205 }
                  }
                }
              ],
              "shows": [
                {
                  "rating": 6,
                  "rated_at": "2024-06-02T10:00:00.000Z",
                  "show": {
                    "title": "Breaking Bad",
                    "year": 2008,
                    "ids": { "imdb": "tt0903747", "tmdb": 1396 }
                  }
                }
              ]
            }
            """;

            var client   = MakeClient(_ => Json(ratingsJson));
            var provider = new SimklProvider(client, "cid", "csec");
            var ratings  = await provider.PullRatingsAsync(FreshConn(), CancellationToken.None);

            Assert.Equal(2, ratings.Count);

            var movie = ratings[0];
            Assert.Equal("movie",      movie.MediaType);
            Assert.Equal("tt1375666",  movie.Imdb);
            Assert.Equal(27205,        movie.Tmdb);
            Assert.Equal(4.0,          movie.Stars);   // rating 8 → 4.0
            Assert.Equal(2010,         movie.Year);
            Assert.Equal("Inception",  movie.Title);

            var show = ratings[1];
            Assert.Equal("show",         show.MediaType);
            Assert.Equal("tt0903747",    show.Imdb);
            Assert.Equal(1396,           show.Tmdb);
            Assert.Equal(3.0,            show.Stars);    // rating 6 → 3.0
            Assert.Equal("Breaking Bad", show.Title);
        }

        [Fact]
        public async Task PullRatingsAsync_HandlesEmptyEnvelope()
        {
            var client   = MakeClient(_ => Json("""{"movies":[],"shows":[]}"""));
            var provider = new SimklProvider(client, "cid", "csec");

            var ratings = await provider.PullRatingsAsync(FreshConn(), CancellationToken.None);

            Assert.Empty(ratings);
        }

        [Fact]
        public async Task PullRatingsAsync_ToleratesNullIds()
        {
            const string json = """
            {
              "movies": [
                {
                  "rating": 10,
                  "rated_at": "2024-01-01T00:00:00.000Z",
                  "movie": {
                    "title": "NoIds",
                    "year": 1999,
                    "ids": { "imdb": null, "tmdb": null }
                  }
                }
              ],
              "shows": []
            }
            """;

            var client   = MakeClient(_ => Json(json));
            var provider = new SimklProvider(client, "cid", "csec");
            var ratings  = await provider.PullRatingsAsync(FreshConn(), CancellationToken.None);

            Assert.Single(ratings);
            var r = ratings[0];
            Assert.Null(r.Imdb);
            Assert.Null(r.Tmdb);
            Assert.Equal(5.0, r.Stars);  // 10 → 5.0
        }
    }
}
