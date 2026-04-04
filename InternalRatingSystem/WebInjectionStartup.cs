using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating
{
    /// <summary>
    /// Registers the widget-injection hosted service via the plugin DI hook.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddHostedService<WebInjectionService>();
        }
    }

    /// <summary>
    /// Runs once at server startup. Injects the StarTrack widget script tag into
    /// Jellyfin's index.html so it loads automatically for every user (including mobile)
    /// without any browser extension.
    /// </summary>
    public class WebInjectionService : IHostedService
    {
        private const string StartMarker = "<!-- startrack-widget-start -->";
        private const string ScriptBlock =
            "\n<!-- startrack-widget-start -->\n" +
            "<script src=\"/Plugins/StarTrack/Widget\"></script>\n" +
            "<!-- startrack-widget-end -->\n";

        private readonly IWebHostEnvironment _env;
        private readonly ILogger<WebInjectionService> _logger;

        public WebInjectionService(IWebHostEnvironment env, ILogger<WebInjectionService> logger)
        {
            _env    = env;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                var webRoot = _env.WebRootPath;
                if (string.IsNullOrEmpty(webRoot))
                {
                    _logger.LogWarning("[StarTrack] WebRootPath is empty – cannot inject widget.");
                    return Task.CompletedTask;
                }

                var indexPath = Path.Combine(webRoot, "index.html");
                if (!File.Exists(indexPath))
                {
                    _logger.LogWarning("[StarTrack] index.html not found at {Path}", indexPath);
                    return Task.CompletedTask;
                }

                var content = File.ReadAllText(indexPath);

                // Idempotent – don't inject twice
                if (content.Contains(StartMarker, StringComparison.Ordinal))
                {
                    _logger.LogDebug("[StarTrack] Widget already injected – skipping.");
                    return Task.CompletedTask;
                }

                var updated = content.Replace("</body>", ScriptBlock + "</body>", StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(indexPath, updated);
                _logger.LogInformation("[StarTrack] Successfully injected widget script into index.html");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] Failed to inject widget into index.html");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
