using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.ExternalSync
{
    /// <summary>
    /// Scheduled task: once per day, export all users' StarTrack ratings to
    /// &lt;DataPath&gt;/InternalRating/exports/&lt;userId&gt;-&lt;yyyy-MM-dd&gt;.csv|json
    /// Only runs when <see cref="PluginConfiguration.AutoExportDaily"/> is enabled.
    /// </summary>
    public sealed class AutoExportTask : IScheduledTask
    {
        private readonly RatingGatherer _gatherer;
        private readonly FileExportService _exportService;
        private readonly IApplicationPaths _appPaths;
        private readonly ILogger<AutoExportTask> _logger;

        public AutoExportTask(
            RatingGatherer gatherer,
            FileExportService exportService,
            IApplicationPaths appPaths,
            ILogger<AutoExportTask> logger)
        {
            _gatherer      = gatherer;
            _exportService = exportService;
            _appPaths      = appPaths;
            _logger        = logger;
        }

        public string Name        => "StarTrack Daily Rating Export";
        public string Description => "Exports each user's StarTrack ratings to a CSV or JSON file on disk once per day.";
        public string Category    => "StarTrack";
        public string Key         => "StarTrackAutoExport";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo
            {
                Type           = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks   // 3 AM UTC — low-traffic window
            }
        };

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.AutoExportDaily)
            {
                _logger.LogDebug("[StarTrack] AutoExportTask: AutoExportDaily is disabled — skipping.");
                progress.Report(100);
                return;
            }

            var format = string.Equals(config.AutoExportFormat, "json", StringComparison.OrdinalIgnoreCase)
                ? "json"
                : "csv";

            // Ensure the export directory exists
            var exportDir = Path.Combine(_appPaths.DataPath, "InternalRating", "exports");
            Directory.CreateDirectory(exportDir);

            if (Plugin.Instance?.Repository is not { } repository)
            {
                _logger.LogWarning("[StarTrack] AutoExport: plugin/repository not initialised; skipping");
                return;
            }

            var userIds = await repository.GetUserIdsWithRatingsAsync().ConfigureAwait(false);
            if (userIds.Count == 0)
            {
                _logger.LogInformation("[StarTrack] AutoExportTask: no users with ratings — nothing to export.");
                progress.Report(100);
                return;
            }

            _logger.LogInformation("[StarTrack] AutoExportTask: exporting {Count} user(s) as {Fmt}", userIds.Count, format);

            int i = 0;
            var dateSuffix = DateTime.UtcNow.ToString("yyyy-MM-dd");

            foreach (var userId in userIds)
            {
                if (cancellationToken.IsCancellationRequested) break;
                i++;

                try
                {
                    var ratings = await _gatherer.GatherAsync(userId).ConfigureAwait(false);
                    if (ratings.Count == 0)
                    {
                        progress.Report(100.0 * i / userIds.Count);
                        continue;
                    }

                    string content;
                    string fileName;
                    if (format == "json")
                    {
                        content  = _exportService.BuildJson(ratings);
                        fileName = $"{userId}-{dateSuffix}.json";
                    }
                    else
                    {
                        content  = _exportService.BuildLetterboxdCsv(ratings);
                        fileName = $"{userId}-{dateSuffix}.csv";
                    }

                    var filePath = Path.Combine(exportDir, fileName);
                    await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, cancellationToken)
                        .ConfigureAwait(false);

                    _logger.LogInformation("[StarTrack] AutoExportTask: wrote {N} ratings for user={U} → {Path}",
                        ratings.Count, userId, filePath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[StarTrack] AutoExportTask: export failed for user={U}", userId);
                }

                progress.Report(100.0 * i / userIds.Count);
            }

            progress.Report(100);
        }
    }
}
