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
    // Simkl-specific JSON DTOs (internal — not part of the public API surface)
    // -------------------------------------------------------------------------

    internal sealed class SimklIds
    {
        [JsonPropertyName("imdb")]  public string? Imdb  { get; set; }
        [JsonPropertyName("tmdb")]  public int?    Tmdb  { get; set; }
        // VERIFY at smoke-test: Simkl may also return simkl/mal/anidb ids — tolerated by ignoring unknown fields
    }

    internal sealed class SimklMovieItem
    {
        [JsonPropertyName("title")] public string?   Title { get; set; }
        [JsonPropertyName("year")]  public int?      Year  { get; set; }
        [JsonPropertyName("ids")]   public SimklIds? Ids   { get; set; }
    }

    internal sealed class SimklShowItem
    {
        [JsonPropertyName("title")] public string?   Title { get; set; }
        [JsonPropertyName("year")]  public int?      Year  { get; set; }
        [JsonPropertyName("ids")]   public SimklIds? Ids   { get; set; }
    }

    internal sealed class SimklRatedMovie
    {
        [JsonPropertyName("rating")]   public int     Rating   { get; set; }
        [JsonPropertyName("rated_at")] public string? RatedAt  { get; set; }
        [JsonPropertyName("movie")]    public SimklMovieItem? Movie { get; set; }
    }

    internal sealed class SimklRatedShow
    {
        [JsonPropertyName("rating")]   public int     Rating   { get; set; }
        [JsonPropertyName("rated_at")] public string? RatedAt  { get; set; }
        [JsonPropertyName("show")]     public SimklShowItem? Show { get; set; }
    }

    /// <summary>
    /// Response envelope from GET /sync/ratings.
    /// Simkl returns a top-level object with "movies" and "shows" arrays.
    /// VERIFY at smoke-test: field names / nesting confirmed from Simkl public docs;
    /// parser is null-safe to handle any variation.
    /// </summary>
    internal sealed class SimklRatingsResponse
    {
        [JsonPropertyName("movies")] public SimklRatedMovie[]? Movies { get; set; }
        [JsonPropertyName("shows")]  public SimklRatedShow[]?  Shows  { get; set; }
    }

    // -------------------------------------------------------------------------
    // SimklProvider
    // -------------------------------------------------------------------------

    /// <summary>
    /// Implements <see cref="IExternalRatingProvider"/> for Simkl (https://simkl.com).
    ///
    /// Construction: inject <paramref name="http"/> (stub in tests, real client at
    /// runtime) and optional <paramref name="clientId"/> / <paramref name="clientSecret"/>
    /// overrides. When overrides are null the live <see cref="PluginConfiguration"/>
    /// is read at each call site (lazy config — same pattern as TraktProvider).
    ///
    /// Auth notes:
    ///   * Simkl access tokens do NOT expire and there is no refresh token.
    ///     <see cref="EnsureTokenAsync"/> is a no-op returning false.
    ///   * Authorization uses the PIN flow (GET-based), wired in ExternalSyncController.
    ///
    /// API base: https://api.simkl.com
    /// Required headers on every call:
    ///   simkl-api-key: {clientId}
    ///   Authorization: Bearer {AccessToken}
    ///   Content-Type: application/json (POST only)
    /// </summary>
    public sealed class SimklProvider : IExternalRatingProvider
    {
        private const string BaseUrl = "https://api.simkl.com";

        private readonly HttpClient _http;

        // Nullable overrides: when non-null the explicit value is used (tests).
        // When null, the live PluginConfiguration is read at each call site.
        private readonly string? _clientIdOverride;
        private readonly string? _clientSecretOverride;

        /// <param name="http">Injected HttpClient. Tests pass a stub; runtime passes a real client.</param>
        /// <param name="clientId">Optional explicit client ID (for tests). Null reads from PluginConfiguration.</param>
        /// <param name="clientSecret">Optional explicit client secret (for tests). Null reads from PluginConfiguration.</param>
        public SimklProvider(HttpClient http, string? clientId = null, string? clientSecret = null)
        {
            _http                 = http ?? throw new ArgumentNullException(nameof(http));
            _clientIdOverride     = clientId;
            _clientSecretOverride = clientSecret;
        }

        // Resolved per-call so a DI singleton built at server start sees config changes.
        private string ClientId     => _clientIdOverride     ?? Plugin.Instance?.Configuration.SimklClientId     ?? string.Empty;

        // ClientSecret not currently sent on API data calls (used in PIN OAuth in controller);
        // kept here for completeness / future refresh flows.
        private string ClientSecret => _clientSecretOverride ?? Plugin.Instance?.Configuration.SimklClientSecret ?? string.Empty;

        /// <inheritdoc />
        public ProviderId Id => ProviderId.Simkl;

        // ------------------------------------------------------------------ //
        // EnsureTokenAsync
        // ------------------------------------------------------------------ //

        /// <inheritdoc />
        /// <remarks>
        /// Simkl access tokens do NOT expire and there is no refresh flow.
        /// This method is intentionally a no-op and always returns false.
        /// No HTTP call is made.
        /// </remarks>
        public Task<bool> EnsureTokenAsync(ProviderConnection conn, CancellationToken ct)
            => Task.FromResult(false);

        // ------------------------------------------------------------------ //
        // Push
        // ------------------------------------------------------------------ //

        /// <inheritdoc />
        /// <remarks>
        /// Groups ratings into movies and shows arrays and POSTs to
        /// <c>POST https://api.simkl.com/sync/ratings</c>.
        ///
        /// Body shape:
        /// <code>
        /// {
        ///   "movies": [{ "rating": 8, "ids": { "imdb": "tt...", "tmdb": 123 } }],
        ///   "shows":  [{ "rating": 6, "ids": { "imdb": "tt...", "tmdb": 456 } }]
        /// }
        /// </code>
        ///
        /// Simkl's response envelope does not reliably echo counts, so this method
        /// returns the count of ratings sent (2xx = full success per Simkl docs).
        ///
        /// VERIFY at smoke-test: Simkl response body shape + whether episodes need
        /// separate handling beyond the "shows" bucket.
        /// </remarks>
        public async Task<int> PushRatingsAsync(
            ProviderConnection conn,
            IReadOnlyList<ExternalRating> ratings,
            CancellationToken ct)
        {
            if (ratings.Count == 0)
                return 0;

            var movies = new List<object>();
            var shows  = new List<object>();

            foreach (var r in ratings)
            {
                int simklRating = RatingScale.ToService10(r.Stars);
                var ids = BuildIds(r);
                var item = new { rating = simklRating, ids };

                // "episode" maps to shows bucket in v1 (same simplification as TraktProvider)
                if (r.MediaType == "movie")
                    movies.Add(item);
                else
                    shows.Add(item);
            }

            var body = new { movies, shows };
            var json = JsonSerializer.Serialize(body);

            using var req  = BuildRequest(HttpMethod.Post, $"{BaseUrl}/sync/ratings", conn, json);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            // Simkl's sync/ratings response varies and does not reliably echo counts.
            // Return the count we sent — all items are accepted on 2xx.
            // VERIFY at smoke-test: parse response body if Simkl adds a count field.
            return ratings.Count;
        }

        // ------------------------------------------------------------------ //
        // Pull
        // ------------------------------------------------------------------ //

        /// <inheritdoc />
        /// <remarks>
        /// Fetches <c>GET https://api.simkl.com/sync/ratings?type=movies,shows</c>
        /// and maps the response to <see cref="ExternalRating"/> records.
        ///
        /// Simkl returns a JSON envelope:
        /// <code>
        /// {
        ///   "movies": [{ "rating": 8, "rated_at": "...", "movie": { "title": "...", "year": 1999, "ids": { "imdb": "tt...", "tmdb": 123 } } }],
        ///   "shows":  [{ "rating": 6, "rated_at": "...", "show":  { "title": "...", "year": 2008, "ids": { "imdb": "tt...", "tmdb": 456 } } }]
        /// }
        /// </code>
        ///
        /// VERIFY at smoke-test: confirm whether ?type= filtering is needed or the
        /// combined endpoint always returns both movies and shows keys.
        /// Unknown JSON fields are tolerated; all parsing is null-safe.
        /// </remarks>
        public async Task<IReadOnlyList<ExternalRating>> PullRatingsAsync(
            ProviderConnection conn,
            CancellationToken ct)
        {
            var result = new List<ExternalRating>();

            using var req  = BuildRequest(HttpMethod.Get, $"{BaseUrl}/sync/ratings?type=movies,shows", conn, body: null);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            SimklRatingsResponse? envelope = null;
            try
            {
                envelope = JsonSerializer.Deserialize<SimklRatingsResponse>(json);
            }
            catch (JsonException)
            {
                // Tolerant: if the response format is unexpected, return what we have
            }

            if (envelope?.Movies != null)
            {
                foreach (var item in envelope.Movies)
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

            if (envelope?.Shows != null)
            {
                foreach (var item in envelope.Shows)
                {
                    if (item.Show == null) continue;
                    result.Add(new ExternalRating(
                        Imdb:      item.Show.Ids?.Imdb,
                        Tmdb:      item.Show.Ids?.Tmdb,
                        Tvdb:      null,  // VERIFY at smoke-test: Simkl may not expose tvdb in ratings response
                        Title:     item.Show.Title ?? string.Empty,
                        Year:      item.Show.Year,
                        MediaType: "show",
                        Stars:     RatingScale.FromService10(item.Rating),
                        RatedAt:   ParseRatedAt(item.RatedAt)));
                }
            }

            return result;
        }

        // ------------------------------------------------------------------ //
        // Private helpers
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Build a Simkl API request with the required authentication headers.
        /// GET requests pass null for <paramref name="body"/>.
        /// </summary>
        private HttpRequestMessage BuildRequest(
            HttpMethod method,
            string url,
            ProviderConnection conn,
            string? body)
        {
            var req = new HttpRequestMessage(method, url);

            // Simkl requires the client ID as a header on every API call
            req.Headers.TryAddWithoutValidation("simkl-api-key", ClientId);

            if (!string.IsNullOrEmpty(conn.AccessToken))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", conn.AccessToken);

            if (body != null)
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            return req;
        }

        /// <summary>
        /// Build a Simkl <c>ids</c> object, omitting null fields so the JSON is clean.
        /// Simkl uses imdb and tmdb for media matching.
        /// </summary>
        private static object BuildIds(ExternalRating r)
        {
            var ids = new Dictionary<string, object>();
            if (r.Imdb != null)  ids["imdb"] = r.Imdb;
            if (r.Tmdb.HasValue) ids["tmdb"] = r.Tmdb.Value;
            return ids;
        }

        /// <summary>
        /// Parse Simkl's <c>rated_at</c> field (ISO 8601 / UTC).
        /// Tolerant: returns <see cref="DateTime.UtcNow"/> on failure so a rating
        /// is not lost due to a malformed timestamp.
        /// </summary>
        private static DateTime ParseRatedAt(string? raw)
        {
            if (string.IsNullOrEmpty(raw))
                return DateTime.UtcNow;

            if (DateTime.TryParse(raw, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt.ToUniversalTime();

            if (DateTime.TryParse(raw, null,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt2))
                return dt2;

            return DateTime.UtcNow;
        }
    }
}
