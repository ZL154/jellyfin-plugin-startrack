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
    /// Stores per-user watchlist, liked films, and top-4 favorites as a
    /// single JSON file at &lt;jellyfin-data&gt;/data/InternalRating/user_interactions.json.
    /// Same SemaphoreSlim-guarded async pattern as <see cref="RatingRepository"/>.
    /// </summary>
    public sealed class UserInteractionsRepository : IDisposable
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private UserInteractionsStore _store = new();

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented             = true,
            PropertyNameCaseInsensitive = true
        };

        public UserInteractionsRepository(IApplicationPaths applicationPaths)
        {
            var dir = Path.Combine(applicationPaths.DataPath, "InternalRating");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "user_interactions.json");
            Load();
        }

        // ============================== Watchlist ================================ //

        public async Task<List<WatchlistEntryDto>> GetWatchlistAsync(string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var u = GetOrInit(userId);
                return u.Watchlist
                    .OrderByDescending(e => e.AddedAt)
                    .Select(e => new WatchlistEntryDto { ItemId = e.ItemId, AddedAt = e.AddedAt })
                    .ToList();
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Returns every user's watchlist aggregated into one list. Items
        /// that multiple users want collapse into a single entry with a
        /// list of which users want it.
        /// </summary>
        public async Task<List<EveryonesWatchlistEntry>> GetEveryonesWatchlistAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var byItem = new Dictionary<string, EveryonesWatchlistEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _store.Users)
                {
                    var userId = kv.Key;
                    var user   = kv.Value;
                    foreach (var entry in user.Watchlist)
                    {
                        if (!byItem.TryGetValue(entry.ItemId, out var agg))
                        {
                            agg = new EveryonesWatchlistEntry
                            {
                                ItemId = entry.ItemId,
                                FirstAddedAt = entry.AddedAt,
                                UserIds = new List<string>()
                            };
                            byItem[entry.ItemId] = agg;
                        }
                        if (entry.AddedAt < agg.FirstAddedAt) agg.FirstAddedAt = entry.AddedAt;
                        if (!agg.UserIds.Contains(userId)) agg.UserIds.Add(userId);
                    }
                }
                return byItem.Values
                    .OrderByDescending(e => e.UserIds.Count)
                    .ThenByDescending(e => e.FirstAddedAt)
                    .ToList();
            }
            finally { _lock.Release(); }
        }

        public async Task<bool> AddToWatchlistAsync(string userId, string itemId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var u = GetOrInit(userId);
                if (u.Watchlist.Any(e => e.ItemId == itemId)) return false;
                u.Watchlist.Add(new InteractionEntry { ItemId = itemId, AddedAt = DateTime.UtcNow });
                await SaveAsync().ConfigureAwait(false);
                return true;
            }
            finally { _lock.Release(); }
        }

        public async Task RemoveFromWatchlistAsync(string userId, string itemId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var u = GetOrInit(userId);
                u.Watchlist.RemoveAll(e => e.ItemId == itemId);
                await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        // ============================== Liked ==================================== //

        public async Task<List<LikedEntryDto>> GetLikedAsync(string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var u = GetOrInit(userId);
                return u.Liked
                    .OrderByDescending(e => e.AddedAt)
                    .Select(e => new LikedEntryDto { ItemId = e.ItemId, LikedAt = e.AddedAt })
                    .ToList();
            }
            finally { _lock.Release(); }
        }

        public async Task<bool> AddLikeAsync(string userId, string itemId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var u = GetOrInit(userId);
                if (u.Liked.Any(e => e.ItemId == itemId)) return false;
                u.Liked.Add(new InteractionEntry { ItemId = itemId, AddedAt = DateTime.UtcNow });
                await SaveAsync().ConfigureAwait(false);
                return true;
            }
            finally { _lock.Release(); }
        }

        public async Task RemoveLikeAsync(string userId, string itemId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var u = GetOrInit(userId);
                u.Liked.RemoveAll(e => e.ItemId == itemId);
                await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        // ============================== Favorites ================================ //

        public async Task<List<string>> GetFavoritesAsync(string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return new List<string>(GetOrInit(userId).Favorites);
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Replaces the favorites list for a user. v1.3.2 raised the cap
        /// from 4 to 12 so the same list can hold a Top 4 for movies, a
        /// Top 4 for series, and a Top 4 for episodes (the client groups
        /// by type when rendering). Empty/duplicate entries are filtered.
        /// </summary>
        public async Task SetFavoritesAsync(string userId, IEnumerable<string> itemIds)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var u = GetOrInit(userId);
                u.Favorites = itemIds
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct()
                    .Take(12)
                    .ToList();
                await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        // ============================== Combined status ========================== //

        public async Task<InteractionStatusDto> GetStatusAsync(string userId, string itemId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var u = GetOrInit(userId);
                var slot = u.Favorites.IndexOf(itemId);
                return new InteractionStatusDto
                {
                    Watchlisted = u.Watchlist.Any(e => e.ItemId == itemId),
                    Liked       = u.Liked.Any(e => e.ItemId == itemId),
                    Favorite    = slot >= 0,
                    FavoriteSlot = slot
                };
            }
            finally { _lock.Release(); }
        }

        // ============================== Private helpers ========================== //

        private UserInteractions GetOrInit(string userId)
        {
            if (!_store.Users.TryGetValue(userId, out var u))
            {
                u = new UserInteractions();
                _store.Users[userId] = u;
            }
            return u;
        }

        private void Load()
        {
            if (!File.Exists(_filePath)) return;
            try
            {
                var json = File.ReadAllText(_filePath);
                _store = JsonSerializer.Deserialize<UserInteractionsStore>(json, _jsonOptions) ?? new UserInteractionsStore();
            }
            catch
            {
                _store = new UserInteractionsStore();
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
