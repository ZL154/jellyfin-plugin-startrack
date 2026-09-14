using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Letterboxd
{
    /// <summary>
    /// What FlareSolverr handed back for one URL: the page as a real browser
    /// saw it after the challenge, plus the cookies and User-Agent that
    /// earned it. The cookie/UA pair is the valuable part — Cloudflare binds
    /// <c>cf_clearance</c> to the UA and the client IP, so a plain HttpClient
    /// on the same host can reuse it by sending both, and skip the solver for
    /// every request after the first.
    /// </summary>
    public sealed class FlareSolverrResult
    {
        public int    Status       { get; init; }
        public string Body         { get; init; } = string.Empty;
        public string UserAgent    { get; init; } = string.Empty;
        /// <summary>Cookies as a single <c>name=value; name=value</c> header value.</summary>
        public string CookieHeader { get; init; } = string.Empty;
        public bool   HasClearance { get; init; }
    }

    /// <summary>
    /// Minimal client for FlareSolverr's <c>/v1</c> API.
    ///
    /// WHY: Letterboxd fronts several pages with a Cloudflare JavaScript
    /// challenge — a page of JS that a real browser has to execute before the
    /// content is served. A .NET HttpClient cannot execute it, and no header
    /// makes it go away (measured: Chrome, Feedly, FreshRSS and Googlebot
    /// User-Agents are all challenged identically). FlareSolverr is a small
    /// self-hosted service that runs a real headless Chromium, passes the
    /// challenge, and returns the page and the resulting cookies. It is how
    /// Prowlarr, Jackett and Sonarr get through the same wall on indexers, so
    /// anyone running those already has one.
    ///
    /// Entirely optional. With no URL configured nothing here is ever called
    /// and the plugin behaves as before (back off, say so in the UI).
    /// </summary>
    public sealed class FlareSolverrClient
    {
        // A solve can take 10-30s; FlareSolverr's own default cap is 60s.
        private const int SolveTimeoutMs = 60_000;

        private readonly HttpClient _http;
        private readonly ILogger _logger;

        public FlareSolverrClient(ILogger logger, HttpClient? http = null)
        {
            _logger = logger;
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromMilliseconds(SolveTimeoutMs + 15_000) };
        }

        /// <summary>The configured base URL, normalised, or null if not configured.</summary>
        public static string? ConfiguredUrl()
        {
            var raw = Plugin.Instance?.Configuration?.FlareSolverrUrl;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = raw.Trim().TrimEnd('/');
            if (raw.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) raw = raw[..^3];
            return Uri.TryCreate(raw, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") ? raw : null;
        }

        public static bool IsConfigured => ConfiguredUrl() != null;

        /// <summary>
        /// GET <c>/</c> and look for FlareSolverr's ready banner. Used by the
        /// self-check so a wrong URL is a red row, not a silent no-op.
        /// </summary>
        public async Task<(bool Ok, string Detail)> PingAsync(CancellationToken ct = default)
        {
            var baseUrl = ConfiguredUrl();
            if (baseUrl == null) return (false, "Not configured.");
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                using var resp = await _http.GetAsync(baseUrl + "/", cts.Token).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return (false, $"HTTP {(int)resp.StatusCode} from {baseUrl}");
                using var doc = JsonDocument.Parse(body);
                var msg = doc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : null;
                var ver = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
                return msg != null && msg.Contains("ready", StringComparison.OrdinalIgnoreCase)
                    ? (true, $"FlareSolverr {ver} at {baseUrl}")
                    : (false, $"Answered, but not like FlareSolverr: {body[..Math.Min(80, body.Length)]}");
            }
            catch (Exception ex)
            {
                return (false, $"Could not reach {baseUrl}: {ex.Message}");
            }
        }

        // A tiny public "what is my IP" endpoint. Used only by the self-check,
        // only when FlareSolverr is configured, only when the admin clicks.
        private const string EchoIpUrl = "https://api.ipify.org";

        /// <summary>
        /// Do FlareSolverr and this server leave for the internet from the same
        /// IP? Cloudflare binds <c>cf_clearance</c> to the client IP, so if
        /// FlareSolverr sits behind a VPN (very common — people run it beside
        /// their download stack) the cookie it earns is useless from Jellyfin's
        /// IP. Page fetches still work, because FlareSolverr returns the page
        /// itself; the sign-in for pushing ratings does not, because that
        /// session runs from Jellyfin. Users hit this constantly and nothing
        /// tells them; this does.
        /// </summary>
        public async Task<(bool? SameIp, string Detail)> CompareEgressAsync(CancellationToken ct = default)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(45));
                var mine = (await _http.GetStringAsync(EchoIpUrl, cts.Token).ConfigureAwait(false)).Trim();
                var theirs = await GetAsync(EchoIpUrl, cts.Token).ConfigureAwait(false);
                var ip = theirs == null ? null : System.Text.RegularExpressions.Regex.Match(theirs.Body, @"\d+\.\d+\.\d+\.\d+").Value;
                if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(mine)) return (null, "Could not compare egress IPs.");
                return ip == mine
                    ? (true, $"Same public IP as this server ({mine}) — clearance cookies can be reused, and Letterboxd push can sign in.")
                    : (false, $"FlareSolverr leaves from {ip} but this server from {mine} (VPN?). Cloudflare ties its clearance to the IP, so likes/watchlist sync will work but pushing ratings to Letterboxd cannot sign in. Run FlareSolverr on the same network path as Jellyfin to enable push.");
            }
            catch (Exception ex)
            {
                return (null, "Could not compare egress IPs: " + ex.Message);
            }
        }

        /// <summary>
        /// Ask FlareSolverr to fetch <paramref name="url"/> in a real browser.
        /// Returns null on any failure (not configured, unreachable, solver
        /// error) — callers fall back to the back-off in that case, never to
        /// an exception in a scheduled task.
        /// </summary>
        public async Task<FlareSolverrResult?> GetAsync(string url, CancellationToken ct = default)
        {
            var baseUrl = ConfiguredUrl();
            if (baseUrl == null) return null;

            var payload = JsonSerializer.Serialize(new { cmd = "request.get", url, maxTimeout = SolveTimeoutMs });
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var resp = await _http.PostAsync(baseUrl + "/v1", content, ct).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return Parse(json, url, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[StarTrack] FlareSolverr request for {Url} failed: {Msg}", url, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Parse a <c>/v1</c> response. Separated so it can be tested against a
        /// captured fixture without a running solver.
        /// </summary>
        internal static FlareSolverrResult? Parse(string json, string url, ILogger? logger = null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
                if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    var message = root.TryGetProperty("message", out var msg) ? msg.GetString() : "(no message)";
                    logger?.LogWarning("[StarTrack] FlareSolverr could not solve {Url}: {Msg}", url, message);
                    return null;
                }
                if (!root.TryGetProperty("solution", out var sol)) return null;

                var cookies = new List<string>();
                var hasClearance = false;
                if (sol.TryGetProperty("cookies", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in arr.EnumerateArray())
                    {
                        var name  = c.TryGetProperty("name",  out var n) ? n.GetString() : null;
                        var value = c.TryGetProperty("value", out var v) ? v.GetString() : null;
                        if (string.IsNullOrEmpty(name) || value == null) continue;
                        cookies.Add(name + "=" + value);
                        if (name == "cf_clearance") hasClearance = true;
                    }
                }

                return new FlareSolverrResult
                {
                    Status       = sol.TryGetProperty("status", out var hs) && hs.TryGetInt32(out var code) ? code : 0,
                    Body         = sol.TryGetProperty("response",  out var b)  ? b.GetString()  ?? string.Empty : string.Empty,
                    UserAgent    = sol.TryGetProperty("userAgent", out var ua) ? ua.GetString() ?? string.Empty : string.Empty,
                    CookieHeader = string.Join("; ", cookies),
                    HasClearance = hasClearance
                };
            }
            catch (Exception ex)
            {
                logger?.LogWarning("[StarTrack] FlareSolverr returned something that is not its JSON: {Msg}", ex.Message);
                return null;
            }
        }
    }

    /// <summary>
    /// The one Cloudflare clearance the server currently holds for
    /// letterboxd.com: the User-Agent and cookies FlareSolverr earned. Shared
    /// by feed fetches and the push sign-in, because the clearance is per
    /// (IP, UA), not per feature, and solving costs 10-30 seconds. Dropped the
    /// moment anything is challenged while presenting it.
    /// </summary>
    public static class LetterboxdClearance
    {
        private static readonly object Lock = new();
        private static string? _userAgent;
        private static string? _cookieHeader;
        private static DateTime _obtainedAt;

        public static bool Has { get { lock (Lock) return _cookieHeader != null; } }

        public static (string UserAgent, string CookieHeader)? Current
        {
            get { lock (Lock) return _cookieHeader == null ? null : (_userAgent!, _cookieHeader); }
        }

        public static DateTime? ObtainedAt { get { lock (Lock) return _cookieHeader == null ? null : _obtainedAt; } }

        public static void Set(FlareSolverrResult r)
        {
            if (!r.HasClearance || string.IsNullOrEmpty(r.UserAgent)) return;
            lock (Lock) { _userAgent = r.UserAgent; _cookieHeader = r.CookieHeader; _obtainedAt = DateTime.UtcNow; }
        }

        public static void Drop() { lock (Lock) { _userAgent = null; _cookieHeader = null; } }
    }
}
