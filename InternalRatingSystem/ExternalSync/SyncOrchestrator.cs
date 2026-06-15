using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.ExternalSync
{
    /// <summary>
    /// Per-user/per-provider pull+push engine.
    /// Runs one full sync cycle: token refresh, optional import, optional export
    /// with deduplication, and final state mutation on the <see cref="ProviderConnection"/>.
    ///
    /// Never throws — all exceptions are captured into <see cref="SyncResult.Error"/>
    /// and <see cref="ProviderConnection.LastError"/> so that the caller (task or
    /// controller) can persist the error and continue with other users/providers.
    /// </summary>
    public sealed class SyncOrchestrator
    {
        private readonly IRatingGatherer _gatherer;
        private readonly IRatingSink     _sink;
        private readonly IExternalIdResolver _resolver;
        private readonly ILogger<SyncOrchestrator> _logger;

        public SyncOrchestrator(
            IRatingGatherer gatherer,
            IRatingSink sink,
            IExternalIdResolver resolver,
            ILogger<SyncOrchestrator> logger)
        {
            _gatherer = gatherer;
            _sink     = sink;
            _resolver = resolver;
            _logger   = logger;
        }

        // ------------------------------------------------------------------
        // Dedup key
        // ------------------------------------------------------------------

        /// <summary>
        /// Composite key used to identify a rating across systems.
        /// Format: <c>&lt;imdb&gt;|&lt;tmdb&gt;|&lt;mediaType&gt;</c>
        /// Empty string is used when a field is absent so the key is always
        /// well-defined and two ratings with the same IDs + media type are
        /// treated as the same content.
        /// </summary>
        private static string Key(ExternalRating r)
            => (r.Imdb ?? string.Empty)
               + "|"
               + (r.Tmdb?.ToString() ?? string.Empty)
               + "|"
               + r.MediaType;

        // ------------------------------------------------------------------
        // Main entry point
        // ------------------------------------------------------------------

        /// <summary>
        /// Runs one sync cycle for one user/provider pair.
        /// Mutates <paramref name="conn"/> state (LastSyncedAt, LastPushed,
        /// LastPulled, LastError) so the caller can persist the updated
        /// connection without needing to inspect the returned result.
        /// </summary>
        public async Task<SyncResult> SyncOneAsync(
            string userId,
            string userName,
            IExternalRatingProvider provider,
            ProviderConnection conn,
            CancellationToken ct)
        {
            var result = new SyncResult();

            // Direction == Off -> nothing to do.
            if (conn.Direction == SyncDirection.Off)
                return result;

            try
            {
                // Step 2: token refresh (mutates conn in-place; caller persists).
                await provider.EnsureTokenAsync(conn, ct).ConfigureAwait(false);

                // Step 3: pull from the remote service.
                // We pull for ALL directions so that the push step can dedup
                // against what the service already has (prevents re-spam on
                // every run for ExportOnly).
                IReadOnlyList<ExternalRating> remote =
                    await provider.PullRatingsAsync(conn, ct).ConfigureAwait(false);

                // Build a lookup by dedup key (last-wins on duplicate keys).
                var remoteByKey = new Dictionary<string, ExternalRating>(StringComparer.Ordinal);
                foreach (var r in remote)
                {
                    var k = Key(r);
                    remoteByKey[k] = r;
                }

                // Step 4: import (ImportOnly / TwoWay).
                if (conn.Direction == SyncDirection.ImportOnly ||
                    conn.Direction == SyncDirection.TwoWay)
                {
                    foreach (var r in remote)
                    {
                        ct.ThrowIfCancellationRequested();

                        var itemId = _resolver.FindItemId(r);
                        if (itemId == null)
                        {
                            result.Skipped++;
                            continue;
                        }

                        var cur = await _sink.GetUserStarsAsync(userId, itemId).ConfigureAwait(false);
                        if (cur.HasValue && cur.Value == r.Stars)
                        {
                            result.Skipped++;
                            continue;
                        }

                        await _sink.SaveRatingAsync(itemId, userId, userName, r.Stars, null, r.RatedAt)
                                   .ConfigureAwait(false);
                        result.Pulled++;
                    }
                }

                // Step 5: export (ExportOnly / TwoWay).
                if (conn.Direction == SyncDirection.ExportOnly ||
                    conn.Direction == SyncDirection.TwoWay)
                {
                    var local = await _gatherer.GatherAsync(userId).ConfigureAwait(false);

                    var toPush = local
                        .Where(l =>
                        {
                            var k = Key(l);
                            // Push only if the service doesn't have it, or has it at a different star rating.
                            return !(remoteByKey.TryGetValue(k, out var rr) && rr.Stars == l.Stars);
                        })
                        .ToList();

                    if (toPush.Count > 0)
                    {
                        result.Pushed = await provider.PushRatingsAsync(conn, toPush, ct)
                                                      .ConfigureAwait(false);
                    }
                }

                // Step 6: persist connection state.
                conn.LastSyncedAt = DateTime.UtcNow;
                conn.LastPushed   = result.Pushed;
                conn.LastPulled   = result.Pulled;
                conn.LastError    = null; // clear any previous error on success
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SyncOrchestrator: sync failed for user {UserId} provider {Provider}",
                    userId, provider.Id);
                result.Error   = ex.Message;
                conn.LastError = ex.Message;
            }

            return result;
        }
    }
}
