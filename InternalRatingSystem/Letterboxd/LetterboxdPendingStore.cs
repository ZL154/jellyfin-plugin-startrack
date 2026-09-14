using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.InternalRating.Letterboxd
{
    /// <summary>
    /// What a pending row was going to be applied to. A single Letterboxd export
    /// yields four independent streams, and an unmatched row has to remember
    /// which one it came from to be replayed correctly later.
    /// </summary>
    public enum LetterboxdPendingKind
    {
        /// <summary>A row from ratings.csv — a star rating.</summary>
        Rating = 0,
        /// <summary>A row from watchlist.csv.</summary>
        Watchlist = 1,
        /// <summary>A row from likes/films.csv.</summary>
        Like = 2,
        /// <summary>A row from diary.csv — one dated watch.</summary>
        Diary = 3
    }

    /// <summary>One Letterboxd row that found no library match, kept for a later retry.</summary>
    public sealed class LetterboxdPendingRow
    {
        /// <summary>Which CSV stream this row came from.</summary>
        [JsonPropertyName("kind")]
        public LetterboxdPendingKind Kind { get; set; }

        /// <summary>Film title exactly as Letterboxd wrote it.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>Release year, when the export carried one.</summary>
        [JsonPropertyName("year")]
        public int? Year { get; set; }

        /// <summary>Stars on StarTrack's 0.5–5 scale. Ratings and diary rows only.</summary>
        [JsonPropertyName("rating")]
        public double? Rating { get; set; }

        /// <summary>When the rating was made / the film was watched.</summary>
        [JsonPropertyName("date")]
        public DateTime? Date { get; set; }

        /// <summary>True for a diary row Letterboxd marked as a rewatch.</summary>
        [JsonPropertyName("rewatch")]
        public bool Rewatch { get; set; }

        /// <summary>Review text attached to a diary row, if any.</summary>
        [JsonPropertyName("review")]
        public string? Review { get; set; }

        /// <summary>First time this row was seen unmatched. Never updated.</summary>
        [JsonPropertyName("firstSeen")]
        public DateTime FirstSeenAt { get; set; }

        /// <summary>Last time a retry looked for this film and still found nothing.</summary>
        [JsonPropertyName("lastTried")]
        public DateTime? LastTriedAt { get; set; }
    }

    /// <summary>
    /// Holds Letterboxd rows that matched nothing in the library, so later syncs
    /// can retry them as the library grows (issue #25).
    ///
    /// WHY THIS EXISTS: the importer used to drop an unmatched row on the floor.
    /// A member importing a 2000-film Letterboxd history into a 400-film server
    /// silently lost about 1600 ratings, and the only way to recover any of them
    /// was to notice a film had been added and re-upload the whole export by
    /// hand. Keeping the rows turns that into something that resolves itself.
    ///
    /// This is the inverse of <see cref="LetterboxdPushLedger"/>, which records
    /// work already done so it can be skipped. This records work NOT done so it
    /// can be repeated — and unlike the ledger, it shrinks: every retry that
    /// finds a match removes a row permanently, so the cost of the feature falls
    /// as it succeeds.
    ///
    /// Stored at jellyfin-data/data/InternalRating/letterboxd-pending.json,
    /// its own file for the same reason the ledger is: it is sized by the part of
    /// a member's Letterboxd history the server does not have, which can be far
    /// larger than their settings, and the settings store is cloned on every read.
    /// </summary>
    public sealed class LetterboxdPendingStore : IDisposable
    {
        /// <summary>
        /// Ceiling on rows kept per user. A full Letterboxd export tops out
        /// around 5000 films, so this only bites on a library that matches almost
        /// nothing — exactly the case where an unbounded file would hurt most.
        /// Merges past the cap are dropped rather than evicting existing rows:
        /// the ones already queued have been retried and are cheap to keep.
        /// </summary>
        public const int MaxRowsPerUser = 10000;

        /// <summary>On-disk shape.</summary>
        private sealed class PendingStore
        {
            /// <summary>userId → dedupe key → row.</summary>
            [JsonPropertyName("users")]
            public Dictionary<string, Dictionary<string, LetterboxdPendingRow>> Users { get; set; } = new();
        }

        private readonly string _filePath;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private PendingStore _store = new();

        private static readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = false,          // grows with the backlog; no need to pretty-print
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>Creates the store, loading any existing queue from disk.</summary>
        public LetterboxdPendingStore(IApplicationPaths applicationPaths)
        {
            var dir = Path.Combine(applicationPaths.DataPath, "InternalRating");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "letterboxd-pending.json");
            Load();
        }

        /// <summary>
        /// Test seam: point the store at an explicit file instead of the Jellyfin
        /// data path, so tests can round-trip a real file without a fake
        /// IApplicationPaths.
        /// </summary>
        internal LetterboxdPendingStore(string filePath)
        {
            _filePath = filePath;
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Load();
        }

        /// <summary>
        /// Identity of a pending row. Built from the NORMALIZED title so the same
        /// film re-exported with different punctuation doesn't queue twice.
        ///
        /// Diary rows include the watched date: a rewatch is a genuinely separate
        /// entry, and collapsing them on (title, year) would lose every watch but
        /// one. The other kinds deliberately exclude the date, so re-importing an
        /// export where a rating's timestamp shifted updates the row in place
        /// rather than duplicating it.
        /// </summary>
        internal static string Key(LetterboxdPendingKind kind, string normalizedTitle, int? year, DateTime? date)
        {
            var head = (int)kind + "|" + normalizedTitle + "|"
                     + (year?.ToString(CultureInfo.InvariantCulture) ?? "-");
            return kind == LetterboxdPendingKind.Diary
                ? head + "|" + (date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-")
                : head;
        }

        /// <summary>Every pending row for a user. Copies, so callers can't mutate the store.</summary>
        public async Task<List<LetterboxdPendingRow>> GetAsync(string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Users.TryGetValue(userId, out var rows)) return new List<LetterboxdPendingRow>();
                return rows.Values.Select(Clone).ToList();
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Adds unmatched rows, keyed so a re-import merges instead of duplicating.
        ///
        /// An existing row keeps its original FirstSeenAt — that timestamp answers
        /// "how long has this been waiting", which a re-upload should not reset.
        /// </summary>
        /// <returns>How many rows were newly queued (excludes updates to rows already held).</returns>
        public async Task<int> MergeAsync(string userId, IEnumerable<(string NormalizedTitle, LetterboxdPendingRow Row)> rows)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Users.TryGetValue(userId, out var map))
                {
                    map = new Dictionary<string, LetterboxdPendingRow>(StringComparer.Ordinal);
                    _store.Users[userId] = map;
                }

                var added = 0;
                var changed = false;
                foreach (var (norm, row) in rows)
                {
                    if (string.IsNullOrEmpty(norm)) continue;
                    var key = Key(row.Kind, norm, row.Year, row.Date);

                    if (map.TryGetValue(key, out var existing))
                    {
                        // Refresh the payload (a re-export may carry a corrected
                        // rating) but preserve when we first saw it.
                        row.FirstSeenAt = existing.FirstSeenAt;
                        row.LastTriedAt = existing.LastTriedAt;
                        map[key] = row;
                        changed = true;
                        continue;
                    }

                    if (map.Count >= MaxRowsPerUser) break;
                    if (row.FirstSeenAt == default) row.FirstSeenAt = DateTime.UtcNow;
                    map[key] = row;
                    added++;
                    changed = true;
                }

                if (changed) await SaveAsync().ConfigureAwait(false);
                return added;
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Drops rows that have been satisfied — matched and written, or found to
        /// be already present. Called with the keys a retry pass resolved.
        /// </summary>
        public async Task RemoveAsync(string userId, IEnumerable<string> keys)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Users.TryGetValue(userId, out var map)) return;
                var changed = false;
                foreach (var k in keys) changed |= map.Remove(k);
                if (map.Count == 0) _store.Users.Remove(userId);
                if (changed) await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Stamps rows that were retried and still found nothing, so the UI can
        /// show a backlog is being worked rather than ignored.
        /// </summary>
        public async Task MarkTriedAsync(string userId, IEnumerable<string> keys, DateTime at)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Users.TryGetValue(userId, out var map)) return;
                var changed = false;
                foreach (var k in keys)
                {
                    if (!map.TryGetValue(k, out var row)) continue;
                    row.LastTriedAt = at;
                    changed = true;
                }
                if (changed) await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        /// <summary>Forgets everything queued for a user.</summary>
        public async Task ClearAsync(string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_store.Users.Remove(userId)) await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// How many rows a user has queued. The retry pass calls this first: it is
        /// the cheap check that lets a tick with an empty queue skip building the
        /// movie lookup, which is a full library scan.
        /// </summary>
        public async Task<int> CountAsync(string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return _store.Users.TryGetValue(userId, out var map) ? map.Count : 0;
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Field-by-field copy so callers can't mutate the live store. Like
        /// LetterboxdSettingsRepository.Clone, this must gain a line whenever
        /// LetterboxdPendingRow gains a property, or the new field silently
        /// reads back as its default.
        /// </summary>
        private static LetterboxdPendingRow Clone(LetterboxdPendingRow r) => new()
        {
            Kind = r.Kind,
            Name = r.Name,
            Year = r.Year,
            Rating = r.Rating,
            Date = r.Date,
            Rewatch = r.Rewatch,
            Review = r.Review,
            FirstSeenAt = r.FirstSeenAt,
            LastTriedAt = r.LastTriedAt
        };

        private void Load()
        {
            try
            {
                if (!File.Exists(_filePath)) return;
                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json)) return;
                _store = JsonSerializer.Deserialize<PendingStore>(json, _json) ?? new PendingStore();
            }
            catch (Exception)
            {
                // A corrupt queue must not break startup. Starting empty loses a
                // backlog of retries, which is the same position the user was in
                // before this feature existed — acceptable next to failing to boot.
                _store = new PendingStore();
            }
        }

        // Caller already holds _lock.
        private async Task SaveAsync()
        {
            var tmp = _filePath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(_store, _json)).ConfigureAwait(false);
            File.Move(tmp, _filePath, overwrite: true);   // never a half-written queue
        }

        /// <summary>Releases the write lock.</summary>
        public void Dispose() => _lock.Dispose();
    }
}
