using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.InternalRating.Models
{
    /// <summary>
    /// A single diary entry — one row per (userId, itemId, watchedAt).
    /// Unlike ratings (which are one-per-film-per-user), diary entries can
    /// have multiple rows for the same film to capture rewatches.
    /// </summary>
    public sealed class DiaryEntry
    {
        [JsonPropertyName("id")]        public string  Id        { get; set; } = string.Empty; // user-unique GUID
        [JsonPropertyName("itemId")]    public string  ItemId    { get; set; } = string.Empty;
        [JsonPropertyName("watchedAt")] public DateTime WatchedAt { get; set; }
        [JsonPropertyName("stars")]     public double? Stars     { get; set; } // null = watched but not rated
        [JsonPropertyName("review")]    public string? Review    { get; set; }
        [JsonPropertyName("rewatch")]   public bool    Rewatch   { get; set; }
    }

    /// <summary>Per-user diary: a list of diary entries newest-first.</summary>
    public sealed class UserDiary
    {
        [JsonPropertyName("entries")] public List<DiaryEntry> Entries { get; set; } = new();
    }

    /// <summary>Top-level store: userId → diary.</summary>
    public sealed class DiaryStore
    {
        [JsonPropertyName("users")]
        public Dictionary<string, UserDiary> Users { get; set; } = new();
    }

    /// <summary>Request DTO for manual diary entry creation (rewatches).</summary>
    public sealed class CreateDiaryEntryRequest
    {
        public string? ItemId    { get; set; }
        public DateTime? WatchedAt { get; set; }
        public double?  Stars     { get; set; }
        public string?  Review    { get; set; }
        public bool?    Rewatch   { get; set; }
    }
}
