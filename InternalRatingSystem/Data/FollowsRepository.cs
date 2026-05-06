using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.InternalRating.Data
{
    /// <summary>
    /// Stores per-user follow lists ("user A follows users B, C, D"). Reverse
    /// lookup (followers) is derived on read since follow lists are usually
    /// small enough that scanning is cheap and we avoid the consistency
    /// headache of keeping two indexes in sync on every mutation.
    /// </summary>
    public sealed class FollowsRepository : IDisposable
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private FollowsStore _store = new();

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public FollowsRepository(IApplicationPaths applicationPaths)
        {
            var dir = Path.Combine(applicationPaths.DataPath, "InternalRating");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "follows.json");
            Load();
        }

        public async Task<List<string>> GetFollowingAsync(string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return _store.Follows.TryGetValue(userId, out var list)
                    ? new List<string>(list)
                    : new List<string>();
            }
            finally { _lock.Release(); }
        }

        public async Task<List<string>> GetFollowersAsync(string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var followers = new List<string>();
                foreach (var kv in _store.Follows)
                {
                    if (kv.Value.Contains(userId, StringComparer.OrdinalIgnoreCase))
                        followers.Add(kv.Key);
                }
                return followers;
            }
            finally { _lock.Release(); }
        }

        public async Task<bool> IsFollowingAsync(string followerId, string followeeId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return _store.Follows.TryGetValue(followerId, out var list) &&
                       list.Contains(followeeId, StringComparer.OrdinalIgnoreCase);
            }
            finally { _lock.Release(); }
        }

        public async Task<bool> FollowAsync(string followerId, string followeeId)
        {
            if (string.Equals(followerId, followeeId, StringComparison.OrdinalIgnoreCase)) return false;
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Follows.TryGetValue(followerId, out var list))
                {
                    list = new List<string>();
                    _store.Follows[followerId] = list;
                }
                if (list.Contains(followeeId, StringComparer.OrdinalIgnoreCase)) return false;
                list.Add(followeeId);
                await SaveAsync().ConfigureAwait(false);
                return true;
            }
            finally { _lock.Release(); }
        }

        public async Task UnfollowAsync(string followerId, string followeeId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_store.Follows.TryGetValue(followerId, out var list))
                {
                    var idx = list.FindIndex(x => string.Equals(x, followeeId, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0)
                    {
                        list.RemoveAt(idx);
                        if (list.Count == 0) _store.Follows.Remove(followerId);
                        await SaveAsync().ConfigureAwait(false);
                    }
                }
            }
            finally { _lock.Release(); }
        }

        /// <summary>Bulk count helper for the Members card grid — one pass over the store.</summary>
        public async Task<(Dictionary<string, int> following, Dictionary<string, int> followers)> CountAllAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var following = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var followers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _store.Follows)
                {
                    following[kv.Key] = kv.Value.Count;
                    foreach (var followee in kv.Value)
                    {
                        followers.TryGetValue(followee, out var c);
                        followers[followee] = c + 1;
                    }
                }
                return (following, followers);
            }
            finally { _lock.Release(); }
        }

        private void Load()
        {
            if (!File.Exists(_filePath)) return;
            try
            {
                var json = File.ReadAllText(_filePath);
                _store = JsonSerializer.Deserialize<FollowsStore>(json, _jsonOptions) ?? new FollowsStore();
            }
            catch { _store = new FollowsStore(); }
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

    public sealed class FollowsStore
    {
        [JsonPropertyName("follows")]
        public Dictionary<string, List<string>> Follows { get; set; } = new();
    }
}
