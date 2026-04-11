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
    /// JSON-backed per-user diary. Multiple entries per film are allowed
    /// (rewatches). File lives at
    /// &lt;jellyfin-data&gt;/data/InternalRating/diary.json.
    /// </summary>
    public sealed class DiaryRepository : IDisposable
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private DiaryStore _store = new();

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented             = true,
            PropertyNameCaseInsensitive = true
        };

        public DiaryRepository(IApplicationPaths applicationPaths)
        {
            var dir = Path.Combine(applicationPaths.DataPath, "InternalRating");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "diary.json");
            Load();
        }

        public async Task<List<DiaryEntry>> GetEntriesAsync(string userId, int limit = 10000)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var d = GetOrInit(userId);
                return d.Entries
                    .OrderByDescending(e => e.WatchedAt)
                    .Take(limit)
                    .Select(e => new DiaryEntry
                    {
                        Id        = e.Id,
                        ItemId    = e.ItemId,
                        WatchedAt = e.WatchedAt,
                        Stars     = e.Stars,
                        Review    = e.Review,
                        Rewatch   = e.Rewatch
                    })
                    .ToList();
            }
            finally { _lock.Release(); }
        }

        public async Task<DiaryEntry> AddEntryAsync(string userId, DiaryEntry entry)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var d = GetOrInit(userId);
                if (string.IsNullOrEmpty(entry.Id)) entry.Id = Guid.NewGuid().ToString("N");
                if (entry.WatchedAt == default) entry.WatchedAt = DateTime.UtcNow;
                d.Entries.Add(entry);
                await SaveAsync().ConfigureAwait(false);
                return entry;
            }
            finally { _lock.Release(); }
        }

        public async Task DeleteEntryAsync(string userId, string entryId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var d = GetOrInit(userId);
                d.Entries.RemoveAll(e => e.Id == entryId);
                await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Used by the Letterboxd diary.csv importer. Dedupes on
        /// (itemId, watchedAt) so reimporting the same CSV doesn't
        /// duplicate every entry.
        /// </summary>
        public async Task<int> ImportEntriesAsync(string userId, IEnumerable<DiaryEntry> entries)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var d = GetOrInit(userId);
                var existing = new HashSet<string>(
                    d.Entries.Select(e => e.ItemId + "|" + e.WatchedAt.ToString("yyyy-MM-dd")),
                    StringComparer.OrdinalIgnoreCase);
                int added = 0;
                foreach (var e in entries)
                {
                    var key = e.ItemId + "|" + e.WatchedAt.ToString("yyyy-MM-dd");
                    if (existing.Contains(key)) continue;
                    if (string.IsNullOrEmpty(e.Id)) e.Id = Guid.NewGuid().ToString("N");
                    d.Entries.Add(e);
                    existing.Add(key);
                    added++;
                }
                await SaveAsync().ConfigureAwait(false);
                return added;
            }
            finally { _lock.Release(); }
        }

        private UserDiary GetOrInit(string userId)
        {
            if (!_store.Users.TryGetValue(userId, out var d))
            {
                d = new UserDiary();
                _store.Users[userId] = d;
            }
            return d;
        }

        private void Load()
        {
            if (!File.Exists(_filePath)) return;
            try
            {
                var json = File.ReadAllText(_filePath);
                _store = JsonSerializer.Deserialize<DiaryStore>(json, _jsonOptions) ?? new DiaryStore();
            }
            catch { _store = new DiaryStore(); }
        }

        private async Task SaveAsync()
        {
            var json = JsonSerializer.Serialize(_store, _jsonOptions);
            await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
        }

        public void Dispose() => _lock.Dispose();
    }
}
