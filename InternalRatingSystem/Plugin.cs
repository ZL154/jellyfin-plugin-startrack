using System;
using System.Collections.Generic;
using Jellyfin.Plugin.InternalRating.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.InternalRating
{
    /// <summary>
    /// Internal Rating System – Jellyfin plugin entry point.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        /// <summary>
        /// Singleton reference set during construction; use for accessing the plugin instance.
        /// </summary>
        public static Plugin? Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        /// <inheritdoc />
        public override string Name => "Internal Rating System";

        /// <inheritdoc />
        public override Guid Id => new Guid("a8b5e2f3-4c1d-4e8a-b2f9-6d3c7e1a5b2f");

        /// <inheritdoc />
        public override string Description =>
            "Allows Jellyfin users to rate movies and TV shows internally and view community star ratings.";

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name                = Name,
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                    EnableInMainMenu    = true,
                    MenuSection         = "server",
                    MenuIcon            = "star",
                    DisplayName         = "Internal Rating System"
                }
            };
        }
    }
}
