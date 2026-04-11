using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.InternalRating.Letterboxd
{
    /// <summary>Per-user Letterboxd sync settings + state.</summary>
    public sealed class LetterboxdUserSettings
    {
        [JsonPropertyName("username")]       public string   Username       { get; set; } = string.Empty;
        [JsonPropertyName("enableAutoSync")] public bool     EnableAutoSync { get; set; }
        [JsonPropertyName("lastSyncedGuid")] public string?  LastSyncedGuid { get; set; }
        [JsonPropertyName("lastSyncedAt")]   public DateTime? LastSyncedAt  { get; set; }
        [JsonPropertyName("lastImportedCount")] public int   LastImportedCount { get; set; }
        [JsonPropertyName("lastUnmatchedCount")] public int  LastUnmatchedCount { get; set; }
    }

    /// <summary>Top-level storage wrapper: userId → settings.</summary>
    public sealed class LetterboxdStore
    {
        [JsonPropertyName("users")]
        public Dictionary<string, LetterboxdUserSettings> Users { get; set; } = new();
    }

    /// <summary>Report returned by CSV import and RSS sync operations.</summary>
    public sealed class LetterboxdImportResult
    {
        public int Imported    { get; set; }
        public int Updated     { get; set; }
        public int Unmatched   { get; set; }
        public int Ambiguous   { get; set; }
        public int Skipped     { get; set; }
        public List<string> UnmatchedTitles { get; set; } = new();
        public string? Error   { get; set; }
    }
}
