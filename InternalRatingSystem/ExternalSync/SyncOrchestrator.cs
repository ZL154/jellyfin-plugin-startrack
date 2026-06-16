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
        private readonly ILikedGatherer? _likedGatherer;
        private readonly ILogger<SyncOrchestrator> _logger;

        public SyncOrchestrator(
            IRatingGatherer gatherer,
            IRatingSink sink,
            IExternalIdResolver resolver,
            ILogger<SyncOrchestrator> logger,
            ILikedGatherer? likedGatherer = null)
        {
            _gatherer = gatherer;
            _sink     = sink;
            _resolver = resolver;
            _logger   = logger;
            _likedGatherer = likedGatherer;
        }

        // ------------------------------------------------------------------
        // Dedup key
        // ------------------------------------------------------------------

        /// <summary>
        /// Composite key used to identify a rating across systems.
        /// Format: <c>&lt;imdb&gt;|&lt;tmdb&gt;|&lt;mediaType&gt;|&lt;title&gt;|&lt;year&gt;</c>
        /// Title (lowercased) and year are appended so that id-less items from
        /// Yamtrack (no TMDB/IMDB) don't all collapse to the same key.
        /// </summary>
        private static string Key(ExternalRating r)
            => (r.Imdb ?? string.Empty)
               + "|"
               + (r.Tmdb?.ToString() ?? string.Empty)
               + "|"
               + r.MediaType
               + "|"
               + (r.Title?.ToLowerInvariant() ?? string.Empty)
               + "|"
               + r.Year;

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
                // We pull for ALL directions so that both the import and export
                // steps can resolve conflicts against what the service already has.
                IReadOnlyList<ExternalRating> remote =
                    await provider.PullRatingsAsync(conn, ct).ConfigureAwait(false);

                // Build a lookup by dedup key (last-wins on duplicate keys).
                var remoteByKey = new Dictionary<string, ExternalRating>(StringComparer.Ordinal);
                foreach (var r in remote)
                    remoteByKey[Key(r)] = r;

                // Gather local ratings ONCE up front (with their RatedAt) so the
                // import step can do newer-wins conflict resolution without first
                // clobbering a local edit.
                //
                // THE BUG THIS FIXES: previously import ran first and overwrote
                // local with the remote value unconditionally, THEN export ran —
                // but by then local already equalled remote, so a locally-changed
                // rating could NEVER be pushed out. Two-way sync only ever pulled.
                var local = await _gatherer.GatherAsync(userId).ConfigureAwait(false);
                var localByKey = new Dictionary<string, ExternalRating>(StringComparer.Ordinal);
                foreach (var l in local)
                    localByKey[Key(l)] = l;

                bool doImport = conn.Direction == SyncDirection.ImportOnly ||
                                conn.Direction == SyncDirection.TwoWay;
                bool doExport = conn.Direction == SyncDirection.ExportOnly ||
                                conn.Direction == SyncDirection.TwoWay;

                // Step 4: import (ImportOnly / TwoWay) — remote -> local, but never
                // clobber a local rating that is newer than the remote one (that
                // one belongs to the export step instead).
                if (doImport)
                {
                    foreach (var r in remote)
                    {
                        ct.ThrowIfCancellationRequested();

                        // Clamp: skip ratings outside the valid 0.5–5 star range.
                        if (r.Stars < 0.5 || r.Stars > 5)
                        {
                            result.Skipped++;
                            continue;
                        }

                        if (localByKey.TryGetValue(Key(r), out var loc))
                        {
                            // Already identical -> nothing to do.
                            if (loc.Stars == r.Stars)
                            {
                                result.Skipped++;
                                continue;
                            }
                            // Conflict: newer wins. Local newer-or-equal -> leave it
                            // for export to push; don't overwrite it here.
                            if (loc.RatedAt >= r.RatedAt)
                            {
                                result.Skipped++;
                                continue;
                            }
                        }

                        var itemId = _resolver.FindItemId(r);
                        if (itemId == null)
                        {
                            result.Skipped++;
                            continue;
                        }

                        await _sink.SaveRatingAsync(itemId, userId, userName, r.Stars, null, r.RatedAt)
                                   .ConfigureAwait(false);
                        result.Pulled++;
                    }
                }

                // Step 5: export (ExportOnly / TwoWay) — local -> remote when the
                // service lacks the rating, or when the local rating differs AND is
                // newer-or-equal to the remote one (newer-wins).
                if (doExport)
                {
                    var toPush = local
                        .Where(l =>
                        {
                            if (!remoteByKey.TryGetValue(Key(l), out var rr))
                                return true;              // remote doesn't have it -> push
                            if (rr.Stars == l.Stars)
                                return false;             // identical -> skip
                            return l.RatedAt >= rr.RatedAt; // differ -> push only if local is newer
                        })
                        .ToList();

                    if (toPush.Count > 0)
                    {
                        result.Pushed = await provider.PushRatingsAsync(conn, toPush, ct)
                                                      .ConfigureAwait(false);
                    }
                }

                // Step 5b: library sync (watched history + liked) for providers
                // that support it (Trakt). Runs on export-capable directions.
                // Best-effort and isolated: a failure here must not fail the
                // rating sync or block the persist below. Provider methods are
                // idempotent (they dedup against the remote), so this is safe to
                // run every cycle.
                if (doExport && provider is ISupportsLibrarySync librarySync)
                {
                    try
                    {
                        // Mark everything the user rated as watched.
                        result.Watched = await librarySync.MarkWatchedAsync(conn, local, ct).ConfigureAwait(false);

                        // Push liked items (Favorites + dedicated list).
                        if (_likedGatherer != null)
                        {
                            var liked = await _likedGatherer.GatherLikedAsync(userId).ConfigureAwait(false);
                            if (liked.Count > 0)
                                result.Liked = await librarySync.PushLikedAsync(conn, liked, ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "SyncOrchestrator: library sync (watched/liked) failed for user {UserId} provider {Provider}",
                            userId, provider.Id);
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
