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
    /// Writes StarTrack activity back to letterboxd.com through an authenticated
    /// <see cref="LetterboxdSession"/>.
    ///
    /// SCALE NOTE: Letterboxd rates on 0.5–5.0 in half-star steps, which is
    /// exactly StarTrack's own scale, so ratings pass through unconverted. Do
    /// not "helpfully" multiply by two here — Trakt and Simkl use 1–10, this
    /// does not, and mixing them up silently rewrites people's ratings.
    /// </summary>
    public sealed class LetterboxdWriteService
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

                var prod = ProductionIdPattern.Match(html);
                return new LetterboxdFilm(slug, filmId, prod.Success ? prod.Groups[1].Value : null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StarTrack] Letterboxd film lookup failed for TMDb {Tmdb}", tmdbId);
                return null;
            }
        }

        /// <summary>
        /// Creates a diary log entry: the watch date, optional rating, like flag,
        /// rewatch flag and optional review, in one write.
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
            var endpoints = film.ProductionId != null
                ? new[] { "/api/v0/production-log-entries", "/api/v0/log-entries" }
                : new[] { "/api/v0/log-entries", "/api/v0/production-log-entries" };

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
