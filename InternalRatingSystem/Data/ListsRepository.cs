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
    /// JSON-backed store for collaborative user-curated film lists. File
    /// lives at &lt;jellyfin-data&gt;/data/InternalRating/lists.json.
    /// </summary>
    public sealed class ListsRepository : IDisposable
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private ListsStore _store = new();

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented             = true,
            PropertyNameCaseInsensitive = true
        };

        public ListsRepository(IApplicationPaths applicationPaths)
        {
            var dir = Path.Combine(applicationPaths.DataPath, "InternalRating");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "lists.json");
            Load();
        }

        public async Task<List<UserList>> GetAllAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try { return _store.Lists.Select(Clone).ToList(); }
            finally { _lock.Release(); }
        }

        public async Task<UserList?> GetByIdAsync(string listId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var l = _store.Lists.FirstOrDefault(x => x.Id == listId);
                return l == null ? null : Clone(l);
            }
            finally { _lock.Release(); }
        }

        public async Task<UserList> CreateAsync(string ownerId, string ownerName, string name, string? description, bool collaborative, bool isPrivate = false)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var list = new UserList
                {
                    Id            = Guid.NewGuid().ToString("N"),
                    OwnerId       = ownerId,
                    OwnerName     = ownerName,
                    Name          = name.Trim(),
                    Description   = description?.Trim(),
                    Collaborative = collaborative,
                    IsPrivate     = isPrivate,
                    CreatedAt     = DateTime.UtcNow,
                    Items         = new List<ListItem>()
                };
                _store.Lists.Add(list);
                await SaveAsync().ConfigureAwait(false);
                return Clone(list);
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Returns true if deleted. Only the list owner can delete.
        /// </summary>
        public async Task<bool> DeleteAsync(string listId, string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var l = _store.Lists.FirstOrDefault(x => x.Id == listId);
                if (l == null || l.OwnerId != userId) return false;
                _store.Lists.Remove(l);
                await SaveAsync().ConfigureAwait(false);
                return true;
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Adds an item to the list. Any authenticated user may add to a
        /// collaborative list; only the owner can add to a non-collab one.
        /// Returns true if newly added, false if already present or denied.
        /// </summary>
        public async Task<bool> AddItemAsync(string listId, string userId, string userName, string itemId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var l = _store.Lists.FirstOrDefault(x => x.Id == listId);
                if (l == null) return false;
                if (!l.Collaborative && l.OwnerId != userId) return false;
                if (l.Items.Any(i => i.ItemId == itemId)) return false;
                l.Items.Add(new ListItem
                {
                    ItemId      = itemId,
                    AddedBy     = userId,
                    AddedByName = userName,
                    AddedAt     = DateTime.UtcNow
                });
                await SaveAsync().ConfigureAwait(false);
                return true;
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Removes an item from the list. The owner can remove anything;
        /// other users can only remove items they themselves added.
        /// </summary>
        public async Task<bool> RemoveItemAsync(string listId, string userId, string itemId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var l = _store.Lists.FirstOrDefault(x => x.Id == listId);
                if (l == null) return false;
                var entry = l.Items.FirstOrDefault(i => i.ItemId == itemId);
                if (entry == null) return false;
                if (l.OwnerId != userId && entry.AddedBy != userId) return false;
                l.Items.Remove(entry);
                await SaveAsync().ConfigureAwait(false);
                return true;
            }
            finally { _lock.Release(); }
        }

        private static UserList Clone(UserList src) => new UserList
        {
            Id = src.Id,
            OwnerId = src.OwnerId,
            OwnerName = src.OwnerName,
            Name = src.Name,
            Description = src.Description,
            Collaborative = src.Collaborative,
            IsPrivate = src.IsPrivate,
            CreatedAt = src.CreatedAt,
            Items = src.Items.Select(i => new ListItem
            {
                ItemId = i.ItemId,
                AddedBy = i.AddedBy,
                AddedByName = i.AddedByName,
                AddedAt = i.AddedAt
            }).ToList()
        };

        private void Load()
        {
            if (!File.Exists(_filePath)) return;
            try
            {
                var json = File.ReadAllText(_filePath);
                _store = JsonSerializer.Deserialize<ListsStore>(json, _jsonOptions) ?? new ListsStore();
            }
            catch { _store = new ListsStore(); }
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
