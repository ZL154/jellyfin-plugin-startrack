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
    }
}
