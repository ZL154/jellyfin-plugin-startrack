using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.InternalRating.Letterboxd
{
    /// <summary>
    /// Stores per-user Letterboxd sync settings as JSON at
    /// &lt;jellyfin-data&gt;/data/InternalRating/letterboxd.json.
    /// </summary>
    public sealed class LetterboxdSettingsRepository : IDisposable
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private LetterboxdStore _store = new();

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented             = true,
            PropertyNameCaseInsensitive = true
        };

        public LetterboxdSettingsRepository(IApplicationPaths applicationPaths)
        {
            var dir = Path.Combine(applicationPaths.DataPath, "InternalRating");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "letterboxd.json");

            // Point the secret protector at a key ring beside this store BEFORE
            // Load(), so the first read of an encrypted password can already
            // decrypt it. Done here rather than in PluginServiceRegistrator
            // because this constructor is the earliest point that both knows
            // the data path and runs before any secret is touched.
            LetterboxdSecretProtector.KeyDirectory ??= Path.Combine(dir, "letterboxd-keys");

            Load();
        }

        /// <summary>Returns a copy of the settings for a user, or an empty object.</summary>
        public async Task<LetterboxdUserSettings> GetAsync(string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return _store.Users.TryGetValue(userId, out var s) ? Clone(s) : new LetterboxdUserSettings();
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Field-by-field copy so callers can't mutate the live store.
        ///
        /// Kept as one helper rather than inlined at each call site: this used
        /// to be an inline initialiser, which meant every new setting silently
        /// read back as its default until someone remembered to add a line
        /// here. Adding a property to LetterboxdUserSettings now only needs one
        /// edit, in this method.
        /// </summary>
        private static LetterboxdUserSettings Clone(LetterboxdUserSettings s) => new()
        {
            Username           = s.Username,
            EnableAutoSync     = s.EnableAutoSync,
            LastSyncedGuid     = s.LastSyncedGuid,
            LastSyncedAt       = s.LastSyncedAt,
            LastImportedCount  = s.LastImportedCount,
            LastUnmatchedCount = s.LastUnmatchedCount,
            RssETag            = s.RssETag,
            RssLastModified    = s.RssLastModified,
            LastCheckedAt      = s.LastCheckedAt,

            Direction          = s.Direction,
            PasswordEnc        = s.PasswordEnc,
            RawCookiesEnc      = s.RawCookiesEnc,
            UserAgent          = s.UserAgent,
            PushRatings        = s.PushRatings,
            PushWatched        = s.PushWatched,
            PushLiked          = s.PushLiked,
            PushReviews        = s.PushReviews,
            PushDiary          = s.PushDiary,
            PushWatchlist      = s.PushWatchlist,
            DiaryLoggingSince  = s.DiaryLoggingSince,
            LastPushedAt       = s.LastPushedAt,
            LastPushedCount    = s.LastPushedCount,
            LastPushError      = s.LastPushError
        };

        /// <summary>
        /// Stores the Letterboxd account credentials for write-back, encrypting
        /// the password and any raw cookies at rest.
        ///
        /// A null <paramref name="password"/> means "leave the stored one
        /// alone" so the UI can save other settings without round-tripping the
        /// secret to the browser and back. Pass an empty string to clear it.
        /// </summary>
        /// <returns>False when a supplied secret could not be encrypted — the caller must surface that rather than storing plaintext.</returns>
        public async Task<bool> SetAccountAsync(
            string userId,
            LetterboxdDirection direction,
            string? password,
            string? rawCookies,
            string? userAgent,
            bool pushRatings, bool pushWatched, bool pushLiked, bool pushReviews,
            bool pushDiary, bool pushWatchlist)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Users.TryGetValue(userId, out var s))
                {
                    s = new LetterboxdUserSettings();
                    _store.Users[userId] = s;
                }

                if (password != null)
                {
                    if (password.Length == 0) s.PasswordEnc = null;
                    else
                    {
                        var enc = LetterboxdSecretProtector.Protect(password);
                        if (enc == null) return false;   // never fall back to plaintext
                        s.PasswordEnc = enc;
                    }
                }

                if (rawCookies != null)
                {
                    if (rawCookies.Length == 0) s.RawCookiesEnc = null;
                    else
                    {
                        var enc = LetterboxdSecretProtector.Protect(rawCookies);
                        if (enc == null) return false;
                        s.RawCookiesEnc = enc;
                    }
                }

                s.Direction   = direction;
                s.UserAgent   = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent.Trim();
                s.PushRatings = pushRatings;
                s.PushWatched = pushWatched;
                s.PushLiked   = pushLiked;
                s.PushReviews = pushReviews;

                // Stamp the cutoff the first time diary logging is switched on,
                // and clear it when switched off so re-enabling later doesn't
                // suddenly log everything watched in the gap.
                // Start of TODAY, not this instant. Enabling the toggle at 19:00
                // otherwise excludes something logged at 11:00 the same morning,
                // which reads as "it just doesn't work". Today still counts;
                // the back catalogue still does not.
                if (pushDiary && !s.PushDiary)
                    s.DiaryLoggingSince = DateTime.UtcNow.ToLocalTime().Date.ToUniversalTime();
                else if (!pushDiary)           s.DiaryLoggingSince = null;
                s.PushDiary     = pushDiary;
                s.PushWatchlist = pushWatchlist;

                await SaveAsync().ConfigureAwait(false);
                return true;
            }
            finally { _lock.Release(); }
        }

        /// <summary>Records the outcome of a push run.</summary>
        public async Task SetPushStateAsync(string userId, DateTime? at, int pushed, string? error)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Users.TryGetValue(userId, out var s))
                {
                    s = new LetterboxdUserSettings();
                    _store.Users[userId] = s;
                }
                s.LastPushedAt    = at;
                s.LastPushedCount = pushed;
                s.LastPushError   = error;
                await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Returns a snapshot of every user with Letterboxd settings.
        /// Deep-copied: the previous shallow copy handed out live references to
        /// the stored objects, which now carry encrypted credentials.
        /// </summary>
        public async Task<Dictionary<string, LetterboxdUserSettings>> GetAllAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var copy = new Dictionary<string, LetterboxdUserSettings>(_store.Users.Count);
                foreach (var kv in _store.Users) copy[kv.Key] = Clone(kv.Value);
                return copy;
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Sets username + auto-sync toggle. Preserves other sync state fields —
        /// except when the username actually changes.
        /// </summary>
        public async Task SetConfigAsync(string userId, string username, bool enableAutoSync)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Users.TryGetValue(userId, out var s))
                {
                    s = new LetterboxdUserSettings();
                    _store.Users[userId] = s;
                }
                var trimmed = (username ?? string.Empty).Trim();
                if (!string.Equals(s.Username, trimmed, StringComparison.Ordinal))
                {
                    s.RssETag         = null;
                    s.RssLastModified = null;
                    s.LastSyncedGuid  = null;
                }
                s.Username       = trimmed;
                s.EnableAutoSync = enableAutoSync;
                await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        /// <summary>Updates the sync-state fields after a sync run.</summary>
        public async Task SetSyncStateAsync(string userId, string? lastGuid, DateTime? lastAt, int imported, int unmatched)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Users.TryGetValue(userId, out var s))
                {
                    s = new LetterboxdUserSettings();
                    _store.Users[userId] = s;
                }
                if (lastGuid != null) s.LastSyncedGuid = lastGuid;
                if (lastAt  != null)  s.LastSyncedAt   = lastAt;
                s.LastImportedCount  = imported;
                s.LastUnmatchedCount = unmatched;
                await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Records the HTTP ETag / Last-Modified headers from the latest RSS
        /// fetch plus a "checked at" timestamp. Called on every poll regardless
        /// of whether the feed actually changed, so the next conditional GET
        /// has fresh validators.
        /// </summary>
        public async Task SetRssCacheAsync(string userId, string? etag, string? lastModified, DateTime checkedAt)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Users.TryGetValue(userId, out var s))
                {
                    s = new LetterboxdUserSettings();
                    _store.Users[userId] = s;
                }
                if (etag != null)         s.RssETag         = etag;
                if (lastModified != null) s.RssLastModified = lastModified;
                s.LastCheckedAt = checkedAt;
                await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        private void Load()
        {
            if (!File.Exists(_filePath)) return;
            try
            {
                var json = File.ReadAllText(_filePath);
                _store = JsonSerializer.Deserialize<LetterboxdStore>(json, _jsonOptions) ?? new LetterboxdStore();
            }
            catch
            {
                _store = new LetterboxdStore();
            }
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
