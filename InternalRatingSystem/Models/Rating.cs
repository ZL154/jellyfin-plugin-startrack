using System;

namespace Jellyfin.Plugin.InternalRating.Models
{
    /// <summary>
    /// Represents a single user rating stored in the database.
    /// </summary>
    public class Rating
    {
        public int Id { get; set; }
        public string ItemId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public double Stars { get; set; }
        public DateTime RatedAt { get; set; }
    }
}
