using System;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Serializd
{
    /// <summary>What a Serializd push run did.</summary>
    public sealed class SerializdPushResult
    {
        /// <summary>Series ratings written.</summary>
        [JsonPropertyName("series")]       public int Series { get; set; }

        /// <summary>Episode ratings written.</summary>
        [JsonPropertyName("episodes")]     public int Episodes { get; set; }

        /// <summary>Skipped because nothing changed since the last push.</summary>
        [JsonPropertyName("unchanged")]    public int Unchanged { get; set; }

        /// <summary>Serializd does not have the show, or there was no TMDb id to match on.</summary>
        [JsonPropertyName("unmatched")]    public int Unmatched { get; set; }

        /// <summary>Left for the next run because this one hit its cap.</summary>
        [JsonPropertyName("remaining")]    public int Remaining { get; set; }

        /// <summary>Fatal error, if the run stopped early.</summary>
        [JsonPropertyName("error")]        public string? Error { get; set; }

        /// <summary>Total successful writes.</summary>
        [JsonPropertyName("totalWritten")] public int TotalWritten => Series + Episodes;
    }

    /// <summary>
    /// Pushes StarTrack TV ratings to Serializd.
    ///
    /// TV ONLY, by design. Serializd does not do films and Letterboxd does not do
    /// television, so between them a whole library can be mirrored while each
    /// side is handed only what it can accept. Movies are dropped in the
    /// gatherer, not here, so they never reach a writer that would mis-file them.
    ///
    /// Reuses the Letterboxd push ledger under its own key namespace. The
    /// reasoning is identical — re-pushing an unchanged library every hour is
    /// correct and abusive — and a second cache would be a second thing to keep
    /// honest.
    /// </summary>
    public sealed class SerializdPushService
    {
        /// <summary>Ledger key suffix, so Serializd state cannot collide with Letterboxd's.</summary>
        private const string LedgerSuffix = ":szd";

        private readonly ISerializdGatherer _gatherer;
        private readonly LetterboxdPushLedger _ledger;
        private readonly ILogger<SerializdPushService> _logger;

        public SerializdPushService(
            ISerializdGatherer gatherer,
            LetterboxdPushLedger ledger,
            ILogger<SerializdPushService> logger)
        {
            _gatherer = gatherer;
            _ledger   = ledger;
            _logger   = logger;
        }

        /// <summary>
        /// Runs one push cycle. Never throws; failures land in
        /// <see cref="SerializdPushResult.Error"/>.
        /// </summary>
        /// <param name="maxItems">Per-run cap, same reasoning as the Letterboxd push.</param>
        /// <param name="delayMs">Pace between networked writes.</param>
        public async Task<SerializdPushResult> PushAsync(
            string userId,
            ISerializdWriter writer,
            SerializdUserSettings settings,
            CancellationToken ct = default,
            int maxItems = 200,
            int delayMs = 250)
        {
            var result = new SerializdPushResult();
            if (settings.Direction != SerializdDirection.ExportOnly) return result;

            try
            {
                var all = await _gatherer.GatherAsync(userId).ConfigureAwait(false);

                var work = all.Where(r => r.IsEpisode ? settings.PushEpisodes : settings.PushSeries).ToList();

                var touched = 0;
                var key = userId + LedgerSuffix;

                foreach (var item in work)
                {
                    ct.ThrowIfCancellationRequested();

                    if (touched >= maxItems) { result.Remaining++; continue; }

                    // An episode and its series share a TMDb id, so the ledger key
                    // has to separate them or rating one would mask the other.
                    var ledgerId = item.IsEpisode
                        ? unchecked(item.TmdbId * 1_000_000 + item.SeasonNumber!.Value * 1_000 + item.EpisodeNumber!.Value)
                        : item.TmdbId;

                    var signature = LetterboxdPushLedger.Signature(
                        item.Stars,
                        item.Review != null,
                        settings.PushSeries, settings.PushEpisodes, settings.PushReviews);

                    if (await _ledger.IsUnchangedAsync(key, ledgerId, signature).ConfigureAwait(false))
                    {
                        result.Unchanged++;
                        continue;
                    }

                    var review = settings.PushReviews ? item.Review : null;

                    var w = item.IsEpisode
                        ? await writer.RateEpisodeAsync(item.TmdbId, item.SeasonNumber!.Value, item.EpisodeNumber!.Value,
                                                        item.Stars, review, ct).ConfigureAwait(false)
                        : await writer.RateShowAsync(item.TmdbId, item.Stars, review, ct).ConfigureAwait(false);

                    // An expired token kills the whole run; carrying on would
                    // issue one doomed request per rating in the library.
                    if (w.Status == SerializdWriteStatus.NeedsReauth)
                    {
                        result.Error = w.Message ?? w.Status.ToString();
                        return result;
                    }

                    if (w.Ok)
                    {
                        // Recorded only after Serializd confirms, so a failure is
                        // retried next run instead of being marked done.
                        await _ledger.SetStateAsync(key, ledgerId, signature).ConfigureAwait(false);
                        if (item.IsEpisode) result.Episodes++; else result.Series++;
                    }
                    else if (w.Status == SerializdWriteStatus.NotFound)
                    {
                        result.Unmatched++;
                    }

                    touched++;
                    if (delayMs > 0) await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] Serializd push failed for user {UserId}", userId);
                result.Error = ex.Message;
            }

            return result;
        }
    }
}
