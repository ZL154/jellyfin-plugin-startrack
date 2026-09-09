using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Letterboxd
{
    /// <summary>
    /// Scheduled task: every hour, iterate all users who have EnableAutoSync
    /// turned on, fetch their Letterboxd RSS feed, and import any new ratings.
    /// </summary>
    public sealed class LetterboxdSyncTask : IScheduledTask
    {
        private readonly LetterboxdSyncService _syncService;
        private readonly LetterboxdSettingsRepository _settingsRepo;
        private readonly IUserManager _userManager;
        private readonly ILogger<LetterboxdSyncTask> _logger;

        public LetterboxdSyncTask(
            LetterboxdSyncService syncService,
            LetterboxdSettingsRepository settingsRepo,
            IUserManager userManager,
            ILogger<LetterboxdSyncTask> logger)
        {
            _syncService   = syncService;
            _settingsRepo  = settingsRepo;
            _userManager   = userManager;
            _logger        = logger;
        }

        public string Name => "StarTrack Letterboxd Sync";
        public string Description =>
            "Polls each user's Letterboxd RSS every 10 minutes and only imports when the feed has actually changed (uses HTTP 304 Not Modified to stay cheap). Also retries ratings that had no library match, hourly, when that option is enabled.";
        public string Category => "StarTrack";
        public string Key => "StarTrackLetterboxdSync";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            // 10-minute interval is cheap because the conditional GET
            // (If-None-Match / If-Modified-Since) returns 304 Not Modified
            // when nothing has changed on Letterboxd's side. Worst-case lag
            // between a Letterboxd post and StarTrack picking it up is 10
            // minutes — close to "automatic" without webhooks.
            new TaskTriggerInfo
            {
                Type          = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(10).Ticks
            }
        };

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var all = await _settingsRepo.GetAllAsync().ConfigureAwait(false);
            if (all.Count == 0)
            {
                progress.Report(100);
                return;
            }

            // [#25] One library fingerprint for the whole tick, taken before the
            // loop. Computing it per user would both repeat the query and, worse,
            // let the first user with a backlog record the new value so everyone
            // after them saw an "unchanged" library and skipped their retry.
            var fingerprint = _syncService.GetLibraryFingerprint();
            var libraryChanged = fingerprint == null ||
                                 !string.Equals(fingerprint, _lastLibraryFingerprint, StringComparison.Ordinal);
            var retriedThisTick = false;

            var i = 0;
            foreach (var kv in all)
            {
                if (cancellationToken.IsCancellationRequested) break;
                i++;

                var userId   = kv.Key;
                var settings = kv.Value;
                if (!settings.EnableAutoSync || string.IsNullOrWhiteSpace(settings.Username))
                {
                    progress.Report(100.0 * i / all.Count);
                    continue;
                }

                // Resolve username for the rating row
                string userName = "Unknown";
                try
                {
                    if (Guid.TryParse(userId, out var gid))
                    {
                        var user = _userManager.GetUserById(gid);
                        if (user != null) userName = user.Username;
                    }
                }
                catch { }

                try
                {
                    var result = await _syncService.SyncRssAsync(userId, userName).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(result.Error))
                        _logger.LogWarning("[StarTrack] Letterboxd auto-sync error for {User}: {Err}", userName, result.Error);
                    else if (result.NotModified)
                        _logger.LogDebug("[StarTrack] Letterboxd auto-sync: feed unchanged for {User} (304)", userName);
                    else if (result.Imported + result.Updated + result.WatchlistAdded + result.LikesAdded > 0)
                        _logger.LogInformation("[StarTrack] Letterboxd auto-sync detected updates for {User}: imported={I} updated={U} watchlist+{W} likes+{L}",
                            userName, result.Imported, result.Updated, result.WatchlistAdded, result.LikesAdded);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[StarTrack] Letterboxd auto-sync threw for {User}", userName);
                }

                // [#25] Retry rows that matched nothing on an earlier import.
                //
                // Deliberately OUTSIDE the sync above and independent of its
                // outcome, including the 304 path that is the common case here.
                // These two wait on different things: the RSS poll on the member's
                // Letterboxd feed, this on the Jellyfin library. Gating it on a
                // feed change would drain a backlog only for members still
                // actively posting to Letterboxd — the opposite of who needs it.
                if (libraryChanged)
                    retriedThisTick |= await RetryPendingAsync(userId, userName, cancellationToken).ConfigureAwait(false);

                progress.Report(100.0 * i / all.Count);
            }

            // Advance the fingerprint only after a tick in which a retry actually
            // completed. If nobody had a backlog, nothing was skipped and there is
            // nothing to remember; if a pass threw, leaving the old value means the
            // next tick treats the library as still-changed and tries again.
            if (retriedThisTick) _lastLibraryFingerprint = fingerprint;

            progress.Report(100);
        }

        /// <summary>
        /// The movie-library fingerprint as of the last retry that actually ran.
        ///
        /// This is the local counterpart of the ETag the RSS poll sends: same
        /// idea, same payoff. Unchanged means skip, and here that is not just an
        /// optimisation but provably lossless — the only thing that can resolve a
        /// pending row is an item appearing that was not there before, so if the
        /// library has not moved, a retry cannot possibly succeed.
        ///
        /// Server-wide rather than per-user because the library is: one query per
        /// tick regardless of how many members have a backlog.
        ///
        /// In-memory rather than persisted. The cost of forgetting it on restart
        /// is exactly one extra retry pass, which is cheaper than a stored field
        /// to migrate — and a pass after a restart is arguably the right thing to
        /// do anyway.
        /// </summary>
        private string? _lastLibraryFingerprint;

        /// <summary>
        /// Replays one user's pending rows. Called only when the library moved,
        /// so the remaining short-circuit is the empty queue — a dictionary
        /// lookup, and the common case once a backlog has drained.
        /// </summary>
        /// <returns>True when a pass ran to completion, which is what licenses the caller to advance the fingerprint.</returns>
        private async Task<bool> RetryPendingAsync(string userId, string userName, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return false;

            try
            {
                if (await _syncService.PendingCountAsync(userId).ConfigureAwait(false) == 0) return false;

                var resolved = await _syncService.RetryPendingAsync(userId, userName).ConfigureAwait(false);
                if (resolved > 0)
                    _logger.LogInformation("[StarTrack] Letterboxd pending retry for {User} applied {N} row(s) after a library change", userName, resolved);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] Letterboxd pending retry threw for {User}", userName);
                return false;
            }
        }
    }
}
