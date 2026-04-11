using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.InternalRating.Models
{
    /// <summary>
    /// Per-user non-rating data: watchlist, liked films, top-4 favorites.
    /// Stored as a single JSON file keyed by userId so the whole state for
    /// a user loads in one go.
    /// </summary>
    public sealed class UserInteractions
    {
        [JsonPropertyName("watchlist")] public List<InteractionEntry> Watchlist { get; set; } = new();
        [JsonPropertyName("liked")]     public List<InteractionEntry> Liked     { get; set; } = new();
        [JsonPropertyName("favorites")] public List<string>           Favorites { get; set; } = new(); // ordered top-4 itemIds
    }

    public sealed class InteractionEntry
    {
        [JsonPropertyName("itemId")]  public string   ItemId  { get; set; } = string.Empty;
        [JsonPropertyName("addedAt")] public DateTime AddedAt { get; set; }
    }

    /// <summary>Top-level JSON wrapper.</summary>
    public sealed class UserInteractionsStore
    {
        [JsonPropertyName("users")]
        public Dictionary<string, UserInteractions> Users { get; set; } = new();
    }

    // ============================ API DTOs ============================ //

    /// <summary>Row returned by the watchlist list endpoint.</summary>
    public sealed class WatchlistEntryDto
    {
        [JsonPropertyName("itemId")]  public string   ItemId  { get; set; } = string.Empty;
        [JsonPropertyName("addedAt")] public DateTime AddedAt { get; set; }
    }

    /// <summary>Aggregated entry from the everyones-watchlist endpoint.</summary>
    public sealed class EveryonesWatchlistEntry
    {
        [JsonPropertyName("itemId")]        public string         ItemId       { get; set; } = string.Empty;
        [JsonPropertyName("firstAddedAt")]  public DateTime       FirstAddedAt { get; set; }
        [JsonPropertyName("userIds")]       public List<string>   UserIds      { get; set; } = new();
        [JsonPropertyName("userNames")]     public List<string>   UserNames    { get; set; } = new();
    }

    /// <summary>Row returned by the liked list endpoint.</summary>
    public sealed class LikedEntryDto
    {
        [JsonPropertyName("itemId")] public string   ItemId { get; set; } = string.Empty;
        [JsonPropertyName("likedAt")] public DateTime LikedAt { get; set; }
    }

    /// <summary>
    /// Combined per-item status returned by /Interactions/{itemId} so the
    /// rating pill can render heart/bookmark state in a single fetch
    /// alongside the existing rating call.
    /// </summary>
    public sealed class InteractionStatusDto
    {
        [JsonPropertyName("watchlisted")] public bool Watchlisted { get; set; }
        [JsonPropertyName("liked")]       public bool Liked       { get; set; }
        [JsonPropertyName("favorite")]    public bool Favorite    { get; set; }
        [JsonPropertyName("favoriteSlot")] public int FavoriteSlot { get; set; } = -1;
    }

    /// <summary>
    /// Import delta returned when a Letterboxd ZIP is processed. Extends
    /// the existing LetterboxdImportResult with counts for the new data
    /// types so the UI can show a single "imported everything" summary.
    /// </summary>
    public sealed class InteractionImportDelta
    {
        [JsonPropertyName("watchlistAdded")] public int WatchlistAdded { get; set; }
        [JsonPropertyName("watchlistSkipped")] public int WatchlistSkipped { get; set; }
        [JsonPropertyName("likesAdded")]     public int LikesAdded     { get; set; }
        [JsonPropertyName("likesSkipped")]   public int LikesSkipped   { get; set; }
        [JsonPropertyName("diaryUpdated")]   public int DiaryUpdated   { get; set; }
    }
}
