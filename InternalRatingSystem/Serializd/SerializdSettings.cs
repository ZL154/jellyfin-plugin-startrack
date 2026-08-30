using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.InternalRating.Serializd
{
    // Every property carries an explicit JsonPropertyName. Jellyfin 10.11's host
    // serializer defaults to PascalCase, and a DTO without these comes back with
    // capitalised keys the widget cannot read — see the note atop
    // LetterboxdSettings.cs and the bug it caused in LetterboxdPushResult.

    /// <summary>
    /// Which way TV ratings flow between StarTrack and Serializd.
    /// Deliberately mirrors <c>LetterboxdDirection</c> so the two panels read alike.
    /// </summary>
    public enum SerializdDirection
    {
        /// <summary>No syncing.</summary>
        Off = 0,
        /// <summary>StarTrack → Serializd only.</summary>
        ExportOnly = 1
    }

    /// <summary>
    /// Per-user Serializd settings.
    ///
    /// Serializd is TV-only, which is exactly why it is worth having alongside
    /// Letterboxd: Letterboxd is films-only, so between them StarTrack can mirror
    /// a whole library. Nothing here touches movies.
    /// </summary>
    public sealed class SerializdUserSettings
    {
        /// <summary>Account email. Serializd logs in by email, not username.</summary>
        [JsonPropertyName("email")]          public string  Email     { get; set; } = string.Empty;

        /// <summary>Username as reported by the login response, for display only.</summary>
        [JsonPropertyName("username")]       public string? Username  { get; set; }

        /// <summary>
        /// Password, ENCRYPTED AT REST via the plugin's Data Protection key ring.
        /// Never returned by any API response — DTOs expose <c>hasPassword</c>.
        /// </summary>
        [JsonPropertyName("passwordEnc")]    public string? PasswordEnc { get; set; }

        /// <summary>Direction. Off by default: this does nothing until asked.</summary>
        [JsonPropertyName("direction")]      public SerializdDirection Direction { get; set; } = SerializdDirection.Off;

        /// <summary>Push star ratings for series.</summary>
        [JsonPropertyName("pushSeries")]     public bool PushSeries   { get; set; } = true;

        /// <summary>Push star ratings for individual episodes.</summary>
        [JsonPropertyName("pushEpisodes")]   public bool PushEpisodes { get; set; }

        /// <summary>Include written reviews. Serializd only stores text on a LOG entry.</summary>
        [JsonPropertyName("pushReviews")]    public bool PushReviews  { get; set; }

        // ---- state ----
        [JsonPropertyName("lastPushedAt")]    public DateTime? LastPushedAt   { get; set; }
        [JsonPropertyName("lastPushedCount")] public int       LastPushedCount { get; set; }
        [JsonPropertyName("lastPushError")]   public string?   LastPushError  { get; set; }
    }

    /// <summary>Top-level storage wrapper: userId → settings.</summary>
    public sealed class SerializdStore
    {
        [JsonPropertyName("users")]
        public Dictionary<string, SerializdUserSettings> Users { get; set; } = new();
    }
}
