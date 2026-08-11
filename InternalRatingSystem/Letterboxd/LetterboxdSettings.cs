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

    /// <summary>
    /// Which way ratings flow between StarTrack and Letterboxd.
    /// Mirrors ExternalSync's SyncDirection so the two panels behave alike.
    ///
    /// Import needs only a username (public RSS/CSV). Export needs the account
    /// password, because Letterboxd has no public write API — see
    /// <see cref="LetterboxdSecretProtector"/>.
    /// </summary>
    public enum LetterboxdDirection
    {
        /// <summary>No syncing.</summary>
        Off = 0,
        /// <summary>Letterboxd → StarTrack only. This is the classic behaviour and needs no password.</summary>
        ImportOnly = 1,
        /// <summary>StarTrack → Letterboxd only. Requires a linked account.</summary>
        ExportOnly = 2,
        /// <summary>Both directions, newer-wins on conflict. Requires a linked account.</summary>
        TwoWay = 3
    }

    /// <summary>Per-user Letterboxd sync settings + state.</summary>
    public sealed class LetterboxdUserSettings
    {
        [JsonPropertyName("username")]           public string   Username       { get; set; } = string.Empty;
        [JsonPropertyName("enableAutoSync")]     public bool     EnableAutoSync { get; set; }

        // ---- Write-back (v1.6.5) ------------------------------------------
        // Import has always worked off the public RSS feed and CSV exports.
        // Pushing back requires driving letterboxd.com as the signed-in user,
        // so these only matter once the user opts into an export direction.

        /// <summary>Direction of sync. Defaults to ImportOnly, which is exactly what pre-1.6.5 did.</summary>
        [JsonPropertyName("direction")]          public LetterboxdDirection Direction { get; set; } = LetterboxdDirection.ImportOnly;

        /// <summary>
        /// The account password, ENCRYPTED AT REST. Never returned by any API
        /// response — the DTOs expose <c>hasPassword</c> instead. Read it only
        /// through <see cref="LetterboxdSecretProtector.Unprotect"/>.
        /// </summary>
        [JsonPropertyName("passwordEnc")]        public string?  PasswordEnc    { get; set; }

        /// <summary>
        /// Optional raw Cookie header (needs <c>cf_clearance</c>) for installs
        /// where Cloudflare challenges the server. Also encrypted at rest — a
        /// live session cookie is as sensitive as the password.
        /// </summary>
        [JsonPropertyName("rawCookiesEnc")]      public string?  RawCookiesEnc  { get; set; }

        /// <summary>
        /// User-Agent to pair with <see cref="RawCookiesEnc"/>. Cloudflare pins
        /// cf_clearance to the exact UA that solved the challenge, so cookies
        /// copied from Chrome sent with a Firefox UA are rejected.
        /// </summary>
        [JsonPropertyName("userAgent")]          public string?  UserAgent      { get; set; }

        /// <summary>Push star ratings to Letterboxd.</summary>
        [JsonPropertyName("pushRatings")]        public bool     PushRatings    { get; set; } = true;

        /// <summary>Log watches as Letterboxd diary entries (with rewatch detection).</summary>
        [JsonPropertyName("pushWatched")]        public bool     PushWatched    { get; set; } = true;

        /// <summary>Mirror ♡ liked films to Letterboxd likes.</summary>
        [JsonPropertyName("pushLiked")]          public bool     PushLiked      { get; set; } = true;

        /// <summary>Post written reviews alongside the diary entry.</summary>
        [JsonPropertyName("pushReviews")]        public bool     PushReviews    { get; set; }

        // ---- Push state ----
        [JsonPropertyName("lastPushedAt")]       public DateTime? LastPushedAt   { get; set; }
        [JsonPropertyName("lastPushedCount")]    public int      LastPushedCount { get; set; }
        [JsonPropertyName("lastPushError")]      public string?  LastPushError   { get; set; }
        [JsonPropertyName("lastSyncedGuid")]     public string?  LastSyncedGuid { get; set; }
        [JsonPropertyName("lastSyncedAt")]       public DateTime? LastSyncedAt  { get; set; }
        [JsonPropertyName("lastImportedCount")]  public int   LastImportedCount { get; set; }
        [JsonPropertyName("lastUnmatchedCount")] public int   LastUnmatchedCount { get; set; }

        // HTTP caching headers captured on the last RSS fetch. Sent back as
        // If-None-Match / If-Modified-Since on the next poll so unchanged
        // feeds return 304 Not Modified — letting us poll every 10 minutes
        // for near-real-time detection without doing real work each time.
        [JsonPropertyName("rssETag")]            public string?  RssETag         { get; set; }
        [JsonPropertyName("rssLastModified")]    public string?  RssLastModified { get; set; }
        [JsonPropertyName("lastCheckedAt")]      public DateTime? LastCheckedAt  { get; set; }
    }

    /// <summary>Top-level storage wrapper: userId → settings.</summary>
    public sealed class LetterboxdStore
    {
        [JsonPropertyName("users")]
        public Dictionary<string, LetterboxdUserSettings> Users { get; set; } = new();
    }

    /// <summary>
    /// One row of the Letterboxd sync admin panel: a Jellyfin user joined
    /// with their current Letterboxd link, if any.
    /// </summary>
    public sealed class AdminUserLetterboxdDto
    {
        [JsonPropertyName("userId")]         public string  UserId         { get; set; } = string.Empty;
        [JsonPropertyName("userName")]       public string  UserName       { get; set; } = string.Empty;
        [JsonPropertyName("username")]       public string  Username       { get; set; } = string.Empty;
        [JsonPropertyName("enableAutoSync")] public bool    EnableAutoSync { get; set; }
        [JsonPropertyName("lastSyncedAt")]   public DateTime? LastSyncedAt { get; set; }
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

        // True when the conditional GET returned 304 Not Modified — feed
        // hadn't changed since last poll, so no work was done. Used by the
        // scheduled task to skip logging "imported 0".
        [JsonPropertyName("notModified")]       public bool NotModified      { get; set; }
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
