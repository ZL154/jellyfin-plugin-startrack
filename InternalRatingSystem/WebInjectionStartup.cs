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

namespace Jellyfin.Plugin.InternalRating
{
    /// <summary>Registers plugin services.</summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
        {
            services.AddHostedService<WebInjectionService>();
        }
    }

    /// <summary>
    /// Runs at startup. Tries File Transformation plugin first (preferred — no file writes),
    /// then falls back to patching index.html directly on disk.
    /// </summary>
    public class WebInjectionService : IHostedService
    {
        private const string Marker    = "<!-- startrack-widget -->";
        private const string ScriptTag = "<script src=\"/Plugins/StarTrack/Widget\"></script>";

        private readonly IApplicationPaths _appPaths;
        private readonly ILogger<WebInjectionService> _logger;

        public WebInjectionService(IApplicationPaths appPaths, ILogger<WebInjectionService> logger)
        {
            _appPaths = appPaths;
            _logger   = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[StarTrack] WebInjectionService starting. WebPath={P}", _appPaths.WebPath);

            if (!TryRegisterWithFileTransformation())
                TryPatchIndexHtml();

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        // ── Strategy 1: File Transformation plugin (preferred) ────────────

        private bool TryRegisterWithFileTransformation()
        {
            try
            {
                var asm = AssemblyLoadContext.All
                    .SelectMany(c => c.Assemblies)
                    .FirstOrDefault(a => a.FullName?.Contains(".FileTransformation") ?? false);

                if (asm == null)
                {
                    _logger.LogInformation("[StarTrack] File Transformation plugin not present — will patch file.");
                    return false;
                }

                var iface = asm.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
                var method = iface?.GetMethod("RegisterTransformation");
                if (method == null)
                {
                    _logger.LogWarning("[StarTrack] RegisterTransformation method not found in File Transformation.");
                    return false;
                }

                // Plain POCO — no Newtonsoft.Json dependency needed.
                // File Transformation reads these via reflection / JObject.FromObject().
                var payload = new FileTransformPayload
                {
                    id              = Plugin.Instance!.Id.ToString(),
                    fileNamePattern = "index\\.html",
                    callbackAssembly = typeof(TransformationPatches).Assembly.FullName!,
                    callbackClass   = typeof(TransformationPatches).FullName!,
                    callbackMethod  = nameof(TransformationPatches.PatchIndexHtml)
                };

                method.Invoke(null, new object?[] { payload });
                _logger.LogInformation("[StarTrack] Registered with File Transformation — widget will be injected per-request.");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StarTrack] File Transformation registration failed.");
                return false;
            }
        }

        // ── Strategy 2: patch index.html on disk ─────────────────────────

        private void TryPatchIndexHtml()
        {
            try
            {
                var path = Path.Combine(_appPaths.WebPath, "index.html");
                _logger.LogInformation("[StarTrack] Attempting to patch {P}", path);

                if (!File.Exists(path))
                {
                    _logger.LogWarning("[StarTrack] index.html not found at {P}", path);
                    return;
                }

                var html = File.ReadAllText(path);
                if (html.Contains(Marker, StringComparison.Ordinal))
                {
                    _logger.LogInformation("[StarTrack] index.html already patched.");
                    return;
                }

                File.WriteAllText(path,
                    html.Replace("</body>",
                        $"{Marker}{ScriptTag}</body>",
                        StringComparison.OrdinalIgnoreCase));

                _logger.LogInformation("[StarTrack] Patched index.html successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] Failed to patch index.html.");
            }
        }

        // ── Payload POCO (no external deps) ──────────────────────────────

        private sealed class FileTransformPayload
        {
            public string id              { get; set; } = string.Empty;
            public string fileNamePattern { get; set; } = string.Empty;
            public string callbackAssembly{ get; set; } = string.Empty;
            public string callbackClass   { get; set; } = string.Empty;
            public string callbackMethod  { get; set; } = string.Empty;
        }
    }
}
