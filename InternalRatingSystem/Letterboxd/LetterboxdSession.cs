using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Letterboxd
{
    /// <summary>Why a sign-in attempt failed, so the UI can say something useful.</summary>
    public enum LetterboxdAuthStatus
    {
        /// <summary>Signed in.</summary>
        Ok,
        /// <summary>Letterboxd rejected the username/password.</summary>
        BadCredentials,
        /// <summary>Cloudflare blocked the server (403 / challenge page). Needs raw cookies.</summary>
        Cloudflare,
        /// <summary>Letterboxd asked for a second factor, which StarTrack cannot complete unattended.</summary>
        TwoFactorRequired,
        /// <summary>Network error, timeout, or an unrecognised response.</summary>
        Failed
    }

    /// <summary>Outcome of an authentication attempt.</summary>
    public sealed record LetterboxdAuthResult(LetterboxdAuthStatus Status, string? Message = null)
    {
        /// <summary>True when the session is usable for writes.</summary>
        public bool Ok => Status == LetterboxdAuthStatus.Ok;
    }

    /// <summary>
    /// An authenticated letterboxd.com browser session.
    ///
    /// Letterboxd has no public write API, so pushing a rating means doing what
    /// a browser does: fetch /sign-in/, lift the hidden __csrf token, POST the
    /// credentials, and keep the resulting session cookies for subsequent
    /// writes. This class owns exactly that, and nothing about *what* gets
    /// written (see LetterboxdWriteService).
    ///
    /// Fragility is inherent and deliberate to keep contained here: if
    /// Letterboxd changes their login markup, only this file breaks, and it
    /// fails with a specific status rather than silently syncing nothing.
    /// </summary>
    public sealed class LetterboxdSession : IDisposable
    {
        internal const string BaseUrl = "https://letterboxd.com";
        private const string CsrfCookie = "com.xk72.webparts.csrf";

        // Letterboxd 403s the default .NET User-Agent on several endpoints, so a
        // real browser UA is required just to get HTML back. Kept in one place.
        internal const string DefaultUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:134.0) Gecko/20100101 Firefox/134.0";

        // Attribute order in the hidden input is not guaranteed, so match both ways round.
        private static readonly Regex CsrfInput = new(
            "<input[^>]*name=\"__csrf\"[^>]*value=\"([^\"]+)\"" +
            "|<input[^>]*value=\"([^\"]+)\"[^>]*name=\"__csrf\"",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly CookieContainer _cookies = new();
        private readonly HttpClient _http;
        private readonly ILogger _logger;
        private readonly string _userAgent;

        private bool _disposed;

        /// <summary>The signed-in Letterboxd username, once authenticated.</summary>
        public string Username { get; private set; } = string.Empty;

        /// <summary>True once <see cref="AuthenticateAsync"/> has succeeded.</summary>
        public bool IsAuthenticated { get; private set; }

        internal HttpClient Http => _http;

        /// <summary>Current CSRF token, read back from the cookie jar (Letterboxd's own JS does the same).</summary>
        internal string Csrf
        {
            get
            {
                foreach (Cookie c in _cookies.GetCookies(new Uri(BaseUrl)))
                    if (c.Name == CsrfCookie) return c.Value;
                return string.Empty;
            }
        }

        public LetterboxdSession(ILogger logger, string? userAgent = null, HttpMessageHandler? handler = null)
        {
            _logger    = logger;
            _userAgent = string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent!.Trim();

            // Tests inject a handler; production builds one with our cookie jar.
            var h = handler ?? new HttpClientHandler
            {
                CookieContainer = _cookies,
                UseCookies      = true,
                AllowAutoRedirect = true
            };

            _http = new HttpClient(h)
            {
                BaseAddress = new Uri(BaseUrl),
                Timeout     = TimeSpan.FromSeconds(30)
            };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", _userAgent);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        }

        /// <summary>
        /// Seeds cookies copied from a real browser. Needed only when Cloudflare
        /// challenges the server; the important one is <c>cf_clearance</c>.
        /// </summary>
        /// <remarks>
        /// cf_clearance is pinned by Cloudflare to BOTH the User-Agent and the
        /// IP that solved the challenge, and typically expires in ~30 minutes.
        /// Cookies pasted from a browser on a different machine, or without the
        /// matching UA, will still be rejected — that is Cloudflare's behaviour,
        /// not a bug here.
        /// </remarks>
        public void SeedRawCookies(string? rawCookieHeader)
        {
            if (string.IsNullOrWhiteSpace(rawCookieHeader)) return;

            var uri = new Uri(BaseUrl);
            foreach (var part in rawCookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2) continue;
                var name  = kv[0].Trim();
                var value = kv[1].Trim();
                if (name.Length == 0) continue;
                try { _cookies.Add(uri, new Cookie(name, value, "/", ".letterboxd.com")); }
                catch (CookieException) { /* skip malformed pairs rather than failing the whole paste */ }
            }
        }

        /// <summary>
        /// Signs in and retains the session cookies.
        /// Never throws for expected failure modes — returns a status instead.
        /// </summary>
        public async Task<LetterboxdAuthResult> AuthenticateAsync(
            string username, string password, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
                return new LetterboxdAuthResult(LetterboxdAuthStatus.BadCredentials,
                    "Letterboxd username and password are both required to push ratings.");

            try
            {
                var csrf = await BootstrapCsrfAsync(ct).ConfigureAwait(false);
                if (csrf == null)
                    return new LetterboxdAuthResult(LetterboxdAuthStatus.Cloudflare,
                        "Could not reach the Letterboxd sign-in page (Cloudflare or a markup change). " +
                        "Paste raw browser cookies including cf_clearance, with the matching User-Agent.");

                using var req = new HttpRequestMessage(HttpMethod.Post, "/user/login.do")
                {
                    Content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("__csrf",   csrf),
                        new KeyValuePair<string, string>("username", username),
                        new KeyValuePair<string, string>("password", password),
                        new KeyValuePair<string, string>("remember", "true")
                    })
                };
                req.Headers.Referrer = new Uri(BaseUrl + "/sign-in/");
                req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

                using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (res.StatusCode == HttpStatusCode.Forbidden)
                    return new LetterboxdAuthResult(LetterboxdAuthStatus.Cloudflare,
                        "Letterboxd returned 403 on sign-in — almost always Cloudflare. " +
                        "Paste raw browser cookies including cf_clearance.");

                // The login endpoint answers with a small JSON blob.
                if (body.Contains("\"result\":\"success\"", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("\"result\": \"success\"", StringComparison.OrdinalIgnoreCase))
                {
                    Username        = username;
                    IsAuthenticated = true;
                    return new LetterboxdAuthResult(LetterboxdAuthStatus.Ok);
                }

                if (body.Contains("two", StringComparison.OrdinalIgnoreCase) &&
                    body.Contains("factor", StringComparison.OrdinalIgnoreCase))
                    return new LetterboxdAuthResult(LetterboxdAuthStatus.TwoFactorRequired,
                        "This Letterboxd account has two-factor authentication enabled. " +
                        "StarTrack cannot complete that unattended, so write-back is not possible for it.");

                if (body.Contains("incorrect", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("credentials", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("\"result\":\"error\"", StringComparison.OrdinalIgnoreCase))
                    return new LetterboxdAuthResult(LetterboxdAuthStatus.BadCredentials,
                        "Letterboxd rejected that username or password.");

                _logger.LogWarning("[StarTrack] Letterboxd sign-in returned an unrecognised response ({Status}).",
                    (int)res.StatusCode);
                return new LetterboxdAuthResult(LetterboxdAuthStatus.Failed,
                    $"Unexpected response from Letterboxd sign-in (HTTP {(int)res.StatusCode}).");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Deliberately does not include the exception detail in the
                // user-facing message — it can contain the request body.
                _logger.LogError(ex, "[StarTrack] Letterboxd sign-in failed for {User}", username);
                return new LetterboxdAuthResult(LetterboxdAuthStatus.Failed,
                    "Could not reach Letterboxd. Check the server's internet access and try again.");
            }
        }

        /// <summary>
        /// GETs /sign-in/ and lifts the hidden __csrf token, which also seeds the
        /// session + CSRF cookies. Returns null when the page can't be read.
        /// </summary>
        private async Task<string?> BootstrapCsrfAsync(CancellationToken ct)
        {
            using var res = await _http.GetAsync("/sign-in/", ct).ConfigureAwait(false);
            if (res.StatusCode == HttpStatusCode.Forbidden) return null;
            if (!res.IsSuccessStatusCode) return null;

            var html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var m = CsrfInput.Match(html);
            if (m.Success)
            {
                var token = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!string.IsNullOrEmpty(token)) return token;
            }

            // Fall back to the cookie: Letterboxd's own JS reads the CSRF value
            // from there and posts it back, so it's equally valid.
            var fromCookie = Csrf;
            if (!string.IsNullOrEmpty(fromCookie)) return fromCookie;

            _logger.LogWarning("[StarTrack] No __csrf token found on the Letterboxd sign-in page.");
            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _http.Dispose();
        }
    }
}
