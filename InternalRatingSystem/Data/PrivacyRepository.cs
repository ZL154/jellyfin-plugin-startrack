using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.InternalRating.Data
{
    /// <summary>
    /// Per-user privacy settings (hide from Members tab, hide follower count).
    /// Server-side so the same setting applies on every browser the user logs
    /// in from, and so the server can actually enforce visibility — not just
    /// the user's own UI.
    /// Stored as JSON at &lt;jellyfin-data&gt;/data/InternalRating/privacy.json.
    /// </summary>
    public sealed class PrivacyRepository : IDisposable
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private PrivacyStore _store = new();

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public PrivacyRepository(IApplicationPaths applicationPaths)
        {
            var dir = Path.Combine(applicationPaths.DataPath, "InternalRating");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "privacy.json");
            Load();
        }

        public async Task<PrivacySettings> GetAsync(string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return _store.Users.TryGetValue(userId, out var s)
                    ? new PrivacySettings
                    {
                        HideFromMembers   = s.HideFromMembers,
                        HideFollowerCount = s.HideFollowerCount,
                        HideFollowing     = s.HideFollowing,
                        HideStats         = s.HideStats,
                        HideActivity      = s.HideActivity
                    }
                    : new PrivacySettings();
            }
            finally { _lock.Release(); }
        }

        /// <summary>Returns the full set so callers (Members list) can filter in one pass.</summary>
        public async Task<HashSet<string>> GetHiddenUserIdsAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _store.Users)
                    if (kv.Value.HideFromMembers) set.Add(kv.Key);
                return set;
            }
            finally { _lock.Release(); }
        }

        public async Task<bool> IsFollowerCountHiddenAsync(string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return _store.Users.TryGetValue(userId, out var s) && s.HideFollowerCount;
            }
            finally { _lock.Release(); }
        }

        public async Task<bool> IsFollowingHiddenAsync(string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return _store.Users.TryGetValue(userId, out var s) && s.HideFollowing;
            }
            finally { _lock.Release(); }
        }

        public async Task<bool> IsStatsHiddenAsync(string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return _store.Users.TryGetValue(userId, out var s) && s.HideStats;
            }
            finally { _lock.Release(); }
        }

        public async Task<bool> IsActivityHiddenAsync(string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return _store.Users.TryGetValue(userId, out var s) && s.HideActivity;
            }
            finally { _lock.Release(); }
        }

        /// <summary>One pass to fetch every user with HideActivity set.</summary>
        public async Task<HashSet<string>> GetActivityHiddenIdsAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _store.Users)
                    if (kv.Value.HideActivity) set.Add(kv.Key);
                return set;
            }
            finally { _lock.Release(); }
        }

        public async Task SetAsync(string userId, bool hideFromMembers, bool hideFollowerCount, bool hideFollowing = false, bool hideStats = false, bool hideActivity = false)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Users.TryGetValue(userId, out var s))
                {
                    s = new PrivacySettings();
                    _store.Users[userId] = s;
                }
                s.HideFromMembers = hideFromMembers;
                s.HideFollowerCount = hideFollowerCount;
                s.HideFollowing = hideFollowing;
                s.HideStats = hideStats;
                s.HideActivity = hideActivity;
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
                _store = JsonSerializer.Deserialize<PrivacyStore>(json, _jsonOptions) ?? new PrivacyStore();
            }
            catch { _store = new PrivacyStore(); }
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

    public sealed class PrivacySettings
    {
        [JsonPropertyName("hideFromMembers")]   public bool HideFromMembers   { get; set; }
        [JsonPropertyName("hideFollowerCount")] public bool HideFollowerCount { get; set; }
        [JsonPropertyName("hideFollowing")]     public bool HideFollowing     { get; set; }
        [JsonPropertyName("hideStats")]         public bool HideStats         { get; set; }
        [JsonPropertyName("hideActivity")]      public bool HideActivity      { get; set; }
    }

    public sealed class PrivacyStore
    {
        [JsonPropertyName("users")]
        public Dictionary<string, PrivacySettings> Users { get; set; } = new();
    }
}
