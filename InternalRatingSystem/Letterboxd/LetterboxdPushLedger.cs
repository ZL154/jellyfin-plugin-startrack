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
    /// Ratings and watched-flags are idempotent on Letterboxd's side, so repeating
    /// them is harmless for CORRECTNESS — but not for traffic, which is the
    /// second thing this file exists for.
    ///
    /// Re-pushing every rated film on every run means, for a 1300-film library:
    /// 1300 film resolutions (a redirect plus a full HTML page download each),
    /// 1300 rating writes and 1300 watch writes, every hour, forever. That is
    /// roughly five thousand requests an hour aimed at a site behind Cloudflare,
    /// and it would get the user blocked in short order. So the ledger also
    /// records WHAT was last pushed for each film, letting an unchanged film be
    /// skipped before it costs a single request. Steady state is zero traffic.
    ///
    /// Stored at &lt;jellyfin-data&gt;/data/InternalRating/letterboxd-pushed.json.
    /// Deliberately a separate file from letterboxd.json: this grows with the
    /// user's library, and the settings store gets cloned on every read.
    /// </summary>
    public sealed class LetterboxdPushLedger : IDisposable
    {
        /// <summary>On-disk shape.</summary>
        private sealed class LedgerStore
        {
            /// <summary>Diary dedupe: userId → set of "tmdbId:yyyy-MM-dd" keys.</summary>
            [JsonPropertyName("entries")]
            public Dictionary<string, HashSet<string>> Entries { get; set; } = new();

            /// <summary>
            /// What was last successfully pushed: userId → tmdbId → signature.
            /// Lets a run skip films whose rating and like state have not moved.
            /// </summary>
            [JsonPropertyName("state")]
            public Dictionary<string, Dictionary<string, string>> State { get; set; } = new();

            /// <summary>
            /// TMDb id → "slug|filmId|productionId". NOT per-user: which
            /// Letterboxd film a TMDb id maps to is the same for everybody, and
            /// resolving costs a redirect plus a full HTML page download.
            /// </summary>
            [JsonPropertyName("films")]
            public Dictionary<string, string> Films { get; set; } = new();
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
                var removed = _store.Entries.Remove(userId);
                removed |= _store.State.Remove(userId);
                if (removed) await SaveAsync().ConfigureAwait(false);
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

        // ---- push state: skip films whose rating/like has not moved ----

        /// <summary>
        /// Signature of what would be pushed for a film. Any change here means
        /// the film needs re-pushing; equality means the remote already matches.
        /// Includes the per-kind toggles so enabling one later re-pushes.
        /// </summary>
        public static string Signature(double stars, bool liked, bool pushRatings, bool pushWatched, bool pushLiked)
            => stars.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
               + (liked ? "|L" : "|-")
               + (pushRatings ? "r" : "-")
               + (pushWatched ? "w" : "-")
               + (pushLiked ? "l" : "-");

        /// <summary>True when this exact signature was already pushed successfully.</summary>
        public async Task<bool> IsUnchangedAsync(string userId, int tmdbId, string signature)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return _store.State.TryGetValue(userId, out var m)
                    && m.TryGetValue(tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var sig)
                    && string.Equals(sig, signature, StringComparison.Ordinal);
            }
            finally { _lock.Release(); }
        }

        /// <summary>Records a successful push. Call only after the writes confirmed.</summary>
        public async Task SetStateAsync(string userId, int tmdbId, string signature)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.State.TryGetValue(userId, out var m))
                {
                    m = new Dictionary<string, string>(StringComparer.Ordinal);
                    _store.State[userId] = m;
                }
                var key = tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!m.TryGetValue(key, out var cur) || cur != signature)
                {
                    m[key] = signature;
                    await SaveAsync().ConfigureAwait(false);
                }
            }
            finally { _lock.Release(); }
        }

        // ---- film resolution cache ----

        /// <summary>Cached TMDb → Letterboxd film, or null if never resolved.</summary>
        public async Task<(string Slug, string FilmId, string? ProductionId)?> GetFilmAsync(int tmdbId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Films.TryGetValue(tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var raw))
                    return null;
                var parts = raw.Split('|');
                if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
                return (parts[0], parts[1], parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null);
            }
            finally { _lock.Release(); }
        }

        /// <summary>Caches a resolved film so it never costs another page fetch.</summary>
        public async Task SetFilmAsync(int tmdbId, string slug, string filmId, string? productionId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                _store.Films[tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                    slug + "|" + filmId + "|" + (productionId ?? string.Empty);
                await SaveAsync().ConfigureAwait(false);
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
