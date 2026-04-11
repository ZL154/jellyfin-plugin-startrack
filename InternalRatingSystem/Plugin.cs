using System;
using System.Collections.Generic;
using Jellyfin.Plugin.InternalRating.Data;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.InternalRating
{
    /// <summary>
    /// StarTrack – Internal Rating System plugin entry point.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        /// <summary>
        /// Plugin singleton – set during construction.
        /// </summary>
        public static Plugin? Instance { get; private set; }

        /// <summary>
        /// Shared rating repository instance.
        /// </summary>
        public RatingRepository Repository { get; }

        /// <summary>
        /// Shared Letterboxd settings repository (per-user username + auto-sync state).
        /// </summary>
        public LetterboxdSettingsRepository LetterboxdSettings { get; }

        /// <summary>
        /// Shared user-interactions repository (watchlist, likes, favorites).
        /// </summary>
        public UserInteractionsRepository Interactions { get; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance           = this;
            Repository         = new RatingRepository(applicationPaths);
            LetterboxdSettings = new LetterboxdSettingsRepository(applicationPaths);
            Interactions       = new UserInteractionsRepository(applicationPaths);
        }

        /// <inheritdoc />
        public override string Name => "StarTrack";

        /// <inheritdoc />
        public override Guid Id => new Guid("a8b5e2f3-4c1d-4e8a-b2f9-6d3c7e1a5b2f");

        /// <inheritdoc />
        public override string Description =>
            "Community star ratings for movies and TV shows — stored privately on your Jellyfin server.";

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name                 = Name,
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                    EnableInMainMenu     = true,
                    MenuSection          = "server",
                    MenuIcon             = "star",
                    DisplayName          = "StarTrack"
                }
            };
        }
    }
}
