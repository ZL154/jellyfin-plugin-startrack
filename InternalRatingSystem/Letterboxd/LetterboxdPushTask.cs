using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Letterboxd
{
    /// <summary>
    /// Scheduled task: pushes StarTrack activity to Letterboxd for every user
    /// who opted into an export direction.
    ///
    /// Hourly, not on the import task's 10-minute cadence. Import polls a cached
    /// RSS feed and usually costs a 304; a push signs in and issues real writes
    /// per film, so matching that frequency would be both wasteful and a good
    /// way to attract Cloudflare's attention. The writes are idempotent, so an
    /// hour of latency costs nothing but a slightly later appearance.
    /// </summary>
    public sealed class LetterboxdPushTask : IScheduledTask
    {
        private readonly LetterboxdSettingsRepository _settings;
        private readonly LetterboxdPushRunner _runner;
        private readonly ILogger<LetterboxdPushTask> _logger;

        public LetterboxdPushTask(
            LetterboxdSettingsRepository settings,
            LetterboxdPushRunner runner,
            ILogger<LetterboxdPushTask> logger)
        {
            _settings = settings;
            _runner   = runner;
            _logger   = logger;
        }

        /// <inheritdoc />
        public string Name        => "StarTrack Letterboxd Push";

        /// <inheritdoc />
        public string Description => "Pushes ratings, watched films and likes from StarTrack to Letterboxd for users who enabled an export direction.";

        /// <inheritdoc />
        public string Category    => "StarTrack";

        /// <inheritdoc />
        public string Key         => "StarTrackLetterboxdPush";

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo
            {
                Type          = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(1).Ticks
            }
        };

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var all = await _settings.GetAllAsync().ConfigureAwait(false);
            if (all.Count == 0) { progress.Report(100); return; }

            var i = 0;
            foreach (var kv in all)
            {
                if (cancellationToken.IsCancellationRequested) break;
                i++;
                progress.Report(100.0 * i / all.Count);

                var s = kv.Value;
                if (s.Direction is not (LetterboxdDirection.ExportOnly or LetterboxdDirection.TwoWay)) continue;
                if (string.IsNullOrWhiteSpace(s.Username)) continue;

                try
                {
                    var r = await _runner.RunForUserAsync(kv.Key, cancellationToken).ConfigureAwait(false);
                    if (r.Error != null)
                        _logger.LogWarning("[StarTrack] Letterboxd push error for {User}: {Err}", s.Username, r.Error);
                    else if (r.TotalWritten > 0 || r.SkippedAlreadyLogged > 0)
                        _logger.LogInformation(
                            "[StarTrack] Letterboxd push for {User}: rated={R} watched={W} liked={L} diary={D} skipped={S} unmatched={U}",
                            s.Username, r.Rated, r.Watched, r.Liked, r.DiaryEntries, r.SkippedAlreadyLogged, r.Unmatched);
                }
                catch (Exception ex)
                {
                    // One user's push must never abort the run for everybody else.
                    _logger.LogError(ex, "[StarTrack] Letterboxd push threw for {User}", s.Username);
                }
            }

            progress.Report(100);
        }
    }
}
