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
    /// Item id → <see cref="ItemIdentity"/> for every item the stores
    /// reference. Jellyfin keeps its own per-user data attached across a file
    /// replacement by keying it on provider ids; this is the plugin's version
    /// of that record, kept in one place rather than beside every row in four
    /// stores.
    ///
    /// Stored at &lt;jellyfin-data&gt;/data/InternalRating/item-identities.json.
    /// </summary>
    public sealed class ItemIdentityLedger : IDisposable
    {
        private sealed class LedgerStore
        {
            [JsonPropertyName("items")]
            public Dictionary<string, ItemIdentity> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private readonly string _filePath;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private LedgerStore _store = new();

        private static readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public ItemIdentityLedger(IApplicationPaths applicationPaths)
            : this(Path.Combine(applicationPaths.DataPath, "InternalRating", "item-identities.json"))
        {
        }

        internal ItemIdentityLedger(string filePath)
        {
            _filePath = filePath;
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            Load();
        }

        public async Task<ItemIdentity?> GetAsync(string itemId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try { return _store.Items.TryGetValue(itemId, out var id) ? id : null; }
            finally { _lock.Release(); }
        }

        /// <summary>Snapshot of every recorded id.</summary>
        public async Task<Dictionary<string, ItemIdentity>> AllAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try { return new Dictionary<string, ItemIdentity>(_store.Items, StringComparer.OrdinalIgnoreCase); }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Records or refreshes an identity. Returns true when the file changed.
        /// A refresh matters: an item added by a scan has no provider ids until
        /// its metadata arrives, so the first record of it can be empty.
        /// </summary>
        public async Task<bool> RecordAsync(string itemId, ItemIdentity identity)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_store.Items.TryGetValue(itemId, out var cur) && Same(cur, identity))
                    return false;
                _store.Items[itemId] = identity;
                await SaveAsync().ConfigureAwait(false);
                return true;
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Records a batch in one write. A sweep over a large library records
        /// hundreds of identities the first time; one file write for all of
        /// them rather than one each. Returns how many changed.
        /// </summary>
        public async Task<int> RecordManyAsync(IEnumerable<KeyValuePair<string, ItemIdentity>> records)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var changed = 0;
                foreach (var kv in records)
                {
                    if (_store.Items.TryGetValue(kv.Key, out var cur) && Same(cur, kv.Value)) continue;
                    _store.Items[kv.Key] = kv.Value;
                    changed++;
                }
                if (changed > 0) await SaveAsync().ConfigureAwait(false);
                return changed;
            }
            finally { _lock.Release(); }
        }

        /// <summary>Moves a record to a new id, after the stores have moved.</summary>
        public async Task RenameAsync(string oldId, string newId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_store.Items.Remove(oldId, out var identity)) return;
                _store.Items[newId] = identity;
                await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        /// <summary>Drops records for ids no store references any more, so the file tracks the stores rather than growing forever.</summary>
        public async Task<int> PruneAsync(ISet<string> referencedIds)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var gone = _store.Items.Keys.Where(k => !referencedIds.Contains(k)).ToList();
                foreach (var k in gone) _store.Items.Remove(k);
                if (gone.Count > 0) await SaveAsync().ConfigureAwait(false);
                return gone.Count;
            }
            finally { _lock.Release(); }
        }

        private static bool Same(ItemIdentity a, ItemIdentity b)
        {
            if (a.Kind != b.Kind || a.Name != b.Name || a.Year != b.Year || a.SeasonNumber != b.SeasonNumber || a.EpisodeNumber != b.EpisodeNumber)
                return false;
            var ka = new HashSet<string>(a.Keys(), StringComparer.Ordinal);
            return ka.SetEquals(b.Keys());
        }

        private void Load()
        {
            if (!File.Exists(_filePath)) return;
            try
            {
                var json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<LedgerStore>(json, _json) ?? new LedgerStore();
                // The deserialiser builds a case-sensitive dictionary; ids are hex GUIDs that other stores compare loosely.
                _store = new LedgerStore { Items = new Dictionary<string, ItemIdentity>(loaded.Items, StringComparer.OrdinalIgnoreCase) };
            }
            catch { _store = new LedgerStore(); }
        }

        private async Task SaveAsync()
        {
            var json = JsonSerializer.Serialize(_store, _json);
            var tmp = _filePath + ".tmp";
            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
            File.Move(tmp, _filePath, overwrite: true);
        }

        public void Dispose() => _lock.Dispose();
    }
}
