using Jellyfin.Plugin.InternalRating.Data;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.InternalRating
{
    /// <summary>
    /// Registers plugin services with Jellyfin's dependency injection container.
    /// Jellyfin discovers this class automatically at startup.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection)
        {
            serviceCollection.AddSingleton<RatingRepository>();
        }
    }
}
