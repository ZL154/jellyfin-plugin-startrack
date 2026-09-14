using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.InternalRating
{
    /// <summary>
    /// Plugin configuration for StarTrack.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        // ---- Language ---------------------------------------------------- //

        /// <summary>Default UI language when a user has not picked their own.</summary>
        public string Language { get; set; } = "en";

        // ---- Floating-button visibility --------------------------------- //

        /// <summary>Hide the floating 'Recent' button system-wide.</summary>
        public bool HideRecentButton { get; set; } = false;

        /// <summary>Hide the floating 'Letterboxd Sync' button system-wide.</summary>
        public bool HideLetterboxdButton { get; set; } = false;

        /// <summary>Hide the External Sync button (⇄ External Sync) in the My Ratings overlay system-wide.</summary>
        public bool HideExternalSyncButton { get; set; } = false;

        /// <summary>When true, the 'Rate' floating button only appears on media detail pages.</summary>
        public bool RateButtonOnlyInMediaItem { get; set; } = false;

        // ---- Rating display enhancements -------------------------------- //

        /// <summary>Replace the native community rating on the media details page with the StarTrack average.</summary>
        public bool ReplaceMediaDetailsRating { get; set; } = true;

        /// <summary>Replace ratings in the 'Media Bar' plugin with the StarTrack average.</summary>
        public bool ReplaceMediaBarRating { get; set; } = true;

        /// <summary>
        /// Replace the community rating on the 'Media Bar Enhanced' plugin's home-page
        /// hero with the StarTrack average. Items StarTrack hasn't rated keep showing
        /// their native rating rather than going blank.
        /// </summary>
        public bool ReplaceMediaBarEnhancedRating { get; set; } = false;

        /// <summary>Overlay the StarTrack average rating on media posters in library grids.</summary>
        public bool ShowRatingsOnPosters { get; set; } = true;

        /// <summary>
        /// Which corner of the poster the StarTrack rating badge sits in.
        /// Valid values: "top-right" (default), "top-left", "bottom-right", "bottom-left".
        /// Lets users move it off Jellyfin's watched/played checkmark (which sits top-right).
        /// </summary>
        public string PosterRatingPosition { get; set; } = "top-right";

        /// <summary>Show a rating popup after a movie or episode finishes playback.</summary>
        public bool PostPlaybackRatingPopup { get; set; } = true;

        /// <summary>
        /// Write a diary entry when a user finishes watching a movie or episode.
        ///
        /// On by default: a diary that does not record what you watched on the
        /// server it is installed in is not much of a diary, and before this the
        /// only way to get entries was importing them from Letterboxd.
        /// </summary>
        public bool LogWatchesToDiary { get; set; } = true;

        /// <summary>
        /// Also create a diary entry when a user rates something they have no
        /// entry for today.
        ///
        /// Off by default. Rating and logging are genuinely different acts —
        /// you can rate a film you saw years ago — so turning every rating into
        /// a "watched today" entry would falsify the diary. It is offered
        /// because most people rate right after watching, and were surprised
        /// that rating produced no diary entry at all.
        /// </summary>
        public bool LogDiaryOnRating { get; set; }

        /// <summary>
        /// When true, the 'Recent' floating pill shows recent ratings from EVERY
        /// user on the server (community feed) instead of only the current user's
        /// own ratings.
        /// </summary>
        public bool CommunityRecentMode { get; set; } = false;

        /// <summary>
        /// Comma-separated list of My Ratings overlay view names that should be
        /// hidden for all users. Valid values: watchlist, liked, diary, reviews,
        /// recs, lists. (The 'films' / Media view is always available.)
        /// </summary>
        public string HiddenOverlayViews { get; set; } = string.Empty;

        // ---- Reviews ----------------------------------------------------- //

        /// <summary>
        /// Maximum length (characters) allowed for a free-text review. Default 10000;
        /// admins can lower it (e.g. 1000) if that feels too long. Clamped to 1-10000.
        /// </summary>
        public int MaxReviewLength { get; set; } = 10000;

        /// <summary>Show only the rating on the media-page badge instead of the rating plus "StarTrack (N)".</summary>
        public bool CompactMediaBadge { get; set; } = false;

        /// <summary>[#19, Imgonnagitit] How a StarTrack average is written where it
        /// is shown as a number: the media-page badge, the poster badges and the
        /// floating pill.
        ///
        ///   "stars" (default) - 3.5          unchanged behaviour
        ///   "both"            - 3.5 (7/10)
        ///   "ten"             - 7/10
        ///
        /// StarTrack rates out of 5 while IMDb, Trakt and Simkl use 10, so a
        /// score of 7 imported from Simkl correctly becomes 3.5 stars and then
        /// sits beside IMDb's 7 on the same row, reading as a disagreement rather
        /// than the same score on a different scale.
        ///
        /// DISPLAY ONLY, and only for averages. 0.5-5 in half-steps and 1-10 in
        /// whole steps are the same ten positions, so nothing is converted,
        /// rounded or lost. The rating control stays a five-star picker under
        /// every mode - you always rate in stars, this only changes how the
        /// result is written back to you.
        ///
        /// Unrecognised values fall back to "stars".
        /// </summary>
        public string RatingDisplayMode { get; set; } = "stars";

        /// <summary>
        /// Optional base URL of a FlareSolverr instance, e.g.
        /// <c>http://192.168.1.10:8191</c>. Server-wide, admin-only.
        ///
        /// WHY: Letterboxd fronts its likes page and its sign-in with a
        /// Cloudflare JavaScript challenge that no server-side HTTP client can
        /// pass. FlareSolverr runs a real headless browser, passes it, and
        /// hands back the page and a <c>cf_clearance</c> cookie the plugin can
        /// then reuse on the same host. It is the same tool Prowlarr, Jackett
        /// and Sonarr use for the same wall.
        ///
        /// Empty (the default) means: never call it, back off when challenged,
        /// and tell the user which feeds are unavailable — the pre-existing
        /// behaviour, unchanged.
        /// </summary>
        public string FlareSolverrUrl { get; set; } = string.Empty;

        /// <summary>Size of the rating badges + floating pill: "normal" (default) or "large".</summary>
        public string RatingSize { get; set; } = "normal";

        /// <summary>[v1.6.2] (#12, damientkyt) Also write each StarTrack rating into
        /// Jellyfin's native per-user rating field (StarTrack's 0.5–5 stars are mapped
        /// x2 to Jellyfin's 0–10 scale), so ratings show in Jellyfin's own UI and are
        /// usable in filters / library backups. Opt-in; default off so existing native
        /// ratings are never touched unless an admin enables this.</summary>
        public bool MirrorToNativeRating { get; set; } = false;

        // ---- Letterboxd pending imports ---------------------------------- //

        /// <summary>
        /// [#25] Keep Letterboxd rows that matched nothing in the library and
        /// retry them on later syncs, so a rating imported today lands automatically once
        /// the film is added to Jellyfin months from now. Without this, an unmatched row is
        /// discarded the moment the import runs and the only way to recover it is to
        /// re-upload the whole export.
        ///
        /// Opt-in; default off. It writes a per-user queue to disk that grows with the part
        /// of a member's Letterboxd history the server does not have, which is a cost an
        /// admin should choose rather than inherit on upgrade.
        /// </summary>
        public bool RetainUnmatchedLetterboxdRows { get; set; } = false;

        // ---- Daily auto-export ------------------------------------------- //

        /// <summary>When true, a daily scheduled task exports all users' ratings to disk.</summary>
        public bool AutoExportDaily { get; set; } = false;

        /// <summary>File format for the daily auto-export: "csv" (default) or "json".</summary>
        public string AutoExportFormat { get; set; } = "csv";

        // ---- Trakt OAuth app credentials -------------------------------- //

        /// <summary>
        /// Client ID from the admin's registered Trakt app (https://trakt.tv/oauth/applications).
        /// Leave empty until the admin pastes their app's value.
        /// </summary>
        public string TraktClientId { get; set; } = string.Empty;

        /// <summary>
        /// Client Secret from the admin's registered Trakt app.
        /// Leave empty until the admin pastes their app's value.
        /// SECURITY NOTE: stored in Jellyfin XML config; encrypt at rest in Phase 2.
        /// </summary>
        public string TraktClientSecret { get; set; } = string.Empty;

        // ---- Simkl OAuth app credentials -------------------------------- //

        /// <summary>
        /// Client ID from the admin's registered Simkl app (https://simkl.com/settings/developer/).
        /// Leave empty until the admin pastes their app's value.
        /// </summary>
        public string SimklClientId { get; set; } = string.Empty;

        /// <summary>
        /// Client Secret from the admin's registered Simkl app.
        /// Leave empty until the admin pastes their app's value.
        /// SECURITY NOTE: stored in Jellyfin XML config; encrypt at rest in Phase 2.
        /// </summary>
        public string SimklClientSecret { get; set; } = string.Empty;
    }
}
