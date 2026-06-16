using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.ExternalSync
{
    /// <summary>
    /// Scheduled task: every 10 minutes, iterate all users' external provider
    /// connections whose <see cref="SyncDirection"/> is not <c>Off</c>, run a full
    /// sync cycle via <see cref="SyncOrchestrator"/>, and persist the updated
    /// connection state.
    /// </summary>
    public sealed class ExternalSyncTask : IScheduledTask
    {
        private readonly ExternalSyncSettingsRepository _settingsRepo;
        private readonly SyncOrchestrator _orchestrator;
        private readonly IEnumerable<IExternalRatingProvider> _providers;
        private readonly IUserManager _userManager;
        private readonly ILogger<ExternalSyncTask> _logger;

        public ExternalSyncTask(
            ExternalSyncSettingsRepository settingsRepo,
            SyncOrchestrator orchestrator,
            IEnumerable<IExternalRatingProvider> providers,
            IUserManager userManager,
            ILogger<ExternalSyncTask> logger)
        {
            _settingsRepo = settingsRepo;
            _orchestrator = orchestrator;
            _providers    = providers;
            _userManager  = userManager;
            _logger       = logger;
        }

        public string Name        => "StarTrack External Rating Sync";
        public string Description => "Syncs each user's ratings with connected external providers (Trakt, Simkl, Yamtrack).";
        public string Category    => "StarTrack";
        public string Key         => "StarTrackExternalSync";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo
            {
                Type          = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(10).Ticks
            }
        };

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var store = await _settingsRepo.GetAllAsync().ConfigureAwait(false);
            if (store.Users.Count == 0)
            {
                progress.Report(100);
                return;
            }

            // Flatten to (userId, providerKey, conn) triples so we can report progress by item.
            var items = store.Users
                .SelectMany(u => u.Value.Providers
                    .Where(p => p.Value.Direction != SyncDirection.Off)
                    .Select(p => (UserId: u.Key, ProviderKey: p.Key, Conn: p.Value)))
                .ToList();

            if (items.Count == 0)
            {
                progress.Report(100);
                return;
            }

            var i = 0;
            foreach (var (userId, providerKey, conn) in items)
            {
                if (cancellationToken.IsCancellationRequested) break;
                i++;

                // Resolve the Jellyfin username for the sync row.
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

                // Resolve the provider by key (case-insensitive, matches ProviderId enum name).
                var provider = _providers.FirstOrDefault(p =>
                    string.Equals(p.Id.ToString(), providerKey, StringComparison.OrdinalIgnoreCase));

                if (provider == null)
                {
                    _logger.LogWarning("[StarTrack] ExternalSyncTask: no provider registered for key={Key}", providerKey);
                    progress.Report(100.0 * i / items.Count);
                    continue;
                }

                try
                {
                    // SyncOneAsync mutates conn in-place (LastSyncedAt, LastError, etc.)
                    var result = await _orchestrator.SyncOneAsync(userId, userName, provider, conn, cancellationToken)
                        .ConfigureAwait(false);

                    // Persist the mutated connection so LastSyncedAt / LastError are saved.
                    await _settingsRepo.SetConnectionAsync(userId, providerKey, conn).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(result.Error))
                        _logger.LogWarning("[StarTrack] ExternalSync auto-sync error for {User}/{Key}: {Err}",
                            userName, providerKey, result.Error);
                    else
                        _logger.LogInformation("[StarTrack] ExternalSync auto-sync OK for {User}/{Key}: pushed={P} pulled={Q}",
                            userName, providerKey, result.Pushed, result.Pulled);
                }
                catch (Exception ex)
                {
                    // SyncOrchestrator.SyncOneAsync should never throw, but be safe.
                    _logger.LogError(ex, "[StarTrack] ExternalSync auto-sync threw for {User}/{Key}", userName, providerKey);
                }

                progress.Report(100.0 * i / items.Count);
            }

            progress.Report(100);
        }
    }
}
