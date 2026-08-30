using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Serializd
{
    /// <summary>Why a Serializd sign-in failed, so the UI can say something useful.</summary>
    public enum SerializdAuthStatus
    {
        /// <summary>Signed in.</summary>
        Ok,
        /// <summary>Serializd rejected the email/password.</summary>
        BadCredentials,
        /// <summary>Network error, timeout, or an unrecognised response.</summary>
        Failed
    }

    /// <summary>Outcome of a sign-in attempt.</summary>
    public sealed record SerializdAuthResult(SerializdAuthStatus Status, string? Message = null, string? Username = null)
    {
        /// <summary>True when the session can be used for writes.</summary>
        public bool Ok => Status == SerializdAuthStatus.Ok;
    }

    /// <summary>
    /// An authenticated Serializd API session.
    ///
    /// Notably easier than Letterboxd: this is a real JSON API returning a bearer
    /// token, so there is no HTML scraping, no CSRF token to lift, and — because
    /// the API host is NOT the same host as www.serializd.com — no Cloudflare
    /// challenge to work around.
    ///
    /// It is still an UNDOCUMENTED private API belonging to the web app, so it
    /// can change shape without notice. Everything is kept in this one file so a
    /// breakage has a single place to fix, and failures are typed rather than
    /// thrown so the UI can explain itself.
    /// </summary>
    public sealed class SerializdSession : IDisposable
    {
        // MUST keep the trailing slash. Uri resolution treats a base without
        // one as a file and discards the last segment, so "/api" plus "login"
        // resolves to /login and every request 404s. Paths passed in are
        // normalised below for the same reason: a LEADING slash resolves
        // against the host root and drops /api just as silently.
        internal const string BaseUrl   = "https://serializd.onrender.com/api/";
        internal const string FrontPage = "https://www.serializd.com";

        /// <summary>Value the web app sends in X-Requested-With; the API expects it.</summary>
        internal const string AppId = "serializd_vercel";

        private readonly HttpClient _http;
        private readonly ILogger _logger;
        private bool _disposed;

        /// <summary>Bearer token from the last successful login.</summary>
        private string _token = string.Empty;

        /// <summary>Username reported by the login response.</summary>
        public string? Username { get; private set; }

        /// <summary>True once <see cref="AuthenticateAsync"/> has succeeded.</summary>
        public bool IsAuthenticated => !string.IsNullOrEmpty(_token);

        public SerializdSession(ILogger logger, HttpMessageHandler? handler = null)
        {
            _logger = logger;
            _http = new HttpClient(handler ?? new HttpClientHandler())
            {
                BaseAddress = new Uri(BaseUrl),
                Timeout     = TimeSpan.FromSeconds(30)
            };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", FrontPage);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", FrontPage + "/");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", AppId);
            _http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", "StarTrack-Jellyfin/1.7 (+https://github.com/ZL154/jellyfin-plugin-startrack)");
        }

        /// <summary>Signs in and retains the bearer token. Never throws for expected failures.</summary>
        public async Task<SerializdAuthResult> AuthenticateAsync(string email, string password, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
                return new SerializdAuthResult(SerializdAuthStatus.BadCredentials,
                    "Serializd email and password are both required.");

            try
            {
                var body = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["email"]    = email,
                    ["password"] = password
                });

                using var res = await PostRawAsync("/login", body, authenticated: false, ct).ConfigureAwait(false);
                var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
                    return new SerializdAuthResult(SerializdAuthStatus.BadCredentials,
                        "Serializd rejected that email or password.");

                if (!res.IsSuccessStatusCode)
                    return new SerializdAuthResult(SerializdAuthStatus.Failed,
                        $"Unexpected response from Serializd sign-in (HTTP {(int)res.StatusCode}).");

                using var doc = JsonDocument.Parse(json);
                var token = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
                if (string.IsNullOrEmpty(token))
                    return new SerializdAuthResult(SerializdAuthStatus.Failed,
                        "Serializd sign-in succeeded but returned no token.");

                _token = token;
                Username = doc.RootElement.TryGetProperty("username", out var u) ? u.GetString() : null;
                return new SerializdAuthResult(SerializdAuthStatus.Ok, null, Username);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Deliberately excludes the exception detail from the user-facing
                // message: the login request body contains the password.
                _logger.LogError(ex, "[StarTrack] Serializd sign-in failed for {Email}", email);
                return new SerializdAuthResult(SerializdAuthStatus.Failed,
                    "Could not reach Serializd. Check the server's internet access and try again.");
            }
        }

        /// <summary>POSTs JSON to the API with the bearer token attached.</summary>
        internal Task<HttpResponseMessage> PostJsonAsync(string path, string json, CancellationToken ct = default)
            => PostRawAsync(path, json, authenticated: true, ct);

        /// <summary>GETs from the API with the bearer token attached.</summary>
        internal async Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Relative(path));
            if (!string.IsNullOrEmpty(_token)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _token);
            return await _http.SendAsync(req, ct).ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> PostRawAsync(string path, string json, bool authenticated, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Relative(path))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            if (authenticated && !string.IsNullOrEmpty(_token))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _token);

            return await _http.SendAsync(req, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Strips a leading slash so the path resolves UNDER <see cref="BaseUrl"/>
        /// rather than against the host root. Call sites keep writing "/login"
        /// because that is how the endpoints are documented everywhere else.
        /// </summary>
        private static string Relative(string path) => path.TrimStart('/');

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _http.Dispose();
        }
    }
}
