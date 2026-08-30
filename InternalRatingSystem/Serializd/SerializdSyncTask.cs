using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Serializd
{
    /// <summary>
    /// Drives an import for one user: opens an UNAUTHENTICATED session, reads
    /// the public diary, persists the outcome.
    ///
    /// Separate from <see cref="SerializdPushRunner"/> because the two halves
    /// have genuinely different requirements — this one never touches a
    /// password, and folding them together would make it look as though it did.
    /// </summary>
    public sealed class SerializdSyncRunner
    {
        private readonly SerializdSettingsRepository _settings;
        private readonly SerializdPullService _pull;
        private readonly IUserManager _userManager;
        private readonly ILogger<SerializdSyncRunner> _logger;

        public SerializdSyncRunner(
            SerializdSettingsRepository settings,
            SerializdPullService pull,
            IUserManager userManager,
            ILogger<SerializdSyncRunner> logger)
        {
            _settings    = settings;
            _pull        = pull;
            _userManager = userManager;
            _logger      = logger;
        }

        /// <summary>Runs an import for one user. Never throws.</summary>
        public async Task<SerializdImportResult> RunForUserAsync(string userId, CancellationToken ct = default)
        {
            var settings = await _settings.GetAsync(userId).ConfigureAwait(false);

            if (settings.Direction is not (SerializdDirection.ImportOnly or SerializdDirection.TwoWay))
                return new SerializdImportResult();

            var userName = "Unknown";
            try
            {
                if (Guid.TryParse(userId, out var guid))
                    userName = _userManager.GetUserById(guid)?.Username ?? userName;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StarTrack] Serializd: could not resolve a name for {UserId}", userId);
            }

            // No AuthenticateAsync call: the diary endpoint is public, so an
            // import must work for someone who has only ever typed a username.
            using var session = new SerializdSession(_logger);

            var result = await _pull.ImportAsync(userId, userName, settings, session, ct).ConfigureAwait(false);

            await _settings.SetSyncStateAsync(
                userId,
                result.Error == null ? DateTime.UtcNow : null,
                result.TotalWritten,
                result.Error).ConfigureAwait(false);

            return result;
        }
    }

    /// <summary>
    /// Hourly Serializd import for every user who opted in.
    ///
    /// Hourly matches the Letterboxd sync task. Reads are cheap and
    /// unauthenticated, but they are still somebody else's server, so the pull
    /// paces itself between pages and stops at a page cap.
    /// </summary>
    public sealed class SerializdSyncTask : IScheduledTask
    {
        private readonly SerializdSettingsRepository _settings;
        private readonly SerializdSyncRunner _runner;
        private readonly ILogger<SerializdSyncTask> _logger;

        public SerializdSyncTask(
            SerializdSettingsRepository settings,
            SerializdSyncRunner runner,
            ILogger<SerializdSyncTask> logger)
        {
            _settings = settings;
            _runner   = runner;
            _logger   = logger;
        }

        /// <inheritdoc />
        public string Name        => "StarTrack Serializd Sync";

        /// <inheritdoc />
        public string Description => "Imports series, season and episode ratings from a public Serializd diary. Needs only a username.";

        /// <inheritdoc />
        public string Category    => "StarTrack";

        /// <inheritdoc />
        public string Key         => "StarTrackSerializdSync";

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
                if (s.Direction is not (SerializdDirection.ImportOnly or SerializdDirection.TwoWay)) continue;
                if (string.IsNullOrWhiteSpace(s.Username)) continue;

                try
                {
                    var r = await _runner.RunForUserAsync(kv.Key, cancellationToken).ConfigureAwait(false);
                    if (r.Error != null)
                        _logger.LogWarning("[StarTrack] Serializd import error for {User}: {Err}", s.Username, r.Error);
                    else if (r.TotalWritten > 0)
                        _logger.LogInformation(
                            "[StarTrack] Serializd import for {User}: imported={I} updated={U} unmatched={M}",
                            s.Username, r.Imported, r.Updated, r.Unmatched);
                }
                catch (Exception ex)
                {
                    // One user's failure must not abort the run for everyone else.
                    _logger.LogError(ex, "[StarTrack] Serializd import threw for {User}", s.Username);
                }
            }

            progress.Report(100);
        }
    }
}
