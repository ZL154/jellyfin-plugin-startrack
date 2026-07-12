using System;
using System.Collections.Generic;
using Jellyfin.Plugin.InternalRating.Data;
using Jellyfin.Plugin.InternalRating.ExternalSync;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
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

        /// <summary>Shared per-user diary repository (rewatch-friendly).</summary>
        public DiaryRepository Diary { get; }

        /// <summary>Shared collaborative lists repository.</summary>
        public ListsRepository Lists { get; }

        /// <summary>Per-user privacy settings (hide-from-members, hide-follower-count).</summary>
        public PrivacyRepository Privacy { get; }

        /// <summary>Follow graph: who follows whom.</summary>
        public FollowsRepository Follows { get; }

        /// <summary>Per-user, per-provider external sync settings (Trakt, Simkl, etc.).</summary>
        public ExternalSyncSettingsRepository ExternalSyncSettings { get; }

        private readonly IServerConfigurationManager _serverConfig;

        /// <summary>[v1.6.2] (#13, LinkdxTTV) Jellyfin's configured base path
        /// (BaseURL network setting, e.g. "/jelly") for reverse-proxy sub-path
        /// deployments. Normalized to "" (root) or "/xxx" with no trailing slash.
        /// The injected widget &lt;script&gt; and all its API calls are prefixed
        /// with this so they route correctly behind a sub-path proxy.</summary>
        public string BaseUrl
        {
            get
            {
                try
                {
                    // Read the NetworkConfiguration.BaseUrl reflectively via the stable
                    // IConfigurationManager.GetConfiguration("network") — avoids a
                    // compile-time reference to the Jellyfin.Networking assembly.
                    var netCfg = _serverConfig.GetConfiguration("network");
                    var b = (netCfg?.GetType().GetProperty("BaseUrl")?.GetValue(netCfg) as string ?? string.Empty).Trim();
                    if (b.Length == 0) return string.Empty;
                    if (!b.StartsWith('/')) b = "/" + b;
                    return b.TrimEnd('/');
                }
                catch { return string.Empty; }
            }
        }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, IServerConfigurationManager serverConfig)
            : base(applicationPaths, xmlSerializer)
        {
            Instance              = this;
            _serverConfig         = serverConfig;
            Repository            = new RatingRepository(applicationPaths);
            LetterboxdSettings    = new LetterboxdSettingsRepository(applicationPaths);
            Interactions          = new UserInteractionsRepository(applicationPaths);
            Diary                 = new DiaryRepository(applicationPaths);
            Lists                 = new ListsRepository(applicationPaths);
            Privacy               = new PrivacyRepository(applicationPaths);
            Follows               = new FollowsRepository(applicationPaths);
            ExternalSyncSettings  = new ExternalSyncSettingsRepository(applicationPaths);
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
