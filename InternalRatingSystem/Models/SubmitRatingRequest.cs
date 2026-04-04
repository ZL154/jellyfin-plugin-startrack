using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.InternalRating.Models
{
    /// <summary>
    /// Request body for submitting a rating.
    /// </summary>
    public class SubmitRatingRequest
    {
        /// <summary>
        /// Star rating between 1 and 5 (whole numbers or half stars).
        /// </summary>
        [Required]
        [Range(0.5, 5)]
        [JsonPropertyName("stars")]
        public double Stars { get; set; }
    }
}
