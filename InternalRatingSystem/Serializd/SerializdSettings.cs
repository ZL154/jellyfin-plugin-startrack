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
        /// <summary>Serializd → StarTrack only. Needs a username and NO password.</summary>
        ImportOnly = 1,
        /// <summary>StarTrack → Serializd only. Requires email + password.</summary>
        ExportOnly = 2,
        /// <summary>Both directions. Requires email + password.</summary>
        TwoWay = 3
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
        /// <summary>Account email. Serializd logs in by email, not username. Export only.</summary>
        [JsonPropertyName("email")]          public string  Email     { get; set; } = string.Empty;

        /// <summary>
        /// Public Serializd username. This is all an IMPORT needs —
        /// /api/user/{username}/diary is unauthenticated — so the import half
        /// works with no password at all, exactly like Letterboxd's.
        /// A successful sign-in also fills this in for display.
        /// </summary>
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

        /// <summary>
        /// Push star ratings for whole seasons. On by default: a season is
        /// Serializd's native unit, the way a film is Letterboxd's, and about
        /// two thirds of a real Serializd diary is season entries.
        /// </summary>
        [JsonPropertyName("pushSeasons")]    public bool PushSeasons  { get; set; } = true;

        /// <summary>Push star ratings for individual episodes.</summary>
        [JsonPropertyName("pushEpisodes")]   public bool PushEpisodes { get; set; }

        /// <summary>Include written reviews. Serializd only stores text on a LOG entry.</summary>
        [JsonPropertyName("pushReviews")]    public bool PushReviews  { get; set; }

        // ---- state ----

        /// <summary>Set when the import last ran without error.</summary>
        [JsonPropertyName("lastSyncedAt")]    public DateTime? LastSyncedAt   { get; set; }

        /// <summary>Ratings written by the last import.</summary>
        [JsonPropertyName("lastImportedCount")] public int     LastImportedCount { get; set; }

        /// <summary>Why the last import failed, if it did.</summary>
        [JsonPropertyName("lastSyncError")]   public string?   LastSyncError  { get; set; }

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
