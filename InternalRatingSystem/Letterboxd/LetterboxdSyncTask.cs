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
            "Polls each user's Letterboxd RSS every 10 minutes and only imports when the feed has actually changed (uses HTTP 304 Not Modified to stay cheap).";
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

                progress.Report(100.0 * i / all.Count);
            }

            progress.Report(100);
        }
    }
}
