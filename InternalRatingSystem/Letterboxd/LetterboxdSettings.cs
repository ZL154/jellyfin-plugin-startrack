using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.InternalRating.Letterboxd
{
    // NOTE: every public property in this file has an explicit JsonPropertyName
    // attribute so the API responses stay camelCase even when Jellyfin's host
    // serializer is set to PascalCase (which it is by default in 10.11 — the
    // v1.1.4 diagnose button returned "undefined" in the UI because the plugin
    // relied on Jellyfin's naming policy and got PascalCase keys back instead
    // of camelCase).

    /// <summary>Per-user Letterboxd sync settings + state.</summary>
    public sealed class LetterboxdUserSettings
    {
        [JsonPropertyName("username")]           public string   Username       { get; set; } = string.Empty;
        [JsonPropertyName("enableAutoSync")]     public bool     EnableAutoSync { get; set; }
        [JsonPropertyName("lastSyncedGuid")]     public string?  LastSyncedGuid { get; set; }
        [JsonPropertyName("lastSyncedAt")]       public DateTime? LastSyncedAt  { get; set; }
        [JsonPropertyName("lastImportedCount")]  public int   LastImportedCount { get; set; }
        [JsonPropertyName("lastUnmatchedCount")] public int   LastUnmatchedCount { get; set; }
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
        [JsonPropertyName("imported")]          public int Imported    { get; set; }
        [JsonPropertyName("updated")]           public int Updated     { get; set; }
        [JsonPropertyName("unmatched")]         public int Unmatched   { get; set; }
        [JsonPropertyName("ambiguous")]         public int Ambiguous   { get; set; }
        [JsonPropertyName("skipped")]           public int Skipped     { get; set; }
        [JsonPropertyName("libraryMovieCount")] public int LibraryMovieCount { get; set; }
        [JsonPropertyName("unmatchedTitles")]   public List<string> UnmatchedTitles { get; set; } = new();
        [JsonPropertyName("error")]             public string? Error   { get; set; }

        // v1.2.0 — counts from the extended CSV import that also pulls
        // watchlist.csv, likes.csv, and diary.csv from the Letterboxd ZIP
        // in the same pass.
        [JsonPropertyName("watchlistAdded")]    public int WatchlistAdded    { get; set; }
        [JsonPropertyName("watchlistSkipped")]  public int WatchlistSkipped  { get; set; }
        [JsonPropertyName("likesAdded")]        public int LikesAdded        { get; set; }
        [JsonPropertyName("likesSkipped")]      public int LikesSkipped      { get; set; }
    }

    /// <summary>
    /// Diagnostic report returned from the Diagnose endpoint. Used by the
    /// Letterboxd settings UI to verify the library query is working and
    /// to show the user how titles look after normalization.
    /// </summary>
    public sealed class LetterboxdDiagnoseResult
    {
        [JsonPropertyName("libraryMovieCount")]       public int LibraryMovieCount { get; set; }
        [JsonPropertyName("uniqueNormalizedTitles")]  public int UniqueNormalizedTitles { get; set; }
        [JsonPropertyName("zombiesFiltered")]         public int ZombiesFiltered { get; set; }
        [JsonPropertyName("usedFallbackQuery")]       public bool UsedFallbackQuery { get; set; }
        [JsonPropertyName("sampleMovies")]            public List<SampleMovie> SampleMovies { get; set; } = new();
        [JsonPropertyName("error")]                   public string? Error { get; set; }
    }

    /// <summary>Result of the dead-ratings cleanup operation.</summary>
    public sealed class CleanupResult
    {
        [JsonPropertyName("deletedRatings")] public int DeletedRatings { get; set; }
        [JsonPropertyName("deletedItems")]   public int DeletedItems   { get; set; }
        [JsonPropertyName("totalItems")]     public int TotalItems     { get; set; }
        [JsonPropertyName("error")]          public string? Error      { get; set; }
    }

    public sealed class SampleMovie
    {
        [JsonPropertyName("originalTitle")]   public string OriginalTitle   { get; set; } = string.Empty;
        [JsonPropertyName("normalizedTitle")] public string NormalizedTitle { get; set; } = string.Empty;
        [JsonPropertyName("year")]            public int? Year { get; set; }
    }
}
