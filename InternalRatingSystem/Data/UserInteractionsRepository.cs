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
        /// Replaces the favorites list for a user (max 4). Empty slots are
        /// filtered out so duplicates/nulls never persist.
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
                    .Take(4)
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
            await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
        }

        public void Dispose() => _lock.Dispose();
    }
}
