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
            "Pulls new ratings from each user's Letterboxd RSS feed and imports any that match items in your library.";
        public string Category => "StarTrack";
        public string Key => "StarTrackLetterboxdSync";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo
            {
                Type          = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(1).Ticks
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
