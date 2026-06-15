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

        /// <summary>When true, the 'Rate' floating button only appears on media detail pages.</summary>
        public bool RateButtonOnlyInMediaItem { get; set; } = false;

        // ---- Rating display enhancements -------------------------------- //

        /// <summary>Replace the native community rating on the media details page with the StarTrack average.</summary>
        public bool ReplaceMediaDetailsRating { get; set; } = true;

        /// <summary>Replace ratings in the 'Media Bar' plugin with the StarTrack average.</summary>
        public bool ReplaceMediaBarRating { get; set; } = true;

        /// <summary>Overlay the StarTrack average rating on media posters in library grids.</summary>
        public bool ShowRatingsOnPosters { get; set; } = true;

        /// <summary>Show a rating popup after a movie or episode finishes playback.</summary>
        public bool PostPlaybackRatingPopup { get; set; } = true;

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
    }
}
