using System;
using System.Collections.Generic;
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
    /// Registers the widget injection hosted service.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
        {
            services.AddHostedService<WebInjectionService>();
        }
    }

    /// <summary>
    /// On server startup, finds Jellyfin's index.html and injects the widget script tag.
    /// Tries multiple known paths to support different OS / Docker layouts.
    /// </summary>
    public class WebInjectionService : IHostedService
    {
        private const string Marker    = "<!-- startrack-widget -->";
        private const string Injection =
            "\n<!-- startrack-widget -->\n" +
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
                var candidates = BuildCandidates();
                foreach (var path in candidates)
                {
                    if (!File.Exists(path)) continue;

                    var html = File.ReadAllText(path);
                    if (html.Contains(Marker, StringComparison.Ordinal))
                    {
                        _logger.LogDebug("[StarTrack] Widget already injected at {Path}", path);
                        return Task.CompletedTask;
                    }

                    if (!html.Contains("</body>", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("[StarTrack] {Path} has no </body> — skipping.", path);
                        continue;
                    }

                    File.WriteAllText(path,
                        html.Replace("</body>", Injection + "</body>", StringComparison.OrdinalIgnoreCase));
                    _logger.LogInformation("[StarTrack] Widget injected into {Path}", path);
                    return Task.CompletedTask;
                }

                _logger.LogWarning("[StarTrack] index.html not found. Tried: {Paths}", string.Join(", ", candidates));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] Failed to inject widget.");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private List<string> BuildCandidates()
        {
            var list = new List<string>();

            // 1. ASP.NET Core web root (most reliable)
            if (!string.IsNullOrEmpty(_env.WebRootPath))
                list.Add(Path.Combine(_env.WebRootPath, "index.html"));

            // 2. Linux package / Docker installs
            list.Add("/usr/share/jellyfin/web/index.html");
            list.Add("/jellyfin/jellyfin-web/index.html");
            list.Add("/app/jellyfin-web/index.html");
            list.Add("/opt/jellyfin/web/index.html");
            list.Add("/data/web/index.html");

            // 3. Windows default install
            list.Add(@"C:\Program Files\Jellyfin\Server\jellyfin-web\index.html");

            // 4. Relative to the current process
            var exe = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(exe))
            {
                list.Add(Path.Combine(exe, "jellyfin-web", "index.html"));
                list.Add(Path.Combine(exe, "..", "jellyfin-web", "index.html"));
            }

            return list;
        }
    }
}
