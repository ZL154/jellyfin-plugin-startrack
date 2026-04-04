using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.InternalRating.Models
{
    /// <summary>Response payload containing all ratings for an item.</summary>
    public class RatingsResponse
    {
        [JsonPropertyName("itemId")]
        public string ItemId { get; set; } = string.Empty;

        [JsonPropertyName("averageRating")]
        public double AverageRating { get; set; }

        [JsonPropertyName("totalRatings")]
        public int TotalRatings { get; set; }

        [JsonPropertyName("userRatings")]
        public List<UserRatingDto> UserRatings { get; set; } = new();
    }

    /// <summary>Individual user rating entry returned to the client.</summary>
    public class UserRatingDto
    {
        [JsonPropertyName("userId")]
        public string UserId { get; set; } = string.Empty;

        [JsonPropertyName("userName")]
        public string UserName { get; set; } = string.Empty;

        [JsonPropertyName("stars")]
        public double Stars { get; set; }

        [JsonPropertyName("review")]
        public string? Review { get; set; }

        [JsonPropertyName("ratedAt")]
        public DateTime RatedAt { get; set; }
    }

    /// <summary>A single entry in the recent-activity feed.</summary>
    public class RecentRatingDto
    {
        [JsonPropertyName("itemId")]
        public string ItemId { get; set; } = string.Empty;

        [JsonPropertyName("userId")]
        public string UserId { get; set; } = string.Empty;

        [JsonPropertyName("userName")]
        public string UserName { get; set; } = string.Empty;

        [JsonPropertyName("stars")]
        public double Stars { get; set; }

        [JsonPropertyName("review")]
        public string? Review { get; set; }

        [JsonPropertyName("ratedAt")]
        public DateTime RatedAt { get; set; }
    }
}
