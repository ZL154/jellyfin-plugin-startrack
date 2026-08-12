using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.InternalRating.Letterboxd
{
    /// <summary>
    /// Remembers which dated diary entries StarTrack has already written to
    /// Letterboxd, so the scheduled push never writes the same one twice.
    ///
    /// WHY THIS HAS TO EXIST: <c>POST /api/v0/log-entries</c> creates a NEW
    /// diary entry on every call — there is no upsert. The push task runs on a
    /// timer. Without a ledger, a single watched film would be logged into the
    /// member's Letterboxd diary again on every tick: 144 identical entries a
    /// day on a 10-minute schedule, in a diary that is the whole point of the
    /// service. Cleaning that up by hand would be brutal.
    ///
    /// Ratings and watched-flags do NOT go through here — those use Letterboxd's
    /// idempotent /rate/ and /watch/ endpoints, where a repeat is a harmless
    /// no-op. Only the append-only diary write needs guarding.
    ///
    /// Stored at &lt;jellyfin-data&gt;/data/InternalRating/letterboxd-pushed.json.
    /// Deliberately a separate file from letterboxd.json: this grows with the
    /// user's library, and the settings store gets cloned on every read.
    /// </summary>
    public sealed class LetterboxdPushLedger : IDisposable
    {
        /// <summary>On-disk shape: userId → set of "tmdbId:yyyy-MM-dd" keys.</summary>
        private sealed class LedgerStore
        {
            [JsonPropertyName("entries")]
            public Dictionary<string, HashSet<string>> Entries { get; set; } = new();
        }

        private readonly string _filePath;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private LedgerStore _store = new();

        private static readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = false,          // can get large; no need to pretty-print
            PropertyNameCaseInsensitive = true
        };

        public LetterboxdPushLedger(IApplicationPaths applicationPaths)
        {
            var dir = Path.Combine(applicationPaths.DataPath, "InternalRating");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "letterboxd-pushed.json");
            Load();
        }

        /// <summary>
        /// Key for one diary entry. Date-only, because Letterboxd diary entries
        /// are a day, not an instant — two watches of the same film on the same
        /// day are one diary entry as far as this dedupe is concerned.
        /// </summary>
        internal static string Key(int tmdbId, DateTime watchedAt) =>
            tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ":" + watchedAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>True when this exact film+date has already been logged.</summary>
        public async Task<bool> HasAsync(string userId, int tmdbId, DateTime watchedAt)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return _store.Entries.TryGetValue(userId, out var set) && set.Contains(Key(tmdbId, watchedAt));
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Records a written entry. Call this only after Letterboxd confirms the
        /// write — recording optimistically would silently drop entries whenever
        /// a push failed.
        /// </summary>
        public async Task AddAsync(string userId, int tmdbId, DateTime watchedAt)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Entries.TryGetValue(userId, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    _store.Entries[userId] = set;
                }
                if (set.Add(Key(tmdbId, watchedAt)))
                    await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Seeds the ledger with entries that already exist on Letterboxd, so a
        /// first run doesn't duplicate a diary the member built up by hand.
        /// </summary>
        public async Task SeedAsync(string userId, IEnumerable<(int TmdbId, DateTime WatchedAt)> existing)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Entries.TryGetValue(userId, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    _store.Entries[userId] = set;
                }
                var changed = false;
                foreach (var (tmdb, at) in existing)
                    changed |= set.Add(Key(tmdb, at));
                if (changed) await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        /// <summary>Forgets everything for a user — used when they unlink the account.</summary>
        public async Task ClearAsync(string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_store.Entries.Remove(userId)) await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        /// <summary>Number of recorded entries for a user (diagnostics/UI).</summary>
        public async Task<int> CountAsync(string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return _store.Entries.TryGetValue(userId, out var set) ? set.Count : 0;
            }
            finally { _lock.Release(); }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_filePath)) return;
                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json)) return;
                _store = JsonSerializer.Deserialize<LedgerStore>(json, _json) ?? new LedgerStore();
            }
            catch (Exception)
            {
                // A corrupt ledger must not break startup. Starting empty is the
                // safe-ish failure: worst case is one duplicate diary entry per
                // film, not a crash — and far better than refusing to sync.
                _store = new LedgerStore();
            }
        }

        // Caller already holds _lock.
        private async Task SaveAsync()
        {
            var tmp = _filePath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(_store, _json)).ConfigureAwait(false);
            File.Move(tmp, _filePath, overwrite: true);   // atomic-ish: never a half-written ledger
        }

        public void Dispose() => _lock.Dispose();
    }
}
