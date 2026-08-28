using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Data;
using Jellyfin.Plugin.InternalRating.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating
{
    /// <summary>
    /// Writes a diary entry when someone actually finishes watching something.
    ///
    /// WHY THIS EXISTS: until now the diary could only be filled from a
    /// Letterboxd import or a manual API call, which meant StarTrack's
    /// "chronological journal of every watch" knew nothing about watches that
    /// happened on the media server it is installed in. Watch a film in
    /// Jellyfin and the diary stayed empty. It also made the Letterboxd diary
    /// push date entries by when a rating was created rather than when the film
    /// was seen, because a real watch date was never recorded.
    ///
    /// Server-side on purpose. There is a client-side post-playback prompt in
    /// widget.js, but that only fires when the web UI is open — it misses the
    /// TV app, the phone apps and anything using the API directly. The session
    /// manager sees every client.
    /// </summary>
    public sealed class PlaybackDiaryService : IHostedService
    {
        /// <summary>
        /// How much of an item must be played before it counts as watched.
        /// Matches the spirit of Jellyfin's own played threshold, and stops a
        /// trailer-length sample or an accidental tap creating a diary entry.
        /// </summary>
        private const double CompletionThreshold = 0.90;

        private readonly ISessionManager _sessions;
        private readonly ILibraryManager _library;
        private readonly ILogger<PlaybackDiaryService> _logger;

        public PlaybackDiaryService(
            ISessionManager sessions,
            ILibraryManager library,
            ILogger<PlaybackDiaryService> logger)
        {
            _sessions = sessions;
            _library  = library;
            _logger   = logger;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _sessions.PlaybackStopped += OnPlaybackStopped;
            _logger.LogInformation("[StarTrack] Playback diary logging active.");
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _sessions.PlaybackStopped -= OnPlaybackStopped;
            return Task.CompletedTask;
        }

        private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
        {
            // Never let a diary write break playback handling for the whole
            // server: this runs on Jellyfin's event pipeline.
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(e).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "[StarTrack] Playback diary logging failed."); }
            });
        }

        private async Task HandleAsync(PlaybackStopEventArgs e)
        {
            if (Plugin.Instance is not { } plugin) return;
            if (!plugin.Configuration.LogWatchesToDiary) return;

            var item = e.Item;
            if (item == null) return;

            // Films and episodes only. A "watch" of a series or a folder is not
            // a thing you can put a date on.
            if (item is not Movie && item is not Episode) return;

            var userId = e.Users?.FirstOrDefault()?.Id ?? Guid.Empty;
            if (userId == Guid.Empty) return;

            if (!Completed(e, item)) return;

            var userKey = userId.ToString("N");
            var itemKey = item.Id.ToString("N");
            var watchedAt = DateTime.UtcNow;

            var diary = plugin.Diary;
            var existing = await diary.GetEntriesAsync(userKey).ConfigureAwait(false);

            // Same item, same calendar day — treat as one viewing. Stopping and
            // resuming, or the client firing stop twice, must not produce two
            // entries for one sitting.
            //
            // Deliberately stricter than the rating path, which DOES log a
            // second entry when the rating changes: a changed rating is a
            // deliberate signal from the user, whereas two stop events are
            // usually one film.
            var day = watchedAt.ToLocalTime().Date;
            if (existing.Any(x => string.Equals(x.ItemId, itemKey, StringComparison.OrdinalIgnoreCase)
                               && x.WatchedAt.ToLocalTime().Date == day))
            {
                _logger.LogDebug("[StarTrack] Diary: {Item} already logged today for {User}", item.Name, userKey);
                return;
            }

            // Anything seen before is a rewatch — the same signal Letterboxd uses.
            var rewatch = existing.Any(x => string.Equals(x.ItemId, itemKey, StringComparison.OrdinalIgnoreCase));

            // Carry the current rating if there is one, so the entry reads like a
            // Letterboxd log rather than a bare timestamp. Null means watched but
            // unrated, which the model already expresses.
            double? stars = null;
            try
            {
                var r = await plugin.Repository.GetUserStarsAsync(userKey, itemKey).ConfigureAwait(false);
                if (r is > 0) stars = r;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StarTrack] Diary: could not read existing rating for {Item}", itemKey);
            }

            await diary.AddEntryAsync(userKey, new DiaryEntry
            {
                ItemId    = itemKey,
                WatchedAt = watchedAt,
                Stars     = stars,
                Rewatch   = rewatch
            }).ConfigureAwait(false);

            _logger.LogInformation("[StarTrack] Diary: logged \"{Item}\" for {User}{Rewatch}",
                item.Name, userKey, rewatch ? " (rewatch)" : string.Empty);
        }

        /// <summary>
        /// True when the session got far enough in to count as watched.
        /// Prefers Jellyfin's own completion flag and falls back to the played
        /// fraction, because not every client reports the same fields.
        /// </summary>
        private static bool Completed(PlaybackStopEventArgs e, BaseItem item)
        {
            if (e.PlayedToCompletion) return true;

            var runtime = item.RunTimeTicks ?? 0;
            var position = e.PlaybackPositionTicks ?? 0;
            if (runtime <= 0 || position <= 0) return false;

            return (double)position / runtime >= CompletionThreshold;
        }
    }
}
