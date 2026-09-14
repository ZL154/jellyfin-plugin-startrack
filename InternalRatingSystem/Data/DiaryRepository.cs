using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Models;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.InternalRating.Data
{
    /// <summary>
    /// JSON-backed per-user diary. Multiple entries per film are allowed
    /// (rewatches). File lives at
    /// &lt;jellyfin-data&gt;/data/InternalRating/diary.json.
    /// </summary>
    /// <summary>
    /// Read side of the diary, so the Letterboxd push can be unit-tested with a
    /// fake instead of a real JSON file on disk.
    /// </summary>
    public interface IWatchDiaryReader
    {
        /// <summary>Diary entries for a user, newest first.</summary>
        Task<List<DiaryEntry>> GetEntriesAsync(string userId, int limit = 10000);
    }

    public sealed class DiaryRepository : IDisposable, IWatchDiaryReader
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private DiaryStore _store = new();

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented             = true,
            PropertyNameCaseInsensitive = true
        };

        public DiaryRepository(IApplicationPaths applicationPaths)
        {
            var dir = Path.Combine(applicationPaths.DataPath, "InternalRating");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "diary.json");
            Load();
        }

        public async Task<List<DiaryEntry>> GetEntriesAsync(string userId, int limit = 10000)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var d = GetOrInit(userId);
                return d.Entries
                    .OrderByDescending(e => e.WatchedAt)
                    .Take(limit)
                    .Select(e => new DiaryEntry
                    {
                        Id        = e.Id,
                        ItemId    = e.ItemId,
                        WatchedAt = e.WatchedAt,
                        Stars     = e.Stars,
                        Review    = e.Review,
                        Rewatch   = e.Rewatch
                    })
                    .ToList();
            }
            finally { _lock.Release(); }
        }

        public async Task<DiaryEntry> AddEntryAsync(string userId, DiaryEntry entry)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var d = GetOrInit(userId);
                if (string.IsNullOrEmpty(entry.Id)) entry.Id = Guid.NewGuid().ToString("N");
                if (entry.WatchedAt == default) entry.WatchedAt = DateTime.UtcNow;
                d.Entries.Add(entry);
                await SaveAsync().ConfigureAwait(false);
                return entry;
            }
            finally { _lock.Release(); }
        }

        public async Task DeleteEntryAsync(string userId, string entryId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var d = GetOrInit(userId);
                d.Entries.RemoveAll(e => e.Id == entryId);
                await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Used by the Letterboxd diary.csv importer. Dedupes on
        /// (itemId, watchedAt) so reimporting the same CSV doesn't
        /// duplicate every entry.
        /// </summary>
        /// <summary>
        /// The calendar day an entry falls on, server-local. Used to spot a
        /// second import of the same rated viewing.
        /// </summary>
        internal static DateTime LocalDay(DateTime utc) => utc.ToLocalTime().Date;

        /// <summary>
        /// How far apart two timestamps can be and still describe one viewing,
        /// when one of them is an unrated playback row.
        ///
        /// WHY NOT "SAME DAY": the server usually runs in UTC (every container
        /// does) while the person watching is in their own timezone, and the
        /// server cannot know which. A film that ended at 00:19 BST on the 13th
        /// is stored as 23:19Z on the 12th; the Letterboxd watched-date the user
        /// then sets is "the 13th", stored as 00:00Z. Forty-one minutes apart,
        /// on different UTC days, and both rows render as "Sep 13" in the
        /// browser — which is exactly the duplicate that was reported. A
        /// calendar test cannot be made right from the server side; a window
        /// can. 36 hours covers a date-only Letterboxd stamp landing either
        /// side of the real timestamp, in any timezone.
        /// </summary>
        internal static readonly TimeSpan SameViewingWindow = TimeSpan.FromHours(36);

        internal static bool WithinSameViewing(DateTime a, DateTime b)
            => (a - b).Duration() <= SameViewingWindow;

        /// <summary>
        /// Adds imported entries (Letterboxd RSS, export ZIP), returning how many
        /// diary rows were created or enriched.
        ///
        /// An import meets three kinds of existing entry for the same item:
        ///   - an UNRATED row — the playback logger's placeholder, written before
        ///     the user rated. Fold the rating, review and rewatch flag into the
        ///     nearest one rather than adding a second row. Its timestamp is
        ///     kept: it is the truer record of when the film was actually seen.
        ///     (See ApplyRatingAsync for why there is no time window on this.)
        ///   - a RATED row on the same day — already there; skip.
        ///   - nothing — add it.
        /// </summary>
        public async Task<int> ImportEntriesAsync(string userId, IEnumerable<DiaryEntry> entries)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var d = GetOrInit(userId);
                int changed = 0;
                foreach (var e in entries)
                {
                    var sameItem = d.Entries
                        .Where(x => string.Equals(x.ItemId, e.ItemId, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (e.Stars is not null)
                    {
                        var placeholder = sameItem
                            .Where(x => x.Stars is null)
                            .OrderBy(x => (x.WatchedAt - e.WatchedAt).Duration())
                            .FirstOrDefault();
                        if (placeholder != null)
                        {
                            placeholder.Stars   = e.Stars;
                            placeholder.Review  = string.IsNullOrWhiteSpace(placeholder.Review) ? e.Review : placeholder.Review;
                            placeholder.Rewatch = placeholder.Rewatch || e.Rewatch;
                            changed++;
                            continue;
                        }
                    }

                    if (sameItem.Any(x => LocalDay(x.WatchedAt) == LocalDay(e.WatchedAt)))
                        continue;

                    if (string.IsNullOrEmpty(e.Id)) e.Id = Guid.NewGuid().ToString("N");
                    d.Entries.Add(e);
                    changed++;
                }
                if (changed > 0) await SaveAsync().ConfigureAwait(false);
                return changed;
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Put a rating the user just gave onto any unrated diary row for the
        /// same film. Returns how many rows were enriched.
        ///
        /// No time window here, deliberately. StarTrack holds one rating per
        /// user per film, so an unrated diary row for a film the user has rated
        /// is not a different fact about a different viewing — it is the same
        /// film with the score missing, and the film's own page already shows
        /// the score. The reporting user's diary had nine such rows, every one
        /// rated the same day it was watched. The window still applies to
        /// deciding whether an IMPORTED rated row is a duplicate of an existing
        /// rated one; that is a different question.
        /// </summary>
        public async Task<int> ApplyRatingAsync(string userId, string itemId, double stars, string? review)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Users.TryGetValue(userId, out var d)) return 0;
                var n = 0;
                foreach (var e in d.Entries)
                {
                    if (e.Stars is not null || !string.Equals(e.ItemId, itemId, StringComparison.OrdinalIgnoreCase)) continue;
                    e.Stars = stars;
                    if (string.IsNullOrWhiteSpace(e.Review) && !string.IsNullOrWhiteSpace(review)) e.Review = review.Trim();
                    n++;
                }
                if (n > 0) await SaveAsync().ConfigureAwait(false);
                return n;
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// One-time repair for diaries written before <see cref="ApplyRatingAsync"/>
        /// existed: every unrated row whose film the user has since rated gets
        /// that rating. Runs at startup; idempotent; cheap (one lookup per
        /// unrated row, and unrated rows are the minority).
        /// </summary>
        public async Task<int> BackfillFromRatingsAsync(Func<string, string, Task<double?>> userStarsFor)
        {
            var fixes = new List<(string User, DiaryEntry Row, double Stars)>();
            List<(string User, DiaryEntry Row)> unrated;
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                unrated = _store.Users
                    .SelectMany(u => u.Value.Entries.Where(e => e.Stars is null).Select(e => (u.Key, e)))
                    .ToList();
            }
            finally { _lock.Release(); }

            foreach (var (user, row) in unrated)
            {
                double? stars = null;
                try { stars = await userStarsFor(user, row.ItemId).ConfigureAwait(false); } catch { }
                if (stars is > 0) fixes.Add((user, row, stars.Value));
            }
            if (fixes.Count == 0) return 0;

            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var (_, row, stars) in fixes)
                    if (row.Stars is null) row.Stars = stars;
                await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
            return fixes.Count;
        }

        private UserDiary GetOrInit(string userId)
        {
            if (!_store.Users.TryGetValue(userId, out var d))
            {
                d = new UserDiary();
                _store.Users[userId] = d;
            }
            return d;
        }

        private void Load()
        {
            if (!File.Exists(_filePath)) return;
            try
            {
                var json = File.ReadAllText(_filePath);
                _store = JsonSerializer.Deserialize<DiaryStore>(json, _jsonOptions) ?? new DiaryStore();
            }
            catch { _store = new DiaryStore(); }
        }

        private async Task SaveAsync()
        {
            var json = JsonSerializer.Serialize(_store, _jsonOptions);
            var tmp = _filePath + ".tmp";
            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
            File.Move(tmp, _filePath, overwrite: true);
        }

        public void Dispose() => _lock.Dispose();
    }
}
