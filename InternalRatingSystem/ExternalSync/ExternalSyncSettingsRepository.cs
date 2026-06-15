using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.InternalRating.ExternalSync
{
    /// <summary>
    /// Persists per-user, per-provider external sync settings as JSON at
    /// &lt;jellyfin-data&gt;/data/InternalRating/external-sync.json.
    /// Uses a SemaphoreSlim lock and atomic write-then-rename (same pattern as
    /// LetterboxdSettingsRepository and RatingRepository).
    /// </summary>
    public sealed class ExternalSyncSettingsRepository : IDisposable
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private ExternalSyncStore _store = new();

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented               = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
        };

        public ExternalSyncSettingsRepository(IApplicationPaths applicationPaths)
        {
            var dir = Path.Combine(applicationPaths.DataPath, "InternalRating");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "external-sync.json");
            Load();
        }

        /// <summary>Returns a snapshot of the full store (all users, all providers).</summary>
        public async Task<ExternalSyncStore> GetAllAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Return a shallow copy so callers can't mutate internal state
                var copy = new ExternalSyncStore();
                foreach (var (userId, userSettings) in _store.Users)
                {
                    var userCopy = new ExternalSyncUserSettings();
                    foreach (var (providerKey, conn) in userSettings.Providers)
                        userCopy.Providers[providerKey] = conn;
                    copy.Users[userId] = userCopy;
                }
                return copy;
            }
            finally { _lock.Release(); }
        }

        /// <summary>Returns the connection for a specific user and provider, or null if not set.</summary>
        public async Task<ProviderConnection?> GetConnectionAsync(string userId, string providerKey)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_store.Users.TryGetValue(userId, out var userSettings)
                    && userSettings.Providers.TryGetValue(providerKey, out var conn))
                    return conn;
                return null;
            }
            finally { _lock.Release(); }
        }

        /// <summary>Saves (creates or replaces) the connection for a specific user and provider.</summary>
        public async Task SetConnectionAsync(string userId, string providerKey, ProviderConnection conn)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Users.TryGetValue(userId, out var userSettings))
                {
                    userSettings = new ExternalSyncUserSettings();
                    _store.Users[userId] = userSettings;
                }
                userSettings.Providers[providerKey] = conn;
                await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        /// <summary>Removes the connection for a specific user and provider. No-op if not found.</summary>
        public async Task RemoveConnectionAsync(string userId, string providerKey)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_store.Users.TryGetValue(userId, out var userSettings))
                {
                    userSettings.Providers.Remove(providerKey);
                    // Clean up the user entry if they have no remaining providers
                    if (userSettings.Providers.Count == 0)
                        _store.Users.Remove(userId);
                    await SaveAsync().ConfigureAwait(false);
                }
            }
            finally { _lock.Release(); }
        }

        private void Load()
        {
            if (!File.Exists(_filePath)) return;
            try
            {
                var json = File.ReadAllText(_filePath);
                _store = JsonSerializer.Deserialize<ExternalSyncStore>(json, _jsonOptions) ?? new ExternalSyncStore();
            }
            catch
            {
                _store = new ExternalSyncStore();
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
