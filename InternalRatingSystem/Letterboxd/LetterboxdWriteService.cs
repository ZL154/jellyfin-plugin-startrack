using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Letterboxd
{
    /// <summary>A resolved Letterboxd film: its URL slug and internal ids.</summary>
    public sealed record LetterboxdFilm(string Slug, string FilmId, string? ProductionId);

    /// <summary>Outcome of writing one diary entry.</summary>
    public enum LetterboxdWriteStatus
    {
        /// <summary>Written.</summary>
        Ok,
        /// <summary>The film isn't on Letterboxd, or TMDb id is missing/unmatched.</summary>
        FilmNotFound,
        /// <summary>Session expired or was rejected — caller should re-authenticate once and retry.</summary>
        NeedsReauth,
        /// <summary>Cloudflare blocked the request.</summary>
        Cloudflare,
        /// <summary>Anything else (network, unexpected status).</summary>
        Failed
    }

    /// <summary>Result of a single write.</summary>
    public sealed record LetterboxdWriteResult(LetterboxdWriteStatus Status, string? Message = null)
    {
        /// <summary>True when the entry was written.</summary>
        public bool Ok => Status == LetterboxdWriteStatus.Ok;
    }

    /// <summary>
    /// The write surface StarTrack needs from Letterboxd.
    ///
    /// Exists as a seam: today the only implementation drives letterboxd.com as
    /// a signed-in browser session, because Letterboxd's public API is closed.
    /// If they ever issue an API key, an ApiLetterboxdWriter can implement this
    /// and drop in without the push orchestrator changing at all.
    /// </summary>
    public interface ILetterboxdWriter
    {
        /// <summary>Resolves a TMDb movie id to a Letterboxd film, or null if unknown to them.</summary>
        Task<LetterboxdFilm?> ResolveFilmAsync(int tmdbId, CancellationToken ct = default);

        /// <summary>Sets the member's rating for a film in place. Idempotent.</summary>
        Task<LetterboxdWriteResult> SetRatingAsync(LetterboxdFilm film, double? stars, CancellationToken ct = default);

        /// <summary>Marks a film watched. Idempotent.</summary>
        Task<LetterboxdWriteResult> SetWatchedAsync(LetterboxdFilm film, CancellationToken ct = default);

        /// <summary>Likes a film. Idempotent. Never un-likes — see the implementation.</summary>
        Task<LetterboxdWriteResult> SetLikedAsync(LetterboxdFilm film, CancellationToken ct = default);

        /// <summary>Adds a film to the member's watchlist. Idempotent. Never removes.</summary>
        Task<LetterboxdWriteResult> AddToWatchlistAsync(LetterboxdFilm film, CancellationToken ct = default);

        /// <summary>Creates a dated diary entry. NOT idempotent — callers must dedupe.</summary>
        Task<LetterboxdWriteResult> LogEntryAsync(
            LetterboxdFilm film, DateTime watchedAt, double? rating, bool liked, bool rewatch,
            string? review = null, bool containsSpoilers = false, CancellationToken ct = default);
    }

    /// <summary>
    /// Writes StarTrack activity back to letterboxd.com through an authenticated
    /// <see cref="LetterboxdSession"/>.
    ///
    /// ==================== READ BEFORE TOUCHING RATINGS ====================
    /// Letterboxd exposes TWO write paths and they use DIFFERENT RATING SCALES.
    /// Verified against both endpoints, not assumed:
    ///
    ///   POST /s/film:{id}/rate/    rating = 0..10 INTEGER  (0 clears it)
    ///                              idempotent: sets the rating in place
    ///
    ///   POST /api/v0/log-entries   rating = 0.5..5.0 HALF-STARS
    ///                              creates a NEW diary entry on every call
    ///
    /// StarTrack's own scale is 0.5–5.0, so the log-entries path passes through
    /// unconverted while the rate path doubles. Getting these the wrong way
    /// round halves or doubles every rating a user pushes — the same class of
    /// bug as issue #19. Both directions are pinned by tests.
    ///
    /// The idempotency difference is why the push orchestrator prefers
    /// rate/watch for ongoing sync and treats diary entries as opt-in: a
    /// repeated rate is a no-op, a repeated log-entry is a duplicate in
    /// someone's diary, forever, every ten minutes.
    /// ======================================================================
    /// </summary>
    public sealed class LetterboxdWriteService : ILetterboxdWriter
    {
        private readonly LetterboxdSession _session;
        private readonly ILogger _logger;

        // Film pages expose the internal id in a few shapes depending on the
        // template version; try each rather than pinning to one.
        private static readonly Regex[] FilmIdPatterns =
        {
            new("data-film-id=\"(\\d+)\"",     RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new("data-item-id=\"(\\d+)\"",     RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new("film:(\\d+)",                 RegexOptions.IgnoreCase | RegexOptions.Compiled)
        };

        private static readonly Regex ProductionIdPattern =
            new("data-production-id=\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Letterboxd dropped data-film-slug / data-film-id in mid-2026 for a
        // data-postered-identifier JSON blob carrying BOTH ids:
        //   {"lid":"2a8i","uid":"film:51561", ...}
        // lid is the productionId (short alphanumeric), and the numeric tail of
        // uid is the legacy filmId. The diary endpoint wants the FORMER; sending
        // the latter earns "Production not found for ID: 51568".
        private static readonly Regex PosteredIdentifierPattern =
            new("data-postered-identifier=\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex LidPattern =
            new("&quot;lid&quot;\\s*:\\s*&quot;([^&]+)&quot;|\"lid\"\\s*:\\s*\"([^\"]+)\"",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SlugFromPath =
            new("/film/([a-z0-9\\-_]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public LetterboxdWriteService(LetterboxdSession session, ILogger logger)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _logger  = logger;
        }

        /// <summary>
        /// Resolves a TMDb movie id to a Letterboxd film via <c>/tmdb/{id}</c>,
        /// which redirects to the canonical film page.
        /// </summary>
        /// <returns>Null when Letterboxd has no matching film.</returns>
        public async Task<LetterboxdFilm?> ResolveFilmAsync(int tmdbId, CancellationToken ct = default)
        {
            try
            {
                using var res = await _session.Http.GetAsync($"/tmdb/{tmdbId}", ct).ConfigureAwait(false);
                if (res.StatusCode == HttpStatusCode.NotFound) return null;
                if (!res.IsSuccessStatusCode) return null;

                // AllowAutoRedirect is on, so RequestMessage.RequestUri is the
                // final film URL. Fall back to scanning the body if the
                // redirect chain didn't land somewhere obvious.
                var html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var finalPath = res.RequestMessage?.RequestUri?.AbsolutePath ?? string.Empty;

                var slugMatch = SlugFromPath.Match(finalPath);
                if (!slugMatch.Success) slugMatch = SlugFromPath.Match(html);
                if (!slugMatch.Success)
                {
                    _logger.LogDebug("[StarTrack] Letterboxd: no film slug for TMDb {Tmdb}", tmdbId);
                    return null;
                }

                var slug = slugMatch.Groups[1].Value;

                string? filmId = null;
                foreach (var p in FilmIdPatterns)
                {
                    var m = p.Match(html);
                    if (m.Success) { filmId = m.Groups[1].Value; break; }
                }

                if (filmId == null)
                {
                    _logger.LogWarning("[StarTrack] Letterboxd: found slug {Slug} for TMDb {Tmdb} but no film id — page markup may have changed.",
                        slug, tmdbId);
                    return null;
                }

                // productionId, in order of reliability:
                //   1. the x-letterboxd-identifier response header
                //   2. "lid" inside data-postered-identifier (new markup)
                //   3. the legacy data-production-id attribute
                string? productionId = null;
                if (res.Headers.TryGetValues("x-letterboxd-identifier", out var idHeader))
                {
                    foreach (var v in idHeader) { productionId = v; break; }
                }

                if (string.IsNullOrEmpty(productionId))
                {
                    var postered = PosteredIdentifierPattern.Match(html);
                    if (postered.Success)
                    {
                        var lid = LidPattern.Match(postered.Groups[1].Value);
                        if (lid.Success)
                            productionId = lid.Groups[1].Success ? lid.Groups[1].Value : lid.Groups[2].Value;
                    }
                }

                if (string.IsNullOrEmpty(productionId))
                {
                    var prod = ProductionIdPattern.Match(html);
                    if (prod.Success) productionId = prod.Groups[1].Value;
                }

                _logger.LogDebug("[StarTrack] Letterboxd TMDb {Tmdb} -> {Slug} film={FilmId} production={Prod}",
                    tmdbId, slug, filmId, productionId ?? "none");

                return new LetterboxdFilm(slug, filmId, productionId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StarTrack] Letterboxd film lookup failed for TMDb {Tmdb}", tmdbId);
                return null;
            }
        }

        /// <summary>
        /// Converts StarTrack's 0.5–5.0 half-stars to the 0–10 integer the
        /// <c>/s/film:{id}/rate/</c> endpoint expects. 0 clears the rating.
        /// </summary>
        internal static int ToRateEndpointScale(double? stars)
        {
            if (stars is not double s || s <= 0) return 0;          // 0 = remove rating
            return (int)Math.Clamp(Math.Round(s * 2, MidpointRounding.AwayFromZero), 1, 10);
        }

        /// <summary>
        /// Sets the member's rating for a film, in place. Idempotent — sending
        /// the same value again is a no-op, so this is safe on every sync tick.
        /// Pass null or 0 stars to clear the rating.
        /// </summary>
        public Task<LetterboxdWriteResult> SetRatingAsync(
            LetterboxdFilm film, double? stars, CancellationToken ct = default)
            => PostFormAsync(film, "rate", new Dictionary<string, string>
            {
                ["rating"] = ToRateEndpointScale(stars).ToString(CultureInfo.InvariantCulture)
            }, ct);

        /// <summary>Marks a film watched. Idempotent.</summary>
        public Task<LetterboxdWriteResult> SetWatchedAsync(LetterboxdFilm film, CancellationToken ct = default)
            => PostFormAsync(film, "watch", new Dictionary<string, string> { ["watched"] = "true" }, ct);

        /// <summary>
        /// Likes a film, using the same /s/film:{id}/{action}/ shape as rate and
        /// watch. Idempotent.
        ///
        /// ONLY EVER ADDS A LIKE. StarTrack does not un-like on Letterboxd when a
        /// heart is cleared locally: likes are a curated list on someone's public
        /// profile, and silently removing entries because a Jellyfin toggle
        /// changed is a destructive surprise. Adding is recoverable by the user
        /// in one click; deleting something they curated is not.
        ///
        /// Unlike rate and watch, this endpoint is inferred from the shared URL
        /// pattern rather than observed, so a 404 is treated as "this Letterboxd
        /// build does not expose it" — non-fatal, and the caller simply does not
        /// count a like it cannot prove happened.
        /// </summary>
        public Task<LetterboxdWriteResult> SetLikedAsync(LetterboxdFilm film, CancellationToken ct = default)
            => PostFormAsync(film, "like", new Dictionary<string, string> { ["liked"] = "true" }, ct);

        /// <summary>
        /// Adds a film to the member's watchlist.
        ///
        /// ONLY EVER ADDS. StarTrack does not remove films from a Letterboxd
        /// watchlist when they leave the local one — a watchlist is a personal
        /// queue, and silently deleting from it because something was watched
        /// or unstarred in Jellyfin is a destructive surprise.
        ///
        /// VERIFIED: returns 200 against a real account. Note the shape differs
        /// from rate/watch/like — it is SLUG-based under /film/, not
        /// /s/film:{id}/{action}/. Guessing variations of the /s/ form produced
        /// nothing but 404s; this URL came from mBaratta96/letterboxd_stats,
        /// which has been doing it this way for years. The body is only __csrf.
        /// </summary>
        public Task<LetterboxdWriteResult> AddToWatchlistAsync(LetterboxdFilm film, CancellationToken ct = default)
            => PostPathAsync(film, $"/film/{film.Slug}/add-to-watchlist/", ct);

        /// <summary>
        /// POSTs to an absolute site path with just the CSRF token, for the
        /// slug-based endpoints that do not follow the /s/film:{id}/ shape.
        /// </summary>
        private async Task<LetterboxdWriteResult> PostPathAsync(LetterboxdFilm film, string path, CancellationToken ct)
        {
            if (film == null) return new LetterboxdWriteResult(LetterboxdWriteStatus.FilmNotFound);
            if (!_session.IsAuthenticated)
                return new LetterboxdWriteResult(LetterboxdWriteStatus.NeedsReauth, "Not signed in to Letterboxd.");

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, path)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__csrf"] = _session.Csrf })
                };
                req.Headers.Referrer = new Uri($"{LetterboxdSession.BaseUrl}/film/{film.Slug}/");

                using var res = await _session.Http.SendAsync(req, ct).ConfigureAwait(false);
                if (res.IsSuccessStatusCode) return new LetterboxdWriteResult(LetterboxdWriteStatus.Ok);

                return res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => new LetterboxdWriteResult(
                        LetterboxdWriteStatus.NeedsReauth, "Letterboxd session expired."),
                    HttpStatusCode.Forbidden => new LetterboxdWriteResult(
                        LetterboxdWriteStatus.Cloudflare,
                        "Letterboxd returned 403 — Cloudflare, or the session/CSRF token expired."),
                    HttpStatusCode.NotFound => new LetterboxdWriteResult(LetterboxdWriteStatus.FilmNotFound),
                    _ => new LetterboxdWriteResult(LetterboxdWriteStatus.Failed,
                        $"Letterboxd returned HTTP {(int)res.StatusCode}.")
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StarTrack] Letterboxd POST {Path} failed", path);
                return new LetterboxdWriteResult(LetterboxdWriteStatus.Failed, "Could not reach Letterboxd.");
            }
        }

        /// <summary>
        /// Pulls the film page and reports every watchlist-related URL, form
        /// action and data attribute it can find.
        ///
        /// Guessing action names found nothing — the watchlist is simply not
        /// under /s/film:{id}/{action}/. Asking the page what its own button
        /// posts to is the only honest way to settle it.
        /// </summary>
        public async Task<Dictionary<string, string>> InspectWatchlistMarkupAsync(
            LetterboxdFilm film, CancellationToken ct = default)
        {
            var report = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using var res = await _session.Http.GetAsync($"/film/{film.Slug}/", ct).ConfigureAwait(false);
                var html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                report["_status"] = ((int)res.StatusCode).ToString();
                report["_length"] = html.Length.ToString();

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var n = 0;

                // Any URL-ish token mentioning watchlist, plus the surrounding
                // attribute so the shape is obvious.
                foreach (Match m in Regex.Matches(html,
                    @"[\w-]+\s*=\s*[""'][^""']*watchlist[^""']*[""']", RegexOptions.IgnoreCase))
                {
                    var v = m.Value.Trim();
                    if (v.Length > 200) v = v.Substring(0, 200);
                    if (seen.Add(v)) report["attr" + (++n)] = v;
                    if (n >= 25) break;
                }

                // Bare paths mentioning watchlist that the attribute scan missed.
                foreach (Match m in Regex.Matches(html, @"/[\w:/.-]*watchlist[\w:/.-]*", RegexOptions.IgnoreCase))
                {
                    var v = m.Value.Trim();
                    if (seen.Add(v)) report["path" + (++n)] = v;
                    if (n >= 40) break;
                }

                if (n == 0) report["_note"] = "no watchlist markup found (signed out? button may be JS-rendered)";
            }
            catch (Exception ex)
            {
                report["_error"] = ex.GetType().Name + ": " + ex.Message;
            }
            return report;
        }

        /// <summary>
        /// Tries a set of candidate action names against one film and reports the
        /// HTTP status of each.
        ///
        /// Exists because the watchlist URL is the one write StarTrack cannot
        /// copy from a working implementation — no reference project writes the
        /// Letterboxd watchlist. Rather than keep guessing a name and shipping a
        /// release to find out, this asks the site directly and reports back.
        /// Admin-triggered and read-mostly: the candidates are all idempotent
        /// "add" style actions, and adding to a watchlist is trivially undone.
        /// </summary>
        public async Task<Dictionary<string, string>> ProbeActionsAsync(
            LetterboxdFilm film, IEnumerable<string> actions, CancellationToken ct = default)
        {
            var report = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!_session.IsAuthenticated)
            {
                report["_error"] = "not signed in";
                return report;
            }

            foreach (var action in actions)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // "_pN" variants walk the production-log-entries payload
                    // forward one validation error at a time. The endpoint
                    // returns precise JSON complaints ("Unknown property at:
                    // filmId"), so the API itself is the documentation.
                    if (action.StartsWith("_p", StringComparison.Ordinal) && action.Length <= 4)
                    {
                        object probeBody = action switch
                        {
                            "_p1" => new Dictionary<string, object?>
                            {
                                ["productionId"] = film.FilmId,
                                ["diaryDetails"] = new Dictionary<string, object> { ["diaryDate"] = DateTime.Now.ToString("yyyy-MM-dd"), ["rewatch"] = false },
                                ["tags"] = Array.Empty<string>(),
                                ["like"] = false,
                                ["rating"] = 4.0
                            },
                            "_p2" => new Dictionary<string, object?>
                            {
                                ["productionId"] = "film:" + film.FilmId,
                                ["diaryDetails"] = new Dictionary<string, object> { ["diaryDate"] = DateTime.Now.ToString("yyyy-MM-dd"), ["rewatch"] = false },
                                ["tags"] = Array.Empty<string>(),
                                ["like"] = false,
                                ["rating"] = 4.0
                            },
                            "_p3" => new Dictionary<string, object?>
                            {
                                ["productionId"] = film.FilmId
                            },
                            _ => new Dictionary<string, object?>
                            {
                                ["filmId"] = film.FilmId,
                                ["diaryDetails"] = new Dictionary<string, object> { ["diaryDate"] = DateTime.Now.ToString("yyyy-MM-dd"), ["rewatch"] = false },
                                ["tags"] = Array.Empty<string>(),
                                ["like"] = false,
                                ["rating"] = 4.0
                            }
                        };

                        using var preq = new HttpRequestMessage(HttpMethod.Post, "/api/v0/production-log-entries")
                        {
                            Content = new StringContent(JsonSerializer.Serialize(probeBody), Encoding.UTF8, "application/json")
                        };
                        preq.Headers.Referrer = new Uri($"{LetterboxdSession.BaseUrl}/film/{film.Slug}/");
                        preq.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                        var pcsrf = _session.Csrf;
                        if (!string.IsNullOrEmpty(pcsrf)) preq.Headers.TryAddWithoutValidation("X-CSRF-Token", pcsrf);

                        using var pres = await _session.Http.SendAsync(preq, ct).ConfigureAwait(false);
                        var pbody = await pres.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        report[action] = ((int)pres.StatusCode) + " | " + Truncate(pbody, 110);
                        continue;
                    }

                    // "_logentries" exercises the EXACT path the real diary
                    // code takes: JSON to /api/v0/log-entries. That call has
                    // never run in anger because the toggle was always off, so
                    // this is the only way to find out what it returns.
                    if (action == "_logentries" || action == "_logentriesprod")
                    {
                        var endpoint = action == "_logentriesprod"
                            ? "/api/v0/production-log-entries"
                            : "/api/v0/log-entries";
                        var payload = BuildPayload(endpoint, film, DateTime.Now, 4.0,
                                                   liked: false, rewatch: false,
                                                   review: null, containsSpoilers: false);
                        using var jreq = new HttpRequestMessage(HttpMethod.Post, endpoint)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                        };
                        jreq.Headers.Referrer = new Uri($"{LetterboxdSession.BaseUrl}/film/{film.Slug}/");
                        jreq.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                        var jcsrf = _session.Csrf;
                        if (!string.IsNullOrEmpty(jcsrf)) jreq.Headers.TryAddWithoutValidation("X-CSRF-Token", jcsrf);

                        using var jres = await _session.Http.SendAsync(jreq, ct).ConfigureAwait(false);
                        var jbody = await jres.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        report[action] = ((int)jres.StatusCode) + " | " + Truncate(jbody, 90);
                        continue;
                    }

                    // "_diaryfull" posts the complete save-diary-entry payload.
                    // The earlier bare-__csrf probe 404d against this endpoint,
                    // which looked like a dead URL but is just what it returns
                    // when the required fields are absent.
                    if (action == "_diaryfull")
                    {
                        var diaryFields = new Dictionary<string, string>
                        {
                            ["__csrf"]           = _session.Csrf,
                            ["filmId"]           = film.FilmId,
                            ["specifiedDate"]    = "true",
                            ["viewingDateStr"]   = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            ["rating"]           = "8",
                            ["liked"]            = "false",
                            ["review"]           = string.Empty,
                            ["containsSpoilers"] = "false",
                            ["rewatch"]          = "false",
                            ["tag"]              = string.Empty
                        };
                        using var dreq = new HttpRequestMessage(HttpMethod.Post, "/s/save-diary-entry")
                        {
                            Content = new FormUrlEncodedContent(diaryFields)
                        };
                        dreq.Headers.Referrer = new Uri($"{LetterboxdSession.BaseUrl}/film/{film.Slug}/");
                        using var dres = await _session.Http.SendAsync(dreq, ct).ConfigureAwait(false);
                        var dbody = await dres.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        report[action] = ((int)dres.StatusCode) + " | " + Truncate(dbody, 90);
                        continue;
                    }

                    // An action containing "/" is a FULL PATH template, so URL
                    // shapes can be probed and not just action names. Without
                    // this, a template was pasted into the /s/film:{id}/ form
                    // and produced meaningless 404s — which is exactly how the
                    // watchlist endpoint got wrongly written off.
                    var path = action.Contains('/')
                        ? action.Replace("{id}", film.FilmId).Replace("{slug}", film.Slug)
                        : $"/s/film:{film.FilmId}/{action}/";

                    // Body matches the reference implementation exactly: ONLY
                    // __csrf, from the com.xk72.webparts.csrf cookie.
                    var fields = new Dictionary<string, string> { ["__csrf"] = _session.Csrf };

                    using var req = new HttpRequestMessage(HttpMethod.Post, path)
                    {
                        Content = new FormUrlEncodedContent(fields)
                    };
                    req.Headers.Referrer = new Uri($"{LetterboxdSession.BaseUrl}/film/{film.Slug}/");

                    using var res = await _session.Http.SendAsync(req, ct).ConfigureAwait(false);
                    var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                    // "landed" reveals redirect-following: a POST that Letterboxd
                    // 302s gets replayed as a GET by HttpClient, which then 404s
                    // and looks like a wrong URL when the URL was fine.
                    var landed = res.RequestMessage?.RequestUri?.PathAndQuery ?? "?";
                    report[action] = ((int)res.StatusCode) + " sent=" + path + " landed=" + landed
                                   + " | " + Truncate(body, 60);
                }
                catch (Exception ex)
                {
                    report[action] = "EX " + ex.GetType().Name;
                }
            }

            return report;
        }

        private static string Truncate(string? s, int n)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace("\n", " ").Replace("\r", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n);
        }

        /// <summary>
        /// Shared plumbing for the form-encoded <c>/s/film:{id}/{action}/</c>
        /// endpoints, which take the CSRF token in the body rather than a header.
        /// </summary>
        private async Task<LetterboxdWriteResult> PostFormAsync(
            LetterboxdFilm film, string action, Dictionary<string, string> fields, CancellationToken ct)
        {
            if (film == null) return new LetterboxdWriteResult(LetterboxdWriteStatus.FilmNotFound);
            if (!_session.IsAuthenticated)
                return new LetterboxdWriteResult(LetterboxdWriteStatus.NeedsReauth, "Not signed in to Letterboxd.");

            // Letterboxd's own JS reads the CSRF value out of the cookie and
            // posts it straight back as __csrf; mirror that exactly.
            fields["__csrf"] = _session.Csrf;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"/s/film:{film.FilmId}/{action}/")
                {
                    Content = new FormUrlEncodedContent(fields)
                };
                req.Headers.Referrer = new Uri($"{LetterboxdSession.BaseUrl}/film/{film.Slug}/");
                req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

                using var res = await _session.Http.SendAsync(req, ct).ConfigureAwait(false);

                if (res.IsSuccessStatusCode) return new LetterboxdWriteResult(LetterboxdWriteStatus.Ok);

                return res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => new LetterboxdWriteResult(
                        LetterboxdWriteStatus.NeedsReauth, "Letterboxd session expired."),
                    HttpStatusCode.Forbidden => new LetterboxdWriteResult(
                        LetterboxdWriteStatus.Cloudflare,
                        "Letterboxd returned 403 — Cloudflare, or the session/CSRF token expired."),
                    HttpStatusCode.NotFound => new LetterboxdWriteResult(LetterboxdWriteStatus.FilmNotFound),
                    _ => new LetterboxdWriteResult(LetterboxdWriteStatus.Failed,
                        $"Letterboxd returned HTTP {(int)res.StatusCode}.")
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StarTrack] Letterboxd {Action} failed for {Slug}", action, film.Slug);
                return new LetterboxdWriteResult(LetterboxdWriteStatus.Failed, "Could not reach Letterboxd.");
            }
        }

        /// <summary>
        /// Creates a diary log entry: the watch date, optional rating, like flag,
        /// rewatch flag and optional review, in one write.
        ///
        /// NOT IDEMPOTENT — every call adds another dated entry to the member's
        /// diary. Callers must dedupe (see LetterboxdPushLedger).
        /// </summary>
        /// <param name="film">Resolved film.</param>
        /// <param name="watchedAt">Diary date. Local date only — Letterboxd stores a day, not an instant.</param>
        /// <param name="rating">0.5–5.0 in half steps, or null to log without rating.</param>
        /// <param name="liked">Whether to mark the film liked.</param>
        /// <param name="rewatch">Whether this is a rewatch.</param>
        /// <param name="review">Optional review text.</param>
        /// <param name="containsSpoilers">Flags the review as containing spoilers.</param>
        /// <param name="ct">Cancellation.</param>
        public async Task<LetterboxdWriteResult> LogEntryAsync(
            LetterboxdFilm film,
            DateTime watchedAt,
            double? rating,
            bool liked,
            bool rewatch,
            string? review = null,
            bool containsSpoilers = false,
            CancellationToken ct = default)
        {
            if (film == null) return new LetterboxdWriteResult(LetterboxdWriteStatus.FilmNotFound);
            if (!_session.IsAuthenticated)
                return new LetterboxdWriteResult(LetterboxdWriteStatus.NeedsReauth, "Not signed in to Letterboxd.");

            // Two endpoint spellings exist depending on whether Letterboxd has a
            // "production" record for the film. Try the production form first
            // when we have that id, then fall back.
            // production-log-entries is the one that actually answers: plain
            // /api/v0/log-entries returns a 403 from the edge. Verified by
            // probing both against a real account.
            var endpoints = film.ProductionId != null
                ? new[] { "/api/v0/production-log-entries" }
                : new[] { "/api/v0/production-log-entries", "/api/v0/log-entries" };

            LetterboxdWriteResult last = new(LetterboxdWriteStatus.Failed, "No endpoint attempted.");

            foreach (var endpoint in endpoints)
            {
                ct.ThrowIfCancellationRequested();
                var payload = BuildPayload(endpoint, film, watchedAt, rating, liked, rewatch, review, containsSpoilers);

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                    };
                    req.Headers.Referrer = new Uri($"{LetterboxdSession.BaseUrl}/film/{film.Slug}/");
                    req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                    var csrf = _session.Csrf;
                    if (!string.IsNullOrEmpty(csrf)) req.Headers.TryAddWithoutValidation("X-CSRF-Token", csrf);

                    using var res = await _session.Http.SendAsync(req, ct).ConfigureAwait(false);

                    if (res.IsSuccessStatusCode)
                        return new LetterboxdWriteResult(LetterboxdWriteStatus.Ok);

                    switch (res.StatusCode)
                    {
                        case HttpStatusCode.NotFound:
                            // Wrong endpoint for this film — try the other spelling.
                            last = new LetterboxdWriteResult(LetterboxdWriteStatus.FilmNotFound);
                            continue;

                        case HttpStatusCode.Unauthorized:
                            return new LetterboxdWriteResult(LetterboxdWriteStatus.NeedsReauth,
                                "Letterboxd session expired.");

                        case HttpStatusCode.Forbidden:
                            // 403 is ambiguous: Cloudflare, or an expired CSRF.
                            // Treat as reauth-able so one retry can recover, and
                            // say so plainly rather than guessing.
                            return new LetterboxdWriteResult(LetterboxdWriteStatus.Cloudflare,
                                "Letterboxd returned 403 — Cloudflare, or the session/CSRF token expired.");

                        default:
                            last = new LetterboxdWriteResult(LetterboxdWriteStatus.Failed,
                                $"Letterboxd returned HTTP {(int)res.StatusCode}.");
                            continue;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[StarTrack] Letterboxd write failed on {Endpoint} for {Slug}", endpoint, film.Slug);
                    last = new LetterboxdWriteResult(LetterboxdWriteStatus.Failed, "Could not reach Letterboxd.");
                }
            }

            return last;
        }

        /// <summary>
        /// Builds the diary payload. Shape matches what letterboxd.com's own
        /// web client posts.
        /// </summary>
        internal static Dictionary<string, object?> BuildPayload(
            string endpoint,
            LetterboxdFilm film,
            DateTime watchedAt,
            double? rating,
            bool liked,
            bool rewatch,
            string? review,
            bool containsSpoilers)
        {
            var payload = new Dictionary<string, object?>
            {
                ["diaryDetails"] = new Dictionary<string, object>
                {
                    ["diaryDate"] = watchedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["rewatch"]   = rewatch
                },
                ["tags"] = Array.Empty<string>(),
                ["like"] = liked
            };

            // Sent on Letterboxd's own 0.5–5.0 half-star scale, unconverted.
            if (rating.HasValue)
                payload["rating"] = Math.Clamp(Math.Round(rating.Value * 2, MidpointRounding.AwayFromZero) / 2.0, 0.5, 5.0);

            if (!string.IsNullOrWhiteSpace(review))
            {
                payload["review"]           = review;
                payload["containsSpoilers"] = containsSpoilers;
            }

            if (endpoint.Contains("production", StringComparison.Ordinal) && !string.IsNullOrEmpty(film.ProductionId))
                payload["productionId"] = film.ProductionId;
            else
                payload["filmId"] = film.FilmId;

            return payload;
        }
    }
}
