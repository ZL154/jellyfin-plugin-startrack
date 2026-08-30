using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Serializd
{
    /// <summary>Outcome of one Serializd write.</summary>
    public enum SerializdWriteStatus
    {
        /// <summary>Written.</summary>
        Ok,
        /// <summary>Serializd does not have this show, or the season could not be resolved.</summary>
        NotFound,
        /// <summary>Token expired or rejected.</summary>
        NeedsReauth,
        /// <summary>Anything else.</summary>
        Failed
    }

    /// <summary>Result of one write.</summary>
    public sealed record SerializdWriteResult(SerializdWriteStatus Status, string? Message = null)
    {
        /// <summary>True when the write succeeded.</summary>
        public bool Ok => Status == SerializdWriteStatus.Ok;
    }

    /// <summary>
    /// The write surface StarTrack needs from Serializd. A seam, exactly as with
    /// Letterboxd, so the push orchestrator can be tested against a fake.
    /// </summary>
    public interface ISerializdWriter
    {
        /// <summary>Rates a whole series by TMDb id.</summary>
        Task<SerializdWriteResult> RateShowAsync(int showTmdbId, double stars, string? review, CancellationToken ct = default);

        /// <summary>Rates a single episode.</summary>
        Task<SerializdWriteResult> RateEpisodeAsync(int showTmdbId, int seasonNumber, int episodeNumber, double stars, string? review, CancellationToken ct = default);
    }

    /// <summary>
    /// Writes StarTrack TV ratings to Serializd.
    ///
    /// ==================== RATING SCALE ====================
    /// Serializd rates 1–10 INTEGER, so StarTrack's 0.5–5.0 half-stars are
    /// DOUBLED here. That is the same direction as Letterboxd's /rate/ endpoint
    /// and the CSV column, but the OPPOSITE of Letterboxd's diary API, which
    /// takes 0.5–5.0 raw. Four scales now live in this plugin; getting one
    /// backwards halves or doubles a user's ratings, which is exactly how issue
    /// #19 happened. Pinned by tests.
    ///
    /// A rating of 0 means "unrated" and is REQUIRED in the payload — omitting
    /// the field entirely makes Serializd return HTTP 500.
    /// =====================================================
    ///
    /// Undocumented private API. Endpoints and payload shape transcribed from a
    /// working implementation, not guessed.
    /// </summary>
    public sealed class SerializdWriteService : ISerializdWriter
    {
        private readonly SerializdSession _session;
        private readonly ILogger _logger;

        /// <summary>showTmdbId → (seasonNumber → seasonId). Season ids cost a request to discover.</summary>
        private readonly Dictionary<int, Dictionary<int, int>> _seasonCache = new();

        public SerializdWriteService(SerializdSession session, ILogger logger)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _logger  = logger;
        }

        /// <summary>StarTrack half-stars to Serializd's 1–10 integer. 0 means unrated.</summary>
        internal static int ToSerializdScale(double stars)
        {
            if (stars <= 0) return 0;
            return (int)Math.Clamp(Math.Round(stars * 2, MidpointRounding.AwayFromZero), 1, 10);
        }

        /// <inheritdoc />
        public Task<SerializdWriteResult> RateShowAsync(int showTmdbId, double stars, string? review, CancellationToken ct = default)
        {
            var hasText = !string.IsNullOrWhiteSpace(review);

            // Serializd only persists review_text on a LOG entry. With
            // is_log:false it accepts the request, stores the rating and
            // silently discards the text — so a written review has to become a
            // log, while a bare rating stays a rating and creates no diary noise.
            var payload = BuildPayload(showTmdbId, null, null, stars, review, isLog: hasText, isRewatch: false);
            return PostReviewAsync(payload, ct);
        }

        /// <inheritdoc />
        public async Task<SerializdWriteResult> RateEpisodeAsync(
            int showTmdbId, int seasonNumber, int episodeNumber, double stars, string? review, CancellationToken ct = default)
        {
            var seasonId = await ResolveSeasonIdAsync(showTmdbId, seasonNumber, ct).ConfigureAwait(false);
            if (seasonId == null)
                return new SerializdWriteResult(SerializdWriteStatus.NotFound,
                    $"Serializd has no season {seasonNumber} for show {showTmdbId}.");

            var hasText = !string.IsNullOrWhiteSpace(review);
            var payload = BuildPayload(showTmdbId, seasonId, episodeNumber, stars, review, isLog: hasText, isRewatch: false);
            return await PostReviewAsync(payload, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// The /show/reviews/add body. snake_case throughout — this is the web
        /// app's own API, not a public one, and it is strict about the shape.
        /// </summary>
        internal static Dictionary<string, object?> BuildPayload(
            int showTmdbId, int? seasonId, int? episodeNumber,
            double stars, string? review, bool isLog, bool isRewatch)
            => new()
            {
                ["show_id"]         = showTmdbId,
                ["season_id"]       = seasonId,
                ["episode_number"]  = episodeNumber,
                ["review_text"]     = review ?? string.Empty,
                ["contains_spoiler"] = false,
                ["backdate"]        = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["is_log"]          = isLog,
                ["is_rewatch"]      = isRewatch,
                ["tags"]            = Array.Empty<string>(),
                ["allows_comments"] = true,
                ["like"]            = false,
                // REQUIRED even when unrated: omitting it returns HTTP 500.
                ["rating"]          = ToSerializdScale(stars)
            };

        private async Task<SerializdWriteResult> PostReviewAsync(Dictionary<string, object?> payload, CancellationToken ct)
        {
            if (!_session.IsAuthenticated)
                return new SerializdWriteResult(SerializdWriteStatus.NeedsReauth, "Not signed in to Serializd.");

            try
            {
                using var res = await _session
                    .PostJsonAsync("/show/reviews/add", JsonSerializer.Serialize(payload), ct)
                    .ConfigureAwait(false);

                if (res.IsSuccessStatusCode) return new SerializdWriteResult(SerializdWriteStatus.Ok);

                // The response echoes review_text back, so it is never logged:
                // that would put a user's private review draft in the log file.
                _logger.LogWarning("[StarTrack] Serializd review POST for show {Show} returned HTTP {Status}",
                    payload["show_id"], (int)res.StatusCode);

                return res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => new SerializdWriteResult(
                        SerializdWriteStatus.NeedsReauth, "Serializd session expired."),
                    HttpStatusCode.NotFound => new SerializdWriteResult(SerializdWriteStatus.NotFound),
                    _ => new SerializdWriteResult(SerializdWriteStatus.Failed,
                        $"Serializd returned HTTP {(int)res.StatusCode}.")
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StarTrack] Serializd review POST failed");
                return new SerializdWriteResult(SerializdWriteStatus.Failed, "Could not reach Serializd.");
            }
        }

        /// <summary>
        /// Maps a season NUMBER to Serializd's internal season id via
        /// <c>GET /show/{tmdbId}</c>. Cached per show for the session — every
        /// episode of a series would otherwise repeat the same lookup.
        /// </summary>
        internal async Task<int?> ResolveSeasonIdAsync(int showTmdbId, int seasonNumber, CancellationToken ct)
        {
            if (_seasonCache.TryGetValue(showTmdbId, out var cached))
                return cached.TryGetValue(seasonNumber, out var id) ? id : null;

            var map = new Dictionary<int, int>();
            try
            {
                using var res = await _session.GetAsync($"/show/{showTmdbId}", ct).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("seasons", out var seasons) &&
                        seasons.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var sn in seasons.EnumerateArray())
                        {
                            if (!sn.TryGetProperty("seasonNumber", out var num) || !num.TryGetInt32(out var n))
                                continue;

                            // The response carries the same value under both "id"
                            // and "seasonId". Reading only one would turn a future
                            // rename into a silent "season not found" for every
                            // episode rather than a visible failure.
                            if ((sn.TryGetProperty("id", out var sid) && sid.TryGetInt32(out var i)) ||
                                (sn.TryGetProperty("seasonId", out sid) && sid.TryGetInt32(out i)))
                                map[n] = i;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StarTrack] Serializd season lookup failed for show {Show}", showTmdbId);
            }

            _seasonCache[showTmdbId] = map;
            return map.TryGetValue(seasonNumber, out var found) ? found : null;
        }
    }
}
