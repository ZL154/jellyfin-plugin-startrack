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
            Load();
        }

        /// <summary>Returns a copy of the settings for a user, or an empty object.</summary>
        public async Task<LetterboxdUserSettings> GetAsync(string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return _store.Users.TryGetValue(userId, out var s)
                    ? new LetterboxdUserSettings
                    {
                        Username           = s.Username,
                        EnableAutoSync     = s.EnableAutoSync,
                        LastSyncedGuid     = s.LastSyncedGuid,
                        LastSyncedAt       = s.LastSyncedAt,
                        LastImportedCount  = s.LastImportedCount,
                        LastUnmatchedCount = s.LastUnmatchedCount
                    }
                    : new LetterboxdUserSettings();
            }
            finally { _lock.Release(); }
        }

        /// <summary>Returns a snapshot of every user with Letterboxd settings.</summary>
        public async Task<Dictionary<string, LetterboxdUserSettings>> GetAllAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return new Dictionary<string, LetterboxdUserSettings>(_store.Users);
            }
            finally { _lock.Release(); }
        }

        /// <summary>Sets username + auto-sync toggle. Preserves sync state fields.</summary>
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
                s.Username       = (username ?? string.Empty).Trim();
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
            await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
        }

        public void Dispose() => _lock.Dispose();
    }
}
