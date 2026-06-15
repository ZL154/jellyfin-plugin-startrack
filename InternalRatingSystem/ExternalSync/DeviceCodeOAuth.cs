using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.InternalRating.ExternalSync
{
    // -------------------------------------------------------------------------
    // DTOs for DeviceCodeOAuth (public so TraktProvider / SimklProvider can use them)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Response from a device-code request endpoint (Trakt / Simkl style).
    /// Fields: device_code, user_code, verification_url, interval, expires_in.
    /// </summary>
    public sealed record DeviceCodeInfo(
        string DeviceCode,
        string UserCode,
        string VerificationUrl,
        int IntervalSeconds,
        int ExpiresInSeconds);

    /// <summary>
    /// Successfully obtained (or refreshed) access token.
    /// ExpiresAt is UTC, computed as UtcNow + expires_in seconds at response time.
    /// </summary>
    public sealed record TokenResult(
        string AccessToken,
        string? RefreshToken,
        DateTime ExpiresAt);

    // -------------------------------------------------------------------------
    // Internal DTO for JSON deserialization only
    // -------------------------------------------------------------------------

    internal sealed class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")]      public string? DeviceCode      { get; set; }
        [JsonPropertyName("user_code")]        public string? UserCode        { get; set; }
        [JsonPropertyName("verification_url")] public string? VerificationUrl { get; set; }
        [JsonPropertyName("interval")]         public int     IntervalSeconds { get; set; }
        [JsonPropertyName("expires_in")]       public int     ExpiresInSeconds{ get; set; }
    }

    internal sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]  public string? AccessToken  { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")]    public int     ExpiresIn    { get; set; }
    }

    // -------------------------------------------------------------------------
    // DeviceCodeOAuth — reusable device-code flow helper
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reusable device-code OAuth helper.  Trakt and Simkl both use this flow.
    /// Inject an <see cref="HttpClient"/> (with a stub handler in tests, or a
    /// real client at runtime) so callers control the transport.
    /// All endpoints and form bodies are passed in so providers can customise them.
    /// </summary>
    public sealed class DeviceCodeOAuth
    {
        private readonly HttpClient _http;

        /// <param name="http">
        /// Injected <see cref="HttpClient"/>. Tests pass one backed by a
        /// <c>StubHandler</c>; production code passes a real client.
        /// </param>
        public DeviceCodeOAuth(HttpClient http)
            => _http = http ?? throw new ArgumentNullException(nameof(http));

        // ------------------------------------------------------------------ //

        /// <summary>
        /// POST to <paramref name="codeEndpoint"/> with <paramref name="bodyForm"/>
        /// (form-url-encoded) and optional extra <paramref name="headers"/>.
        /// Returns the parsed device-code info.
        /// </summary>
        public async Task<DeviceCodeInfo> RequestCodeAsync(
            string codeEndpoint,
            IDictionary<string, string> bodyForm,
            IDictionary<string, string>? headers,
            CancellationToken ct)
        {
            using var req = BuildRequest(HttpMethod.Post, codeEndpoint, bodyForm, headers);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<DeviceCodeResponse>(json)
                      ?? throw new InvalidOperationException("Empty device-code response.");

            return new DeviceCodeInfo(
                dto.DeviceCode      ?? throw new InvalidOperationException("Missing device_code."),
                dto.UserCode        ?? throw new InvalidOperationException("Missing user_code."),
                dto.VerificationUrl ?? throw new InvalidOperationException("Missing verification_url."),
                dto.IntervalSeconds,
                dto.ExpiresInSeconds);
        }

        // ------------------------------------------------------------------ //

        /// <summary>
        /// Poll the token endpoint once.
        /// Returns <c>null</c> when still pending (HTTP 400 with body containing
        /// <c>"authorization_pending"</c>).
        /// Returns a <see cref="TokenResult"/> on success (HTTP 200).
        /// Throws <see cref="HttpRequestException"/> on any other error.
        /// </summary>
        public async Task<TokenResult?> PollOnceAsync(
            string tokenEndpoint,
            IDictionary<string, string> bodyForm,
            IDictionary<string, string>? headers,
            CancellationToken ct)
        {
            using var req = BuildRequest(HttpMethod.Post, tokenEndpoint, bodyForm, headers);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                // Tolerant check: body text must contain the "authorization_pending" substring.
                if (errBody.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase))
                    return null;

                // Any other non-success is a hard error (denied, expired, etc.)
                throw new HttpRequestException(
                    $"Device-code poll failed ({(int)resp.StatusCode}): {errBody}");
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseTokenResponse(json);
        }

        // ------------------------------------------------------------------ //

        /// <summary>
        /// POST a token-refresh request.  Throws on failure.
        /// </summary>
        public async Task<TokenResult> RefreshAsync(
            string tokenEndpoint,
            IDictionary<string, string> bodyForm,
            IDictionary<string, string>? headers,
            CancellationToken ct)
        {
            using var req = BuildRequest(HttpMethod.Post, tokenEndpoint, bodyForm, headers);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseTokenResponse(json);
        }

        // ------------------------------------------------------------------ //
        // Private helpers
        // ------------------------------------------------------------------ //

        private static HttpRequestMessage BuildRequest(
            HttpMethod method,
            string endpoint,
            IDictionary<string, string> bodyForm,
            IDictionary<string, string>? headers)
        {
            var req = new HttpRequestMessage(method, endpoint)
            {
                Content = new FormUrlEncodedContent(bodyForm)
            };

            if (headers != null)
            {
                foreach (var (k, v) in headers)
                    req.Headers.TryAddWithoutValidation(k, v);
            }

            return req;
        }

        private static TokenResult ParseTokenResponse(string json)
        {
            var dto = JsonSerializer.Deserialize<TokenResponse>(json)
                      ?? throw new InvalidOperationException("Empty token response.");

            return new TokenResult(
                dto.AccessToken  ?? throw new InvalidOperationException("Missing access_token."),
                dto.RefreshToken,
                DateTime.UtcNow.AddSeconds(dto.ExpiresIn));
        }
    }
}
