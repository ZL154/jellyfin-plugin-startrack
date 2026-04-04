using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Models;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.InternalRating.Data
{
    /// <summary>
    /// Stores ratings as a JSON file in Jellyfin's data directory.
    /// No external dependencies required — uses only System.Text.Json (built into .NET 8).
    /// File location: &lt;jellyfin-data&gt;/data/InternalRating/ratings.json
    /// </summary>
    public sealed class RatingRepository : IDisposable
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private RatingsStore _store = new();

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented             = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition    = JsonIgnoreCondition.WhenWritingNull
        };

        public RatingRepository(IApplicationPaths applicationPaths)
        {
            var dir = Path.Combine(applicationPaths.DataPath, "InternalRating");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "ratings.json");
            Load();
        }

        // ------------------------------------------------------------------ //
        // Public API
        // ------------------------------------------------------------------ //

        /// <summary>Returns the average rating and all individual scores for an item.</summary>
        public async Task<RatingsResponse> GetRatingsAsync(string itemId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var ratings = GetItemRatings(itemId);
                return BuildResponse(itemId, ratings);
            }
            finally { _lock.Release(); }
        }

        /// <summary>Inserts or replaces a user's rating for an item.</summary>
        public async Task SaveRatingAsync(string itemId, string userId, string userName, double stars)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var ratings = GetItemRatings(itemId);
                var existing = ratings.FirstOrDefault(r => r.UserId == userId);
                if (existing != null) ratings.Remove(existing);

                ratings.Add(new StoredRating
                {
                    UserId   = userId,
                    UserName = userName,
                    Stars    = stars,
                    RatedAt  = DateTime.UtcNow
                });

                _store.Ratings[itemId] = ratings;
                await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        /// <summary>Removes a user's rating for an item.</summary>
        public async Task DeleteRatingAsync(string itemId, string userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var ratings = GetItemRatings(itemId);
                ratings.RemoveAll(r => r.UserId == userId);

                if (ratings.Count == 0)
                    _store.Ratings.Remove(itemId);
                else
                    _store.Ratings[itemId] = ratings;

                await SaveAsync().ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        /// <summary>Server-wide statistics.</summary>
        public (int TotalItems, int TotalRatings) GetStats()
        {
            _lock.Wait();
            try
            {
                var totalItems   = _store.Ratings.Count;
                var totalRatings = _store.Ratings.Values.Sum(v => v.Count);
                return (totalItems, totalRatings);
            }
            finally { _lock.Release(); }
        }

        // ------------------------------------------------------------------ //
        // Private helpers
        // ------------------------------------------------------------------ //

        private List<StoredRating> GetItemRatings(string itemId)
        {
            return _store.Ratings.TryGetValue(itemId, out var list)
                ? list
                : new List<StoredRating>();
        }

        private static RatingsResponse BuildResponse(string itemId, List<StoredRating> ratings)
        {
            var avg = ratings.Count > 0
                ? Math.Round(ratings.Average(r => r.Stars), 1)
                : 0.0;

            return new RatingsResponse
            {
                ItemId        = itemId,
                AverageRating = avg,
                TotalRatings  = ratings.Count,
                UserRatings   = ratings
                    .OrderByDescending(r => r.RatedAt)
                    .Select(r => new UserRatingDto
                    {
                        UserId   = r.UserId,
                        UserName = r.UserName,
                        Stars    = r.Stars,
                        RatedAt  = r.RatedAt
                    })
                    .ToList()
            };
        }

        private void Load()
        {
            if (!File.Exists(_filePath)) return;
            try
            {
                var json = File.ReadAllText(_filePath);
                _store = JsonSerializer.Deserialize<RatingsStore>(json, _jsonOptions)
                         ?? new RatingsStore();
            }
            catch
            {
                _store = new RatingsStore();
            }
        }

        private async Task SaveAsync()
        {
            var json = JsonSerializer.Serialize(_store, _jsonOptions);
            await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
        }

        public void Dispose() => _lock.Dispose();

        // ------------------------------------------------------------------ //
        // Internal storage models (not exposed via API)
        // ------------------------------------------------------------------ //

        private sealed class RatingsStore
        {
            [JsonPropertyName("ratings")]
            public Dictionary<string, List<StoredRating>> Ratings { get; set; } = new();
        }

        private sealed class StoredRating
        {
            [JsonPropertyName("userId")]   public string UserId   { get; set; } = string.Empty;
            [JsonPropertyName("userName")] public string UserName { get; set; } = string.Empty;
            [JsonPropertyName("stars")]    public double Stars    { get; set; }
            [JsonPropertyName("ratedAt")]  public DateTime RatedAt { get; set; }
        }
    }
}
