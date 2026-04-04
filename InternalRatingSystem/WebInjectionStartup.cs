using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

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
    /// On startup:
    ///   1. Registers with the File Transformation plugin so it injects the widget
    ///      script on every index.html response (non-destructive, preferred).
    ///   2. Falls back to patching index.html on disk if File Transformation is absent.
    /// </summary>
    public class WebInjectionService : IHostedService
    {
        private const string Marker    = "<!-- startrack-widget -->";
        private const string Injection =
            "<!-- startrack-widget -->" +
            "<script src=\"/Plugins/StarTrack/Widget\"></script>";

        private readonly IApplicationPaths _appPaths;
        private readonly ILogger<WebInjectionService> _logger;

        public WebInjectionService(IApplicationPaths appPaths, ILogger<WebInjectionService> logger)
        {
            _appPaths = appPaths;
            _logger   = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Try File Transformation plugin first (best — no file modification needed)
            if (!TryRegisterFileTransformation())
            {
                // Fallback: patch index.html directly on disk
                TryPatchIndexHtml();
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        // ── Strategy 1: File Transformation plugin ────────────────────────

        private bool TryRegisterFileTransformation()
        {
            try
            {
                var ftAssembly = AssemblyLoadContext.All
                    .SelectMany(ctx => ctx.Assemblies)
                    .FirstOrDefault(a => a.FullName?.Contains(".FileTransformation") ?? false);

                if (ftAssembly == null)
                {
                    _logger.LogDebug("[StarTrack] File Transformation plugin not found — will patch file instead.");
                    return false;
                }

                var pluginInterface = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
                if (pluginInterface == null)
                {
                    _logger.LogWarning("[StarTrack] PluginInterface type not found in File Transformation assembly.");
                    return false;
                }

                var registerMethod = pluginInterface.GetMethod("RegisterTransformation");
                if (registerMethod == null)
                {
                    _logger.LogWarning("[StarTrack] RegisterTransformation method not found.");
                    return false;
                }

                var payload = new JObject
                {
                    ["id"]              = Plugin.Instance!.Id.ToString(),
                    ["fileNamePattern"] = "index\\.html",
                    ["callbackAssembly"]= typeof(TransformationPatches).Assembly.FullName,
                    ["callbackClass"]   = typeof(TransformationPatches).FullName,
                    ["callbackMethod"]  = nameof(TransformationPatches.PatchIndexHtml)
                };

                registerMethod.Invoke(null, new object?[] { payload });
                _logger.LogInformation("[StarTrack] Registered with File Transformation plugin — widget will be injected dynamically.");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StarTrack] File Transformation registration failed.");
                return false;
            }
        }

        // ── Strategy 2: patch index.html on disk ──────────────────────────

        private void TryPatchIndexHtml()
        {
            try
            {
                // IApplicationPaths.WebPath is Jellyfin's actual web root — correct for all installs
                var indexPath = Path.Combine(_appPaths.WebPath, "index.html");

                if (!File.Exists(indexPath))
                {
                    _logger.LogWarning("[StarTrack] index.html not found at {Path}", indexPath);
                    return;
                }

                var html = File.ReadAllText(indexPath);

                if (html.Contains(Marker, StringComparison.Ordinal))
                {
                    _logger.LogDebug("[StarTrack] index.html already patched.");
                    return;
                }

                if (!html.Contains("</body>", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("[StarTrack] index.html has no </body> tag — cannot patch.");
                    return;
                }

                File.WriteAllText(indexPath,
                    html.Replace("</body>", Injection + "</body>", StringComparison.OrdinalIgnoreCase));

                _logger.LogInformation("[StarTrack] Patched index.html at {Path}", indexPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] Failed to patch index.html.");
            }
        }
    }
}
