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
    // Yamtrack-specific JSON DTOs (internal — not part of the public API surface)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Nested media info inside a Yamtrack list item.
    /// VERIFY at smoke-test: exact field names may differ across Yamtrack versions.
    /// </summary>
    internal sealed class YamtrackMediaInfo
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("year")]  public int?    Year  { get; set; }
    }

    /// <summary>
    /// Single item from GET /api/v1/media/{type}/.
    /// VERIFY at smoke-test: field names, nullable behaviour, and whether tmdb id
    /// is in media_id (int) or a nested ids object.
    /// </summary>
    internal sealed class YamtrackListItem
    {
        [JsonPropertyName("media_id")] public int?    MediaId { get; set; }
        [JsonPropertyName("source")]   public string? Source  { get; set; }
        // VERIFY at smoke-test: score field name (may be "score", "rating", or nested)
        [JsonPropertyName("score")]    public int?    Score   { get; set; }
        // VERIFY at smoke-test: title may be at item level or nested under "media"
        [JsonPropertyName("media")]    public YamtrackMediaInfo? Media { get; set; }
    }

    // -------------------------------------------------------------------------
    // YamtrackProvider
    // -------------------------------------------------------------------------

    /// <summary>
    /// Implements <see cref="IExternalRatingProvider"/> for Yamtrack
    /// (self-hosted Django REST Framework media tracker).
    ///
    /// Auth: header <c>Authorization: Token {conn.ApiToken}</c> (DRF default).
    /// Base URL: <c>conn.BaseUrl</c> (trim trailing slash).
    ///
    /// Media type map: "movie" → "movie", "show"/"episode" → "tv".
    /// ID strategy: use <c>source=tmdb</c> with <c>media_id = Tmdb</c>.
    ///   Ratings with no Tmdb id are skipped (no reliable mapping to Yamtrack).
    ///
    /// Score scale: ASSUME 0–10 (StarTrack's 0.5–5 half-stars × 2 via RatingScale).
    /// VERIFY at smoke-test: actual Yamtrack score scale + exact API field names.
    ///
    /// NOTE: Episode-level push is not implemented in v1 (episodes map to "tv" type).
    /// </summary>
    public sealed class YamtrackProvider : IExternalRatingProvider
    {
        private readonly HttpClient _http;

        /// <param name="http">Injected HttpClient. Tests pass a stub; runtime passes a real client.</param>
        public YamtrackProvider(HttpClient http)
            => _http = http ?? throw new ArgumentNullException(nameof(http));

        /// <inheritdoc />
        public ProviderId Id => ProviderId.Yamtrack;

        // ------------------------------------------------------------------ //
        // EnsureTokenAsync
        // ------------------------------------------------------------------ //

        /// <inheritdoc />
        /// <remarks>
        /// Yamtrack uses a static API token (DRF Token auth). No refresh flow exists.
        /// Always returns false — no HTTP call is made.
        /// </remarks>
        public Task<bool> EnsureTokenAsync(ProviderConnection conn, CancellationToken ct)
            => Task.FromResult(false);

        // ------------------------------------------------------------------ //
        // Push
        // ------------------------------------------------------------------ //

        /// <inheritdoc />
        /// <remarks>
        /// For each rating with a Tmdb id:
        ///   <c>POST {base}/api/v1/media/{yamtrackType}/</c>
        ///   body: <c>{ "media_id": &lt;tmdb&gt;, "source": "tmdb", "score": &lt;0-10&gt; }</c>
        ///
        /// This upserts the entry and sets the score in Yamtrack.
        /// Ratings without a Tmdb id are skipped (no reliable id mapping).
        /// HTTP 200, 201, and any other 2xx are treated as success.
        ///
        /// VERIFY at smoke-test: whether a separate PATCH is needed to set the score
        /// after initial creation (current approach sends score in the POST body).
        /// </remarks>
        public async Task<int> PushRatingsAsync(
            ProviderConnection conn,
            IReadOnlyList<ExternalRating> ratings,
            CancellationToken ct)
        {
            if (ratings.Count == 0)
                return 0;

            var baseUrl  = conn.BaseUrl?.TrimEnd('/');
            var apiToken = conn.ApiToken;

            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiToken))
                return 0;

            int successCount = 0;

            foreach (var r in ratings)
            {
                // Skip ratings with no Tmdb id — cannot map to Yamtrack without it
                if (!r.Tmdb.HasValue)
                    continue;

                var yamtrackType = ToYamtrackType(r.MediaType);
                // VERIFY at smoke-test: score scale — assuming 0-10 (StarTrack half-stars × 2)
                int score = RatingScale.ToService10(r.Stars);

                var body = new
                {
                    media_id = r.Tmdb.Value,
                    source   = "tmdb",
                    score
                };
                var json = JsonSerializer.Serialize(body);

                string postUrl = $"{baseUrl}/api/v1/media/{yamtrackType}/";

                using var req  = BuildRequest(HttpMethod.Post, postUrl, apiToken, json);
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                    successCount++;
                // Non-2xx: silently skip individual failures to be tolerant
                // VERIFY at smoke-test: whether error responses need special handling
            }

            return successCount;
        }

        // ------------------------------------------------------------------ //
        // Pull
        // ------------------------------------------------------------------ //

        /// <inheritdoc />
        /// <remarks>
        /// Fetches <c>GET {base}/api/v1/media/movie/</c> and
        /// <c>GET {base}/api/v1/media/tv/</c>.
        /// Each returns an array of items; items with a null/zero score are skipped.
        /// Items with source=tmdb populate the Tmdb field; others have Tmdb=null.
        ///
        /// VERIFY at smoke-test: exact field names, whether pagination exists,
        /// and whether title is at item level or nested under "media".
        /// RatedAt defaults to UtcNow — Yamtrack list API does not expose rated_at.
        /// </remarks>
        public async Task<IReadOnlyList<ExternalRating>> PullRatingsAsync(
            ProviderConnection conn,
            CancellationToken ct)
        {
            var result   = new List<ExternalRating>();
            var baseUrl  = conn.BaseUrl?.TrimEnd('/');
            var apiToken = conn.ApiToken;

            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiToken))
                return result;

            result.AddRange(await FetchTypeAsync(baseUrl, apiToken, "movie", "movie", ct).ConfigureAwait(false));
            result.AddRange(await FetchTypeAsync(baseUrl, apiToken, "tv",    "show",  ct).ConfigureAwait(false));

            return result;
        }

        // ------------------------------------------------------------------ //
        // Private helpers
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Fetch and map ratings for a single Yamtrack media type.
        /// </summary>
        private async Task<List<ExternalRating>> FetchTypeAsync(
            string baseUrl,
            string apiToken,
            string yamtrackType,   // "movie" or "tv"
            string mediaType,      // "movie" or "show"
            CancellationToken ct)
        {
            var list = new List<ExternalRating>();
            string url = $"{baseUrl}/api/v1/media/{yamtrackType}/";

            using var req  = BuildRequest(HttpMethod.Get, url, apiToken, body: null);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            YamtrackListItem[]? items = null;
            try
            {
                items = JsonSerializer.Deserialize<YamtrackListItem[]>(json);
            }
            catch (JsonException)
            {
                // Tolerant: unexpected response shape — return empty for this type
                return list;
            }

            if (items == null) return list;

            foreach (var item in items)
            {
                // Skip unrated items (null or zero score)
                if (!item.Score.HasValue || item.Score.Value <= 0)
                    continue;

                // Resolve Tmdb id only when source is tmdb
                int? tmdb = string.Equals(item.Source, "tmdb", StringComparison.OrdinalIgnoreCase)
                    ? item.MediaId
                    : null;

                // VERIFY at smoke-test: title may be at item level or nested under "media"
                string title = item.Media?.Title ?? string.Empty;
                int?   year  = item.Media?.Year;

                list.Add(new ExternalRating(
                    Imdb:      null,               // Yamtrack list API does not expose imdb ids
                    Tmdb:      tmdb,
                    Tvdb:      null,
                    Title:     title,
                    Year:      year,
                    MediaType: mediaType,
                    Stars:     RatingScale.FromService10(item.Score.Value),
                    RatedAt:   DateTime.UtcNow));  // VERIFY at smoke-test: Yamtrack may not expose rated_at
            }

            return list;
        }

        /// <summary>
        /// Map StarTrack media type to Yamtrack API path segment.
        /// "movie" → "movie", everything else (show/episode) → "tv".
        /// VERIFY at smoke-test: confirm "tv" is the correct Yamtrack path segment for shows.
        /// </summary>
        private static string ToYamtrackType(string mediaType) =>
            mediaType == "movie" ? "movie" : "tv";

        /// <summary>
        /// Build a Yamtrack request with DRF Token auth header.
        /// GET requests pass null for <paramref name="body"/>.
        /// </summary>
        private static HttpRequestMessage BuildRequest(
            HttpMethod method,
            string url,
            string apiToken,
            string? body)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Token", apiToken);

            if (body != null)
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            return req;
        }
    }
}
