using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.InternalRating.Serializd
{
    /// <summary>
    /// Per-user Serializd settings at
    /// &lt;jellyfin-data&gt;/data/InternalRating/serializd.json.
    ///
    /// Secrets reuse <see cref="LetterboxdSecretProtector"/> — it is the plugin's
    /// Data Protection key ring rather than anything Letterboxd-specific, and a
    /// second key ring would double the surface protecting the same thing.
    /// </summary>
    public sealed class SerializdSettingsRepository : IDisposable
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private SerializdStore _store = new();

        private static readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public SerializdSettingsRepository(IApplicationPaths applicationPaths)
        {
            var dir = Path.Combine(applicationPaths.DataPath, "InternalRating");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "serializd.json");
            LetterboxdSecretProtector.KeyDirectory ??= Path.Combine(dir, "letterboxd-keys");
            Load();
        }

        /// <summary>Settings for a user, or an empty object.</summary>
        public async Task<SerializdUserSettings> GetAsync(string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try { return _store.Users.TryGetValue(userId, out var s) ? Clone(s) : new SerializdUserSettings(); }
            finally { _lock.Release(); }
        }

        /// <summary>Snapshot of every user, deep-copied — these carry credentials.</summary>
        public async Task<Dictionary<string, SerializdUserSettings>> GetAllAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var copy = new Dictionary<string, SerializdUserSettings>(_store.Users.Count);
                foreach (var kv in _store.Users) copy[kv.Key] = Clone(kv.Value);
                return copy;
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Saves the account. A null password leaves the stored one alone so the
        /// UI can change other settings without round-tripping the secret;
        /// an empty string clears it.
        /// </summary>
        /// <returns>False when a supplied secret could not be encrypted.</returns>
        public async Task<bool> SetAccountAsync(
            string userId, string email, string? password, SerializdDirection direction,
            bool pushSeries, bool pushSeasons, bool pushEpisodes, bool pushReviews,
            string? username = null)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Users.TryGetValue(userId, out var s))
                {
                    s = new SerializdUserSettings();
                    _store.Users[userId] = s;
                }

                if (password != null)
                {
                    if (password.Length == 0) s.PasswordEnc = null;
                    else
                    {
                        var enc = LetterboxdSecretProtector.Protect(password);
                        if (enc == null) return false;   // never store plaintext
                        s.PasswordEnc = enc;
                    }
                }

                s.Email        = (email ?? string.Empty).Trim();
                s.Direction    = direction;
                s.PushSeries   = pushSeries;
                s.PushSeasons  = pushSeasons;
                s.PushEpisodes = pushEpisodes;
                s.PushReviews  = pushReviews;
                if (username != null) s.Username = username.Trim();

                await SaveAsync().ConfigureAwait(false);
                return true;
            }
            finally { _lock.Release(); }
        }

        /// <summary>Records the outcome of an import run.</summary>
        public async Task SetSyncStateAsync(string userId, DateTime? at, int imported, string? error)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Users.TryGetValue(userId, out var s))
                {
                    s = new SerializdUserSettings();
                    _store.Users[userId] = s;
                }
                s.LastSyncedAt      = at;
                s.LastImportedCount = imported;
                s.LastSyncError     = error;
                await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        /// <summary>Records the outcome of a push run.</summary>
        public async Task SetPushStateAsync(string userId, DateTime? at, int pushed, string? error, string? username = null)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Users.TryGetValue(userId, out var s))
                {
                    s = new SerializdUserSettings();
                    _store.Users[userId] = s;
                }
                s.LastPushedAt    = at;
                s.LastPushedCount = pushed;
                s.LastPushError   = error;
                if (!string.IsNullOrEmpty(username)) s.Username = username;
                await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// One copy helper rather than inline initialisers: on the Letterboxd
        /// side an inline copy meant every new setting silently read back as its
        /// default until someone remembered to add a line.
        /// </summary>
        private static SerializdUserSettings Clone(SerializdUserSettings s) => new()
        {
            Email           = s.Email,
            Username        = s.Username,
            PasswordEnc     = s.PasswordEnc,
            Direction       = s.Direction,
            PushSeries      = s.PushSeries,
            PushSeasons     = s.PushSeasons,
            PushEpisodes    = s.PushEpisodes,
            PushReviews     = s.PushReviews,
            LastSyncedAt      = s.LastSyncedAt,
            LastImportedCount = s.LastImportedCount,
            LastSyncError     = s.LastSyncError,
            LastPushedAt    = s.LastPushedAt,
            LastPushedCount = s.LastPushedCount,
            LastPushError   = s.LastPushError
        };

        private void Load()
        {
            try
            {
                if (!File.Exists(_filePath)) return;
                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json)) return;
                _store = JsonSerializer.Deserialize<SerializdStore>(json, _json) ?? new SerializdStore();
            }
            catch (Exception)
            {
                // A corrupt settings file must not break plugin startup.
                _store = new SerializdStore();
            }
        }

        // Caller holds _lock.
        private async Task SaveAsync()
        {
            var tmp = _filePath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(_store, _json)).ConfigureAwait(false);
            File.Move(tmp, _filePath, overwrite: true);
        }

        public void Dispose() => _lock.Dispose();
    }
}
