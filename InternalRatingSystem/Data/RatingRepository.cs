using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.InternalRating.Data
{
    /// <summary>
    /// Manages persistent storage of user ratings using SQLite.
    /// Database is stored in Jellyfin's data directory under InternalRating/ratings.db
    /// </summary>
    public class RatingRepository : IDisposable
    {
        private readonly string _dbPath;
        private bool _disposed;

        public RatingRepository(IApplicationPaths applicationPaths)
        {
            var dataDir = Path.Combine(applicationPaths.DataPath, "InternalRating");
            Directory.CreateDirectory(dataDir);
            _dbPath = Path.Combine(dataDir, "ratings.db");
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Ratings (
                    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    ItemId    TEXT    NOT NULL,
                    UserId    TEXT    NOT NULL,
                    UserName  TEXT    NOT NULL,
                    Stars     REAL    NOT NULL,
                    RatedAt   TEXT    NOT NULL,
                    UNIQUE(ItemId, UserId)
                );
                CREATE INDEX IF NOT EXISTS idx_ratings_itemid ON Ratings(ItemId);
            ";
            cmd.ExecuteNonQuery();
        }

        private SqliteConnection CreateConnection()
        {
            var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            return conn;
        }

        /// <summary>
        /// Returns all ratings for an item plus computed average.
        /// </summary>
        public async Task<RatingsResponse> GetRatingsAsync(string itemId)
        {
            var ratings = new List<UserRatingDto>();

            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT UserId, UserName, Stars, RatedAt
                FROM   Ratings
                WHERE  ItemId = @itemId
                ORDER  BY RatedAt DESC
            ";
            cmd.Parameters.AddWithValue("@itemId", itemId);

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                ratings.Add(new UserRatingDto
                {
                    UserId   = reader.GetString(0),
                    UserName = reader.GetString(1),
                    Stars    = reader.GetDouble(2),
                    RatedAt  = DateTime.Parse(reader.GetString(3))
                });
            }

            var avg = ratings.Count > 0
                ? Math.Round(ratings.Average(r => r.Stars), 1)
                : 0.0;

            return new RatingsResponse
            {
                ItemId        = itemId,
                AverageRating = avg,
                TotalRatings  = ratings.Count,
                UserRatings   = ratings
            };
        }

        /// <summary>
        /// Inserts or updates a user's rating for an item.
        /// </summary>
        public async Task SaveRatingAsync(string itemId, string userId, string userName, double stars)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Ratings (ItemId, UserId, UserName, Stars, RatedAt)
                VALUES (@itemId, @userId, @userName, @stars, @ratedAt)
                ON CONFLICT(ItemId, UserId) DO UPDATE SET
                    Stars    = excluded.Stars,
                    UserName = excluded.UserName,
                    RatedAt  = excluded.RatedAt
            ";
            cmd.Parameters.AddWithValue("@itemId",   itemId);
            cmd.Parameters.AddWithValue("@userId",   userId);
            cmd.Parameters.AddWithValue("@userName", userName);
            cmd.Parameters.AddWithValue("@stars",    stars);
            cmd.Parameters.AddWithValue("@ratedAt",  DateTime.UtcNow.ToString("O"));

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Removes a user's rating for an item.
        /// </summary>
        public async Task DeleteRatingAsync(string itemId, string userId)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Ratings WHERE ItemId = @itemId AND UserId = @userId";
            cmd.Parameters.AddWithValue("@itemId", itemId);
            cmd.Parameters.AddWithValue("@userId", userId);

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Returns total number of rated items and total ratings across the server.
        /// </summary>
        public (int TotalItems, int TotalRatings) GetStats()
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(DISTINCT ItemId), COUNT(*) FROM Ratings";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
                return (reader.GetInt32(0), reader.GetInt32(1));
            return (0, 0);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }
    }
}
