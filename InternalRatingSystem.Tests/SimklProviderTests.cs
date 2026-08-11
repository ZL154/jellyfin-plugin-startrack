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

        // ---------------------------------------------------------------------
        // The payload below is transcribed VERBATIM from Simkl's published
        // apiary spec ("Get Ratings", /sync/ratings/{type}/{rating}) — not
        // invented from what our own push code sends.
        //
        // Issue #19: the previous fixtures used "rating"/"rated_at" and a
        // numeric tmdb, which is the shape of our POST *request* body, not
        // Simkl's GET *response*. The tests passed against the wrong contract
        // while every real pull returned nothing. Keep this fixture honest.
        // ---------------------------------------------------------------------
        private const string RealSimklRatingsJson = """
        {
          "shows": [
            {
              "last_watched_at": "2016-09-12T13:00:30Z",
              "user_rated_at": "2021-06-23T13:19:05Z",
              "user_rating": 5,
              "status": "dropped",
              "last_watched": null,
              "next_to_watch": "S01E01",
              "show": {
                "title": "The Last Ship",
                "year": 2014,
                "ids": { "simkl": 42040, "imdb": "tt2402207", "tvdb": "269533" }
              }
            }
          ],
          "anime": [
            {
              "last_watched_at": "2014-11-06T22:05:52Z",
              "user_rated_at": "2021-06-23T13:19:05Z",
              "user_rating": 10,
              "status": "completed",
              "show": {
                "title": "Hunter x Hunter",
                "year": 2011,
                "ids": { "simkl": 40398, "imdb": "tt2098220", "mal": "11061", "anidb": "8550" }
              }
            }
          ],
          "movies": [
            {
              "last_watched_at": "2014-08-16T18:45:20Z",
              "user_rated_at": "2021-06-23T13:19:05Z",
              "user_rating": 6,
              "status": "completed",
              "movie": {
                "title": "Maleficent",
                "year": 2014,
                "ids": { "simkl": 195258, "imdb": "tt1587310", "tmdb": "102651" }
              }
            }
          ]
        }
        """;

        [Fact]
        public async Task PullRatingsAsync_ParsesRealSimklPayload()
        {
            var client   = MakeClient(_ => Json(RealSimklRatingsJson));
            var provider = new SimklProvider(client, "cid", "csec");
            var ratings  = await provider.PullRatingsAsync(FreshConn(), CancellationToken.None);

            // 1 movie + 1 show + 1 anime — anime used to be dropped entirely.
            Assert.Equal(3, ratings.Count);

            var movie = Assert.Single(ratings, r => r.MediaType == "movie");
            Assert.Equal("Maleficent", movie.Title);
            Assert.Equal("tt1587310",  movie.Imdb);
            Assert.Equal(102651,       movie.Tmdb);          // arrives as the STRING "102651"
            Assert.Equal(3.0,          movie.Stars);         // user_rating 6 -> 3.0
            Assert.Equal(2014,         movie.Year);
            Assert.Equal(new DateTime(2021, 6, 23, 13, 19, 5, DateTimeKind.Utc), movie.RatedAt);

            var show = Assert.Single(ratings, r => r.Title == "The Last Ship");
            Assert.Equal("show",    show.MediaType);
            Assert.Equal(269533,    show.Tvdb);              // tvdb was previously discarded
            Assert.Equal(2.5,       show.Stars);             // user_rating 5 -> 2.5

            var anime = Assert.Single(ratings, r => r.Title == "Hunter x Hunter");
            Assert.Equal("show", anime.MediaType);
            Assert.Equal(5.0,    anime.Stars);               // user_rating 10 -> 5.0
        }

        [Fact]
        public async Task PullRatingsAsync_SkipsWatchedItemsWithNoRating()
        {
            // Simkl returns watched-but-unrated items in the same buckets.
            // Clamping a missing score would import the whole watch history
            // at 0.5 stars, silently overwriting real ratings.
            const string json = """
            {
              "movies": [
                {
                  "last_watched_at": "2024-01-01T00:00:00Z",
                  "status": "completed",
                  "movie": { "title": "Watched Not Rated", "year": 2020, "ids": { "imdb": "tt1" } }
                },
                {
                  "user_rating": 8,
                  "user_rated_at": "2024-02-01T00:00:00Z",
                  "movie": { "title": "Rated", "year": 2021, "ids": { "imdb": "tt2" } }
                }
              ]
            }
            """;

            var client   = MakeClient(_ => Json(json));
            var provider = new SimklProvider(client, "cid", "csec");
            var ratings  = await provider.PullRatingsAsync(FreshConn(), CancellationToken.None);

            var only = Assert.Single(ratings);
            Assert.Equal("Rated", only.Title);
            Assert.Equal(4.0,     only.Stars);
        }

        [Fact]
        public async Task PullRatingsAsync_FallsBackToPostStyleFieldNames()
        {
            // Defensive: if Simkl ever returns the POST-style naming, take it
            // rather than treating every item as unrated.
            const string json = """
            {
              "movies": [
                {
                  "rating": 8,
                  "rated_at": "2024-06-01T10:00:00.000Z",
                  "movie": { "title": "Inception", "year": 2010, "ids": { "imdb": "tt1375666", "tmdb": 27205 } }
                }
              ]
            }
            """;

            var client   = MakeClient(_ => Json(json));
            var provider = new SimklProvider(client, "cid", "csec");
            var ratings  = await provider.PullRatingsAsync(FreshConn(), CancellationToken.None);

            var r = Assert.Single(ratings);
            Assert.Equal(4.0,   r.Stars);
            Assert.Equal(27205, r.Tmdb);   // numeric tmdb still accepted
        }

        [Fact]
        public async Task PullRatingsAsync_HandlesEmptyEnvelope()
        {
            var client   = MakeClient(_ => Json("""{"movies":[],"shows":[],"anime":[]}"""));
            var provider = new SimklProvider(client, "cid", "csec");

            var ratings = await provider.PullRatingsAsync(FreshConn(), CancellationToken.None);

            Assert.Empty(ratings);
        }

        [Fact]
        public async Task PullRatingsAsync_ThrowsRatherThanReportingZeroOnGarbage()
        {
            // A parse failure must NOT look like "user has no ratings" — that
            // is precisely what hid issue #19 for months.
            var client   = MakeClient(_ => Json("""{"movies": "not-an-array"}"""));
            var provider = new SimklProvider(client, "cid", "csec");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.PullRatingsAsync(FreshConn(), CancellationToken.None));
            Assert.Contains("could not parse", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task PullRatingsAsync_ToleratesNullIds()
        {
            const string json = """
            {
              "movies": [
                {
                  "user_rating": 10,
                  "user_rated_at": "2024-01-01T00:00:00.000Z",
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
