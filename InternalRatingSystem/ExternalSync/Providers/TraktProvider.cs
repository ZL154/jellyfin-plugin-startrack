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

    internal sealed class TraktEpisodeItem
    {
        [JsonPropertyName("title")]  public string?   Title  { get; set; }
        [JsonPropertyName("season")] public int?      Season { get; set; }
        [JsonPropertyName("number")] public int?      Number { get; set; }
        [JsonPropertyName("ids")]    public TraktIds? Ids    { get; set; }
    }

    internal sealed class TraktRatedEpisode
    {
        [JsonPropertyName("rating")]   public int    Rating   { get; set; }
        [JsonPropertyName("rated_at")] public string? RatedAt { get; set; }
        [JsonPropertyName("episode")]  public TraktEpisodeItem? Episode { get; set; }
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
    /// Movies, shows and episodes each sync via their own Trakt bucket
    /// (/sync/ratings movies[] / shows[] / episodes[]), matched by external ids.
    /// </summary>
    public sealed class TraktProvider : IExternalRatingProvider, ISupportsLibrarySync
    {
        private const string BaseUrl = "https://api.trakt.tv";

        private readonly HttpClient _http;

        // Nullable overrides: when non-null the explicit value is used (tests).
        // When null the live PluginConfiguration is read at each call site so that
        // a DI singleton built before the admin saves credentials still works correctly.
        private readonly string? _clientIdOverride;
        private readonly string? _clientSecretOverride;

        private readonly DeviceCodeOAuth _oauth;

        /// <summary>
        /// Ctor used by both production code and unit tests.
        /// When <paramref name="clientId"/> or <paramref name="clientSecret"/> are
        /// omitted (null), the live plugin configuration is consulted at each call
        /// site so that tests can still inject explicit values while production code
        /// picks up the admin-configured credentials automatically.
        /// </summary>
        public TraktProvider(HttpClient http, string? clientId = null, string? clientSecret = null)
        {
            _http                 = http ?? throw new ArgumentNullException(nameof(http));
            _clientIdOverride     = clientId;
            _clientSecretOverride = clientSecret;
            _oauth                = new DeviceCodeOAuth(http);
        }

        // Resolved per-call so a singleton sees config changes without restart.
        private string ClientId     => _clientIdOverride     ?? Plugin.Instance?.Configuration.TraktClientId     ?? string.Empty;
        private string ClientSecret => _clientSecretOverride ?? Plugin.Instance?.Configuration.TraktClientSecret ?? string.Empty;

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

            // Build body — separate movies / shows / episodes arrays. Episodes
            // MUST go in their own "episodes" array keyed by the EPISODE's ids;
            // Trakt cannot match an episode id placed in the "shows" bucket (this
            // was the bug that left episode ratings stranded in StarTrack).
            var movies   = new List<object>();
            var shows    = new List<object>();
            var episodes = new List<object>();

            foreach (var r in ratings)
            {
                var ids  = BuildIds(r);
                int traktRating = RatingScale.ToService10(r.Stars);
                string ratedAt  = r.RatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

                var item = new { ids, rating = traktRating, rated_at = ratedAt };

                if (r.MediaType == "movie")
                    movies.Add(item);
                else if (r.MediaType == "episode")
                    episodes.Add(item);
                else
                    shows.Add(item);
            }

            var body = new { movies, shows, episodes };
            var json = JsonSerializer.Serialize(body);

            using var req = BuildRequest(HttpMethod.Post, $"{BaseUrl}/sync/ratings", conn, json);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var responseJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<TraktSyncResponse>(responseJson);

            var added = dto?.Added;
            return (added?.Movies ?? 0) + (added?.Shows ?? 0) + (added?.Episodes ?? 0);
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

            // --- episodes ---
            using (var req = BuildRequest(HttpMethod.Get, $"{BaseUrl}/sync/ratings/episodes", conn, body: null))
            using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var items = JsonSerializer.Deserialize<TraktRatedEpisode[]>(json);
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        if (item.Episode == null) continue;
                        result.Add(new ExternalRating(
                            Imdb:      item.Episode.Ids?.Imdb,
                            Tmdb:      item.Episode.Ids?.Tmdb,
                            Tvdb:      item.Episode.Ids?.Tvdb,
                            Title:     item.Episode.Title ?? string.Empty,
                            Year:      null,
                            MediaType: "episode",
                            Stars:     RatingScale.FromService10(item.Rating),
                            RatedAt:   ParseRatedAt(item.RatedAt)));
                    }
                }
            }

            return result;
        }

        // ------------------------------------------------------------------ //
        // Library sync (ISupportsLibrarySync) — watched history + liked
        // ------------------------------------------------------------------ //

        /// <inheritdoc />
        /// <remarks>
        /// Marks movies/episodes as watched via <c>POST /sync/history</c>.
        /// Idempotent: pulls the already-watched set first and only posts items
        /// Trakt doesn't already have, so repeated syncs never create duplicate
        /// plays. (Shows are skipped — marking a whole show watched would mark
        /// every episode.)
        /// </remarks>
        public async Task<int> MarkWatchedAsync(ProviderConnection conn, IReadOnlyList<ExternalRating> watched, CancellationToken ct)
        {
            if (watched.Count == 0) return 0;

            var watchedMovies = await GetIdSetAsync(conn, "/sync/watched/movies", ct).ConfigureAwait(false);
            var watchedEps    = await GetIdSetAsync(conn, "/sync/history/episodes?limit=100", ct).ConfigureAwait(false);

            var movies   = new List<object>();
            var episodes = new List<object>();
            foreach (var r in watched)
            {
                var k = KeyOf(r);
                if (k == null) continue;
                var item = new { ids = BuildIds(r), watched_at = r.RatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") };
                if (r.MediaType == "movie")        { if (!watchedMovies.Contains(k)) movies.Add(item); }
                else if (r.MediaType == "episode") { if (!watchedEps.Contains(k))    episodes.Add(item); }
            }

            if (movies.Count == 0 && episodes.Count == 0) return 0;
            return await PostCountAsync(conn, "/sync/history", new { movies, episodes }, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Pushes liked items to BOTH the user's Trakt Favorites (shown on the
        /// profile) and a dedicated private "StarTrack Liked" list. Idempotent:
        /// dedups against existing favorites / list items. Returns the number
        /// newly added to Favorites.
        /// </remarks>
        public async Task<int> PushLikedAsync(ProviderConnection conn, IReadOnlyList<ExternalRating> liked, CancellationToken ct)
        {
            if (liked.Count == 0) return 0;
            int added = 0;

            // --- Favorites (movies + shows) ---
            try
            {
                var favMovies = await GetIdSetAsync(conn, "/users/me/favorites/movies", ct).ConfigureAwait(false);
                var favShows  = await GetIdSetAsync(conn, "/users/me/favorites/shows", ct).ConfigureAwait(false);

                var m = new List<object>();
                var s = new List<object>();
                foreach (var l in liked)
                {
                    var k = KeyOf(l);
                    if (k == null) continue;
                    if (l.MediaType == "movie") { if (!favMovies.Contains(k)) m.Add(new { ids = BuildIds(l) }); }
                    else                        { if (!favShows.Contains(k))  s.Add(new { ids = BuildIds(l) }); }
                }
                if (m.Count > 0 || s.Count > 0)
                    added += await PostCountAsync(conn, "/users/me/favorites", new { movies = m, shows = s }, ct).ConfigureAwait(false);
            }
            catch { /* favorites is best-effort; the list below still runs */ }

            // --- Dedicated "StarTrack Liked" list ---
            try
            {
                var listId = await EnsureListAsync(conn, "StarTrack Liked", ct).ConfigureAwait(false);
                if (listId != null)
                {
                    var inList = await GetIdSetAsync(conn, $"/users/me/lists/{listId}/items/movie", ct).ConfigureAwait(false);
                    var inListShows = await GetIdSetAsync(conn, $"/users/me/lists/{listId}/items/show", ct).ConfigureAwait(false);
                    var m = new List<object>();
                    var s = new List<object>();
                    foreach (var l in liked)
                    {
                        var k = KeyOf(l);
                        if (k == null) continue;
                        if (l.MediaType == "movie") { if (!inList.Contains(k))      m.Add(new { ids = BuildIds(l) }); }
                        else                        { if (!inListShows.Contains(k)) s.Add(new { ids = BuildIds(l) }); }
                    }
                    if (m.Count > 0 || s.Count > 0)
                        added += await PostCountAsync(conn, $"/users/me/lists/{listId}/items", new { movies = m, shows = s }, ct).ConfigureAwait(false);
                }
            }
            catch { /* list is best-effort */ }

            return added;
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
                ["client_id"]     = ClientId,
                ["client_secret"] = ClientSecret,
                ["redirect_uri"]  = "urn:ietf:wg:oauth:2.0:oob"   // Trakt device-code flow
            };

            // No bearer token needed for refresh; trakt-api-key is sufficient
            var headers = new Dictionary<string, string>
            {
                ["trakt-api-version"] = "2",
                ["trakt-api-key"]     = ClientId
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

            // Standard Trakt headers on every call — ClientId resolved per-call (lazy config)
            req.Headers.TryAddWithoutValidation("trakt-api-version", "2");
            req.Headers.TryAddWithoutValidation("trakt-api-key", ClientId);

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

        /// <summary>Stable cross-system key for dedup: IMDb if present, else "tmdb:N".</summary>
        private static string? KeyOf(ExternalRating r)
            => r.Imdb ?? (r.Tmdb.HasValue ? "tmdb:" + r.Tmdb.Value : null);

        /// <summary>
        /// GETs a Trakt array endpoint and returns the set of item keys (imdb +
        /// "tmdb:N") found in any nested entity's <c>ids</c>. Works for
        /// /sync/watched, /sync/history, favorites, and list-items shapes.
        /// Best-effort: returns an empty set on any error.
        /// </summary>
        private async Task<HashSet<string>> GetIdSetAsync(ProviderConnection conn, string path, CancellationToken ct)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var req  = BuildRequest(HttpMethod.Get, $"{BaseUrl}{path}", conn, body: null);
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return set;
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return set;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    foreach (var prop in el.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Object &&
                            prop.Value.TryGetProperty("ids", out var ids))
                        {
                            if (ids.TryGetProperty("imdb", out var im) && im.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(im.GetString()))
                                set.Add(im.GetString()!);
                            if (ids.TryGetProperty("tmdb", out var tm) && tm.ValueKind == JsonValueKind.Number)
                                set.Add("tmdb:" + tm.GetInt32());
                        }
                    }
                }
            }
            catch { /* best-effort */ }
            return set;
        }

        /// <summary>POSTs a JSON body and returns added.movies + added.shows + added.episodes.</summary>
        private async Task<int> PostCountAsync(ProviderConnection conn, string path, object body, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(body);
            using var req  = BuildRequest(HttpMethod.Post, $"{BaseUrl}{path}", conn, json);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var rj = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<TraktSyncResponse>(rj);
            var a = dto?.Added;
            return (a?.Movies ?? 0) + (a?.Shows ?? 0) + (a?.Episodes ?? 0);
        }

        /// <summary>
        /// Finds the user's list by name (case-insensitive); creates it private
        /// if absent. Returns the Trakt list id as a string, or null on failure.
        /// </summary>
        private async Task<string?> EnsureListAsync(ProviderConnection conn, string name, CancellationToken ct)
        {
            try
            {
                using (var req  = BuildRequest(HttpMethod.Get, $"{BaseUrl}/users/me/lists", conn, body: null))
                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var el in doc.RootElement.EnumerateArray())
                            {
                                if (el.TryGetProperty("name", out var n) &&
                                    string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase) &&
                                    el.TryGetProperty("ids", out var ids) && ids.TryGetProperty("trakt", out var tid))
                                    return tid.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
                            }
                        }
                    }
                }

                var createBody = JsonSerializer.Serialize(new { name, description = "Movies & shows liked in StarTrack.", privacy = "private" });
                using (var req  = BuildRequest(HttpMethod.Post, $"{BaseUrl}/users/me/lists", conn, createBody))
                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("ids", out var ids) && ids.TryGetProperty("trakt", out var tid))
                        return tid.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch { /* best-effort */ }
            return null;
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
