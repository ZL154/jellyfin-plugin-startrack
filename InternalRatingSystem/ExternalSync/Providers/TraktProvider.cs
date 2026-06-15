using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.InternalRating.ExternalSync.Providers
{
    // -------------------------------------------------------------------------
    // Trakt-specific JSON DTOs (internal — not part of the public API surface)
    // -------------------------------------------------------------------------

    internal sealed class TraktIds
    {
        [JsonPropertyName("imdb")]  public string? Imdb  { get; set; }
        [JsonPropertyName("tmdb")]  public int?    Tmdb  { get; set; }
        [JsonPropertyName("tvdb")]  public int?    Tvdb  { get; set; }
    }

    internal sealed class TraktMovieItem
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("year")]  public int?    Year  { get; set; }
        [JsonPropertyName("ids")]   public TraktIds? Ids { get; set; }
    }

    internal sealed class TraktShowItem
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("year")]  public int?    Year  { get; set; }
        [JsonPropertyName("ids")]   public TraktIds? Ids { get; set; }
    }

    internal sealed class TraktRatedMovie
    {
        [JsonPropertyName("rating")]   public int    Rating   { get; set; }
        [JsonPropertyName("rated_at")] public string? RatedAt { get; set; }
        [JsonPropertyName("movie")]    public TraktMovieItem? Movie { get; set; }
    }

    internal sealed class TraktRatedShow
    {
        [JsonPropertyName("rating")]   public int    Rating   { get; set; }
        [JsonPropertyName("rated_at")] public string? RatedAt { get; set; }
        [JsonPropertyName("show")]     public TraktShowItem? Show { get; set; }
    }

    internal sealed class TraktAddedCounts
    {
        [JsonPropertyName("movies")]   public int Movies   { get; set; }
        [JsonPropertyName("shows")]    public int Shows    { get; set; }
        [JsonPropertyName("episodes")] public int Episodes { get; set; }
        [JsonPropertyName("seasons")]  public int Seasons  { get; set; }
    }

    internal sealed class TraktSyncResponse
    {
        [JsonPropertyName("added")] public TraktAddedCounts? Added { get; set; }
    }

    // -------------------------------------------------------------------------
    // TraktProvider
    // -------------------------------------------------------------------------

    /// <summary>
    /// Implements <see cref="IExternalRatingProvider"/> for Trakt.tv.
    ///
    /// Construction: inject <paramref name="http"/> (stub in tests, real client at
    /// runtime) and the Trakt app's <paramref name="clientId"/> /
    /// <paramref name="clientSecret"/> so tests never need <c>Plugin.Instance</c>.
    ///
    /// NOTE (v1 simplification): episodes are pushed as show-level entries.
    /// Trakt's full episode-level push will be a later enhancement.
    /// </summary>
    public sealed class TraktProvider : IExternalRatingProvider
    {
        private const string BaseUrl = "https://api.trakt.tv";

        private readonly HttpClient _http;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly DeviceCodeOAuth _oauth;

        /// <summary>
        /// Ctor used by both production code and unit tests.
        /// Production code should resolve clientId/Secret from
        /// <c>Plugin.Instance!.Configuration.TraktClientId</c> etc. and pass them in.
        /// </summary>
        public TraktProvider(HttpClient http, string clientId, string clientSecret)
        {
            _http         = http         ?? throw new ArgumentNullException(nameof(http));
            _clientId     = clientId     ?? throw new ArgumentNullException(nameof(clientId));
            _clientSecret = clientSecret ?? throw new ArgumentNullException(nameof(clientSecret));
            _oauth        = new DeviceCodeOAuth(http);
        }

        /// <inheritdoc />
        public ProviderId Id => ProviderId.Trakt;

        // ------------------------------------------------------------------ //
        // Push
        // ------------------------------------------------------------------ //

        /// <inheritdoc />
        /// <remarks>
        /// Groups ratings by media type and posts a single <c>POST /sync/ratings</c>.
        /// Episodes are included in the <c>shows</c> bucket (v1 simplification).
        /// Returns the sum of added.movies + added.shows from Trakt's response.
        /// </remarks>
        public async Task<int> PushRatingsAsync(
            ProviderConnection conn,
            IReadOnlyList<ExternalRating> ratings,
            CancellationToken ct)
        {
            if (ratings.Count == 0)
                return 0;

            // Build body — movies array and shows array
            var movies = new List<object>();
            var shows  = new List<object>();

            foreach (var r in ratings)
            {
                var ids  = BuildIds(r);
                int traktRating = RatingScale.ToService10(r.Stars);
                string ratedAt  = r.RatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

                var item = new { ids, rating = traktRating, rated_at = ratedAt };

                // "episode" maps to shows bucket in v1 (see class-level note)
                if (r.MediaType == "movie")
                    movies.Add(item);
                else
                    shows.Add(item);
            }

            var body = new { movies, shows };
            var json = JsonSerializer.Serialize(body);

            using var req = BuildRequest(HttpMethod.Post, $"{BaseUrl}/sync/ratings", conn, json);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var responseJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<TraktSyncResponse>(responseJson);

            var added = dto?.Added;
            return (added?.Movies ?? 0) + (added?.Shows ?? 0);
        }

        // ------------------------------------------------------------------ //
        // Pull
        // ------------------------------------------------------------------ //

        /// <inheritdoc />
        /// <remarks>
        /// Fetches <c>GET /sync/ratings/movies</c> and <c>GET /sync/ratings/shows</c>
        /// and combines the results.
        /// Unknown JSON fields are tolerated (null-safe).
        /// </remarks>
        public async Task<IReadOnlyList<ExternalRating>> PullRatingsAsync(
            ProviderConnection conn,
            CancellationToken ct)
        {
            var result = new List<ExternalRating>();

            // --- movies ---
            using (var req = BuildRequest(HttpMethod.Get, $"{BaseUrl}/sync/ratings/movies", conn, body: null))
            using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var items = JsonSerializer.Deserialize<TraktRatedMovie[]>(json);
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        if (item.Movie == null) continue;
                        result.Add(new ExternalRating(
                            Imdb:      item.Movie.Ids?.Imdb,
                            Tmdb:      item.Movie.Ids?.Tmdb,
                            Tvdb:      null,
                            Title:     item.Movie.Title ?? string.Empty,
                            Year:      item.Movie.Year,
                            MediaType: "movie",
                            Stars:     RatingScale.FromService10(item.Rating),
                            RatedAt:   ParseRatedAt(item.RatedAt)));
                    }
                }
            }

            // --- shows ---
            using (var req = BuildRequest(HttpMethod.Get, $"{BaseUrl}/sync/ratings/shows", conn, body: null))
            using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var items = JsonSerializer.Deserialize<TraktRatedShow[]>(json);
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        if (item.Show == null) continue;
                        result.Add(new ExternalRating(
                            Imdb:      item.Show.Ids?.Imdb,
                            Tmdb:      item.Show.Ids?.Tmdb,
                            Tvdb:      item.Show.Ids?.Tvdb,
                            Title:     item.Show.Title ?? string.Empty,
                            Year:      item.Show.Year,
                            MediaType: "show",
                            Stars:     RatingScale.FromService10(item.Rating),
                            RatedAt:   ParseRatedAt(item.RatedAt)));
                    }
                }
            }

            return result;
        }

        // ------------------------------------------------------------------ //
        // EnsureToken
        // ------------------------------------------------------------------ //

        /// <inheritdoc />
        /// <remarks>
        /// If <see cref="ProviderConnection.TokenExpiresAt"/> is null, in the past,
        /// or within 5 minutes, AND a <see cref="ProviderConnection.RefreshToken"/>
        /// is present, performs a refresh and mutates <paramref name="conn"/> in-place.
        /// Returns <c>true</c> if the token was refreshed, <c>false</c> otherwise.
        /// </remarks>
        public async Task<bool> EnsureTokenAsync(ProviderConnection conn, CancellationToken ct)
        {
            bool needsRefresh =
                conn.TokenExpiresAt == null ||
                conn.TokenExpiresAt <= DateTime.UtcNow.AddMinutes(5);

            if (!needsRefresh)
                return false;

            if (string.IsNullOrEmpty(conn.RefreshToken))
                return false;

            var form = new Dictionary<string, string>
            {
                ["grant_type"]    = "refresh_token",
                ["refresh_token"] = conn.RefreshToken!,
                ["client_id"]     = _clientId,
                ["client_secret"] = _clientSecret,
                ["redirect_uri"]  = "urn:ietf:wg:oauth:2.0:oob"   // Trakt device-code flow
            };

            // No bearer token needed for refresh; trakt-api-key is sufficient
            var headers = new Dictionary<string, string>
            {
                ["trakt-api-version"] = "2",
                ["trakt-api-key"]     = _clientId
            };

            var token = await _oauth.RefreshAsync(
                $"{BaseUrl}/oauth/token", form, headers, ct).ConfigureAwait(false);

            conn.AccessToken    = token.AccessToken;
            conn.RefreshToken   = token.RefreshToken;
            conn.TokenExpiresAt = token.ExpiresAt;

            return true;
        }

        // ------------------------------------------------------------------ //
        // Private helpers
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Build a standard Trakt request.  For GET requests <paramref name="body"/>
        /// is null.  For POST requests it is a JSON string.
        /// </summary>
        private HttpRequestMessage BuildRequest(
            HttpMethod method,
            string url,
            ProviderConnection conn,
            string? body)
        {
            var req = new HttpRequestMessage(method, url);

            // Standard Trakt headers on every call
            req.Headers.TryAddWithoutValidation("trakt-api-version", "2");
            req.Headers.TryAddWithoutValidation("trakt-api-key", _clientId);

            if (!string.IsNullOrEmpty(conn.AccessToken))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", conn.AccessToken);

            if (body != null)
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            return req;
        }

        /// <summary>
        /// Build a Trakt <c>ids</c> object, omitting null fields so the JSON is clean.
        /// Uses <see cref="Dictionary{TKey,TValue}"/> because anonymous-type properties
        /// cannot be conditionally omitted.
        /// </summary>
        private static object BuildIds(ExternalRating r)
        {
            var ids = new Dictionary<string, object>();
            if (r.Imdb != null)      ids["imdb"] = r.Imdb;
            if (r.Tmdb.HasValue)     ids["tmdb"] = r.Tmdb.Value;
            if (r.Tvdb.HasValue)     ids["tvdb"] = r.Tvdb.Value;
            return ids;
        }

        /// <summary>
        /// Parse Trakt's <c>rated_at</c> field (ISO 8601).
        /// Tolerant: returns <see cref="DateTime.UtcNow"/> on failure so a rating
        /// is not lost due to a malformed timestamp.
        /// </summary>
        private static DateTime ParseRatedAt(string? raw)
        {
            if (string.IsNullOrEmpty(raw))
                return DateTime.UtcNow;

            // Try round-trip first (handles "Z" suffix and "+00:00" offset correctly)
            if (DateTime.TryParse(raw, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt.ToUniversalTime();

            // Fallback: assume UTC for bare date-times with no offset info
            if (DateTime.TryParse(raw, null,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt2))
                return dt2;

            return DateTime.UtcNow;
        }
    }
}
