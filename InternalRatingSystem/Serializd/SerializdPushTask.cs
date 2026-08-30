using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Serializd
{
    /// <summary>
    /// Hourly push of TV ratings to Serializd for every user who opted in.
    ///
    /// Hourly for the same reason as the Letterboxd push: each run signs in and
    /// issues real writes, so a tighter cadence would be wasteful and rude. The
    /// skip-if-unchanged ledger means a steady state costs one login and nothing
    /// else.
    /// </summary>
    public sealed class SerializdPushTask : IScheduledTask
    {
        private readonly SerializdSettingsRepository _settings;
        private readonly SerializdPushRunner _runner;
        private readonly ILogger<SerializdPushTask> _logger;

        public SerializdPushTask(
            SerializdSettingsRepository settings,
            SerializdPushRunner runner,
            ILogger<SerializdPushTask> logger)
        {
            _settings = settings;
            _runner   = runner;
            _logger   = logger;
        }

        /// <inheritdoc />
        public string Name        => "StarTrack Serializd Push";

        /// <inheritdoc />
        public string Description => "Pushes TV series and episode ratings from StarTrack to Serializd for users who enabled it.";

        /// <inheritdoc />
        public string Category    => "StarTrack";

        /// <inheritdoc />
        public string Key         => "StarTrackSerializdPush";

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
                if (s.Direction != SerializdDirection.ExportOnly) continue;
                if (string.IsNullOrWhiteSpace(s.Email)) continue;

                try
                {
                    var r = await _runner.RunForUserAsync(kv.Key, cancellationToken).ConfigureAwait(false);
                    if (r.Error != null)
                        _logger.LogWarning("[StarTrack] Serializd push error for {User}: {Err}", s.Email, r.Error);
                    else if (r.TotalWritten > 0)
                        _logger.LogInformation(
                            "[StarTrack] Serializd push for {User}: series={S} episodes={E} unchanged={U} unmatched={M}",
                            s.Email, r.Series, r.Episodes, r.Unchanged, r.Unmatched);
                }
                catch (Exception ex)
                {
                    // One user's failure must not abort the run for everyone else.
                    _logger.LogError(ex, "[StarTrack] Serializd push threw for {User}", s.Email);
                }
            }

            progress.Report(100);
        }
    }
}
