using System;
using System.Collections.Generic;
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
    // TraktProvider tests (Tasks 12 + 13)
    // All tests use StubHandler (defined in DeviceCodeOAuthTests.cs, same assembly).
    // =========================================================================

    public class TraktProviderTests
    {
        // ---------- helpers --------------------------------------------------

        private static HttpClient MakeClient(Func<HttpRequestMessage, HttpResponseMessage> fn)
            => new HttpClient(new StubHandler(fn));

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
            => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

        private static ProviderConnection FreshConn(string accessToken = "tok") =>
            new ProviderConnection
            {
                Direction    = SyncDirection.TwoWay,
                AccessToken  = accessToken,
                RefreshToken = "ref",
                TokenExpiresAt = DateTime.UtcNow.AddHours(2)   // not expired
            };

        // =====================================================================
        // TASK 12 — PushRatingsAsync
        // =====================================================================

        [Fact]
        public async Task PushRatingsAsync_PostsCorrectBody_AndReturnsAddedCount()
        {
            HttpRequestMessage? captured = null;
            string? capturedBody = null;

            var client = MakeClient(req =>
            {
                captured = req;
                capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("""{"added":{"movies":1,"shows":0,"episodes":0,"seasons":0}}""");
            });

            var provider = new TraktProvider(client, clientId: "mycid", clientSecret: "mysecret");
            var conn = FreshConn("mytoken");

            var rating = new ExternalRating(
                Imdb: "tt1234567",
                Tmdb: 999,
                Tvdb: null,
                Title: "Test Movie",
                Year: 2022,
                MediaType: "movie",
                Stars: 4.0,
                RatedAt: new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc));

            var count = await provider.PushRatingsAsync(conn, new[] { rating }, CancellationToken.None);

            // Return value
            Assert.Equal(1, count);

            // Endpoint
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Post, captured!.Method);
            Assert.Contains("/sync/ratings", captured.RequestUri!.PathAndQuery);

            // Standard Trakt headers
            Assert.True(captured.Headers.Contains("trakt-api-version"));
            Assert.True(captured.Headers.Contains("trakt-api-key"));
            Assert.True(captured.Headers.Contains("Authorization"));

            // Body
            Assert.NotNull(capturedBody);
            using var doc = JsonDocument.Parse(capturedBody!);
            var movies = doc.RootElement.GetProperty("movies");
            Assert.Equal(1, movies.GetArrayLength());

            var m = movies[0];
            Assert.Equal("tt1234567", m.GetProperty("ids").GetProperty("imdb").GetString());
            Assert.Equal(999, m.GetProperty("ids").GetProperty("tmdb").GetInt32());
            // 4.0 stars → rating 8
            Assert.Equal(8, m.GetProperty("rating").GetInt32());
            Assert.Equal("2024-01-15T12:00:00Z",
                m.GetProperty("rated_at").GetString(),
                StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task PushRatingsAsync_GroupsShowsIntoShowsArray()
        {
            string? capturedBody = null;

            var client = MakeClient(req =>
            {
                capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("""{"added":{"movies":0,"shows":1,"episodes":0,"seasons":0}}""");
            });

            var provider = new TraktProvider(client, "cid", "csec");
            var conn = FreshConn();

            var rating = new ExternalRating(
                Imdb: "tt9999999",
                Tmdb: 456,
                Tvdb: 789,
                Title: "Test Show",
                Year: 2020,
                MediaType: "show",
                Stars: 3.0,
                RatedAt: DateTime.UtcNow);

            var count = await provider.PushRatingsAsync(conn, new[] { rating }, CancellationToken.None);

            Assert.Equal(1, count);
            Assert.NotNull(capturedBody);
            using var doc = JsonDocument.Parse(capturedBody!);
            var shows = doc.RootElement.GetProperty("shows");
            Assert.Equal(1, shows.GetArrayLength());
            // 3.0 stars → rating 6
            Assert.Equal(6, shows[0].GetProperty("rating").GetInt32());
        }

        // REGRESSION: episodes must go in their own "episodes" array (keyed by the
        // episode's ids), NOT the "shows" bucket — otherwise Trakt can't match them
        // and episode ratings never leave StarTrack.
        [Fact]
        public async Task PushRatingsAsync_GroupsEpisodesIntoEpisodesArray()
        {
            string? capturedBody = null;

            var client = MakeClient(req =>
            {
                capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("""{"added":{"movies":0,"shows":0,"episodes":1,"seasons":0}}""");
            });

            var provider = new TraktProvider(client, "cid", "csec");
            var conn = FreshConn();

            var rating = new ExternalRating(
                Imdb: "tt13842130",
                Tmdb: null,
                Tvdb: null,
                Title: "The Set Up",
                Year: null,
                MediaType: "episode",
                Stars: 4.0,
                RatedAt: DateTime.UtcNow);

            var count = await provider.PushRatingsAsync(conn, new[] { rating }, CancellationToken.None);

            Assert.Equal(1, count);
            Assert.NotNull(capturedBody);
            using var doc = JsonDocument.Parse(capturedBody!);
            // Episode is in "episodes", and NOT in "shows".
            var episodes = doc.RootElement.GetProperty("episodes");
            Assert.Equal(1, episodes.GetArrayLength());
            Assert.Equal("tt13842130", episodes[0].GetProperty("ids").GetProperty("imdb").GetString());
            Assert.Equal(8, episodes[0].GetProperty("rating").GetInt32()); // 4.0 stars → 8/10
            Assert.Equal(0, doc.RootElement.GetProperty("shows").GetArrayLength());
        }

        [Fact]
        public async Task PushRatingsAsync_SkipsImdbWhenNull()
        {
            string? capturedBody = null;

            var client = MakeClient(req =>
            {
                capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("""{"added":{"movies":1,"shows":0,"episodes":0,"seasons":0}}""");
            });

            var provider = new TraktProvider(client, "cid", "csec");
            var conn = FreshConn();

            var rating = new ExternalRating(
                Imdb: null,
                Tmdb: 123,
                Tvdb: null,
                Title: "No IMDB",
                Year: 2021,
                MediaType: "movie",
                Stars: 2.5,
                RatedAt: DateTime.UtcNow);

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
                return Json("""{"added":{"movies":0,"shows":0}}""");
            });

            var provider = new TraktProvider(client, "cid", "csec");
            var conn = FreshConn();

            var result = await provider.PushRatingsAsync(conn, Array.Empty<ExternalRating>(), CancellationToken.None);

            // No HTTP call should be made when there's nothing to push
            Assert.Equal(0, callCount);
            Assert.Equal(0, result);
        }

        // =====================================================================
        // TASK 13a — PullRatingsAsync
        // =====================================================================

        [Fact]
        public async Task PullRatingsAsync_MapsMoviesAndShows()
        {
            const string moviesJson = """
            [
              {
                "rating": 8,
                "rated_at": "2024-06-01T10:00:00.000Z",
                "movie": {
                  "title": "Inception",
                  "year": 2010,
                  "ids": { "imdb": "tt1375666", "tmdb": 27205, "trakt": 16662 }
                }
              }
            ]
            """;
            const string showsJson = """
            [
              {
                "rating": 6,
                "rated_at": "2024-06-02T10:00:00.000Z",
                "show": {
                  "title": "Breaking Bad",
                  "year": 2008,
                  "ids": { "imdb": "tt0903747", "tmdb": 1396, "tvdb": 81189 }
                }
              }
            ]
            """;

            var client = MakeClient(req =>
            {
                if (req.RequestUri!.PathAndQuery.Contains("/movies"))
                    return Json(moviesJson);
                if (req.RequestUri.PathAndQuery.Contains("/shows"))
                    return Json(showsJson);
                return Json("[]");
            });

            var provider = new TraktProvider(client, "cid", "csec");
            var conn = FreshConn();

            var ratings = await provider.PullRatingsAsync(conn, CancellationToken.None);

            Assert.Equal(2, ratings.Count);

            var movie = ratings[0];
            Assert.Equal("movie", movie.MediaType);
            Assert.Equal("tt1375666", movie.Imdb);
            Assert.Equal(27205, movie.Tmdb);
            Assert.Equal(4.0, movie.Stars);   // 8 → 4.0
            Assert.Equal(2010, movie.Year);
            Assert.Equal("Inception", movie.Title);

            var show = ratings[1];
            Assert.Equal("show", show.MediaType);
            Assert.Equal("tt0903747", show.Imdb);
            Assert.Equal(1396, show.Tmdb);
            Assert.Equal(81189, show.Tvdb);
            Assert.Equal(3.0, show.Stars);    // 6 → 3.0
        }

        [Fact]
        public async Task PullRatingsAsync_HandlesEmptyArrays()
        {
            var client = MakeClient(_ => Json("[]"));
            var provider = new TraktProvider(client, "cid", "csec");

            var ratings = await provider.PullRatingsAsync(FreshConn(), CancellationToken.None);

            Assert.Empty(ratings);
        }

        [Fact]
        public async Task PullRatingsAsync_ToleratesNullIds()
        {
            // Trakt can omit tmdb/tvdb — parser must be null-safe
            const string moviesJson = """
            [
              {
                "rating": 10,
                "rated_at": "2024-01-01T00:00:00.000Z",
                "movie": {
                  "title": "NoIds",
                  "year": 1999,
                  "ids": { "imdb": null, "tmdb": null }
                }
              }
            ]
            """;

            var client = MakeClient(req =>
                req.RequestUri!.PathAndQuery.Contains("/movies")
                    ? Json(moviesJson)
                    : Json("[]"));

            var provider = new TraktProvider(client, "cid", "csec");
            var rating = (await provider.PullRatingsAsync(FreshConn(), CancellationToken.None))[0];

            Assert.Null(rating.Imdb);
            Assert.Null(rating.Tmdb);
            Assert.Equal(5.0, rating.Stars);  // 10 → 5.0
        }

        // =====================================================================
        // TASK 13b — EnsureTokenAsync
        // =====================================================================

        [Fact]
        public async Task EnsureTokenAsync_ReturnsFalse_WhenTokenNotExpired()
        {
            int callCount = 0;
            var client = MakeClient(_ => { callCount++; return Json("{}"); });

            var provider = new TraktProvider(client, "cid", "csec");
            var conn = new ProviderConnection
            {
                AccessToken    = "valid",
                RefreshToken   = "ref",
                TokenExpiresAt = DateTime.UtcNow.AddHours(1)   // far future
            };

            var result = await provider.EnsureTokenAsync(conn, CancellationToken.None);

            Assert.False(result);
            Assert.Equal(0, callCount);   // no HTTP call
        }

        [Fact]
        public async Task EnsureTokenAsync_RefreshesToken_WhenExpired()
        {
            const string refreshJson = """
            {
                "access_token": "refreshed-access",
                "refresh_token": "refreshed-refresh",
                "expires_in": 7776000
            }
            """;

            string? capturedForm = null;
            var client = MakeClient(req =>
            {
                capturedForm = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json(refreshJson);
            });

            var provider = new TraktProvider(client, "mycid", "mysecret");
            var conn = new ProviderConnection
            {
                AccessToken    = "old",
                RefreshToken   = "old-refresh",
                TokenExpiresAt = DateTime.UtcNow.AddMinutes(-1)  // expired
            };

            var result = await provider.EnsureTokenAsync(conn, CancellationToken.None);

            Assert.True(result);
            Assert.Equal("refreshed-access", conn.AccessToken);
            Assert.Equal("refreshed-refresh", conn.RefreshToken);
            Assert.True(conn.TokenExpiresAt > DateTime.UtcNow.AddDays(1));

            // Verify refresh form included required fields
            Assert.NotNull(capturedForm);
            Assert.Contains("grant_type=refresh_token", capturedForm);
            Assert.Contains("client_id=mycid", capturedForm);
            Assert.Contains("client_secret=mysecret", capturedForm);
            Assert.Contains("refresh_token=old-refresh", capturedForm);
        }

        [Fact]
        public async Task EnsureTokenAsync_RefreshesToken_WhenNearlyExpired()
        {
            // Within 5 minutes counts as expired
            const string refreshJson = """
            {"access_token":"new","refresh_token":"newref","expires_in":7776000}
            """;

            var client = MakeClient(_ => Json(refreshJson));

            var provider = new TraktProvider(client, "cid", "csec");
            var conn = new ProviderConnection
            {
                AccessToken    = "old",
                RefreshToken   = "ref",
                TokenExpiresAt = DateTime.UtcNow.AddMinutes(3)  // within 5-minute window
            };

            var result = await provider.EnsureTokenAsync(conn, CancellationToken.None);

            Assert.True(result);
            Assert.Equal("new", conn.AccessToken);
        }

        [Fact]
        public async Task EnsureTokenAsync_ReturnsFalse_WhenNoRefreshToken()
        {
            int callCount = 0;
            var client = MakeClient(_ => { callCount++; return Json("{}"); });

            var provider = new TraktProvider(client, "cid", "csec");
            var conn = new ProviderConnection
            {
                AccessToken    = "valid",
                RefreshToken   = null,                              // no refresh token
                TokenExpiresAt = DateTime.UtcNow.AddMinutes(-10)   // expired, but no refresh
            };

            var result = await provider.EnsureTokenAsync(conn, CancellationToken.None);

            Assert.False(result);
            Assert.Equal(0, callCount);
        }

        // =====================================================================
        // Id property
        // =====================================================================

        [Fact]
        public void Id_IsTrakt()
        {
            var provider = new TraktProvider(new HttpClient(), "cid", "csec");
            Assert.Equal(ProviderId.Trakt, provider.Id);
        }
    }
}
