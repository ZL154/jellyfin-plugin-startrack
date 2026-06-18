using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.InternalRating.Models
{
    /// <summary>Request body for submitting a rating.</summary>
    public class SubmitRatingRequest
    {
        /// <summary>Star rating 0.5–5 in 0.5 increments.</summary>
        [Required]
        [Range(0.5, 5)]
        [JsonPropertyName("stars")]
        public double Stars { get; set; }

        /// <summary>Optional free-text review (ceiling 10000 chars; admin-configurable limit enforced in the controller).</summary>
        [MaxLength(10000)]
        [JsonPropertyName("review")]
        public string? Review { get; set; }
    }
}
