using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Data;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating
{
    /// <summary>Registers plugin services and the HTTP middleware startup filter.</summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
        {
            // Primary injection: IStartupFilter inserts our middleware at the very front
            // of Jellyfin's ASP.NET Core pipeline. This intercepts every index.html
            // HTTP response and injects the widget script tag — no file permissions
            // or third-party plugins required.
            services.AddSingleton<IStartupFilter, ScriptInjectionStartupFilter>();

            // Fallback: also try patching index.html on disk in case middleware
            // somehow doesn't reach it (very unusual setups).
            services.AddHostedService<WebInjectionService>();

            // Expose the existing repositories as DI singletons so controllers
            // and services can request them via constructor injection. Both are
            // constructed by Plugin.cs with the ApplicationPaths the base class
            // provides, so we just forward the already-built instances.
            services.AddSingleton<RatingRepository>(_ => Plugin.Instance!.Repository);
            services.AddSingleton<LetterboxdSettingsRepository>(_ => Plugin.Instance!.LetterboxdSettings);

            // Letterboxd sync service — gets ILibraryManager + logger from DI,
            // repositories from the singletons above.
            services.AddSingleton<LetterboxdSyncService>();

            // Scheduled task: register as IScheduledTask so Jellyfin's task
            // scheduler picks it up and runs it hourly by default.
            services.AddSingleton<IScheduledTask, LetterboxdSyncTask>();
        }
    }

    /// <summary>
    /// Fallback hosted service: patches index.html on disk.
    /// The primary injection is handled by ScriptInjectionMiddleware.
    /// </summary>
    public class WebInjectionService : IHostedService
    {
        private const string Marker    = "<!-- startrack-widget -->";
        private const string ScriptTag = "<script src=\"/Plugins/StarTrack/Widget\"></script>";

        // Diagnostics for the debug endpoint
        public static string DiagWebPath     = "not set";
        public static bool   DiagIndexFound;
        public static bool   DiagIndexPatched;
        public static string DiagPatchedPath = "none";
        public static string DiagLastError   = "none";
        public static string DiagFtStatus    = "not used (middleware is primary)";

        private readonly IApplicationPaths _appPaths;
        private readonly ILogger<WebInjectionService> _logger;

        public WebInjectionService(IApplicationPaths appPaths, ILogger<WebInjectionService> logger)
        {
            _appPaths = appPaths;
            _logger   = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            DiagWebPath = _appPaths.WebPath ?? "null";
            _logger.LogInformation("[StarTrack] v1.0.9 WebInjectionService starting (fallback). WebPath={P}", _appPaths.WebPath);

            await TryPatchIndexHtmlAsync().ConfigureAwait(false);

            _ = Task.Run(async () =>
            {
                await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
                if (!DiagIndexPatched)
                    await TryPatchIndexHtmlAsync().ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private async Task TryPatchIndexHtmlAsync()
        {
            var candidates = new[]
            {
                Path.Combine(_appPaths.WebPath, "index.html"),
                "/usr/share/jellyfin/web/index.html",
                "/usr/lib/jellyfin/web/index.html",
                "/jellyfin/jellyfin-web/index.html",
                "/var/lib/jellyfin/web/index.html"
            };

            foreach (var path in candidates)
            {
                try
                {
                    if (!File.Exists(path)) continue;

                    DiagIndexFound = true;
                    var html = await File.ReadAllTextAsync(path).ConfigureAwait(false);

                    if (html.Contains(Marker, StringComparison.Ordinal))
                    {
                        DiagIndexPatched = true;
                        DiagPatchedPath  = path;
                        return;
                    }

                    if (!html.Contains("</body>", StringComparison.OrdinalIgnoreCase)) continue;

                    await File.WriteAllTextAsync(path,
                        html.Replace("</body>", $"{Marker}{ScriptTag}</body>", StringComparison.OrdinalIgnoreCase))
                        .ConfigureAwait(false);

                    DiagIndexPatched = true;
                    DiagPatchedPath  = path;
                    _logger.LogInformation("[StarTrack] Patched index.html at {P}", path);
                    return;
                }
                catch (UnauthorizedAccessException uex)
                {
                    DiagLastError = uex.Message;
                }
                catch (Exception ex)
                {
                    DiagLastError = ex.Message;
                }
            }
        }
    }
}
