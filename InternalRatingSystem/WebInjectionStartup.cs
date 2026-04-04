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

        // Static diagnostics — readable by the debug endpoint
        public static string  DiagWebPath    = "not set";
        public static bool    DiagIndexFound;
        public static bool    DiagIndexPatched;
        public static string  DiagFtStatus   = "not checked";
        public static string  DiagPatchedPath = "none";
        public static string  DiagLastError  = "none";

        private readonly IApplicationPaths _appPaths;
        private readonly ILogger<WebInjectionService> _logger;

        public WebInjectionService(IApplicationPaths appPaths, ILogger<WebInjectionService> logger)
        {
            _appPaths = appPaths;
            _logger   = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            DiagWebPath = _appPaths.WebPath ?? "null";
            _logger.LogWarning("[StarTrack] v1.0.2 WebInjectionService starting. WebPath={P}", _appPaths.WebPath);

            // Run injection on background thread so we don't block Jellyfin startup
            _ = Task.Run(async () =>
            {
                await RunInjectionAsync().ConfigureAwait(false);

                // Retry after 5 s in case index.html wasn't ready yet
                await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
                if (!DiagIndexPatched)
                {
                    _logger.LogWarning("[StarTrack] Retrying injection after 5 s delay...");
                    await RunInjectionAsync().ConfigureAwait(false);
                }
            }, cancellationToken);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private async Task RunInjectionAsync()
        {
            if (!TryRegisterWithFileTransformation())
                await TryPatchIndexHtmlAsync().ConfigureAwait(false);
        }

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
                    DiagFtStatus = "not installed";
                    _logger.LogWarning("[StarTrack] File Transformation plugin not present — will patch file.");
                    return false;
                }

                DiagFtStatus = $"found: {asm.GetName().Name}";

                var iface = asm.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
                if (iface == null)
                {
                    DiagFtStatus = "assembly found but PluginInterface type missing";
                    _logger.LogWarning("[StarTrack] PluginInterface type not found in File Transformation assembly.");
                    return false;
                }

                var method = iface.GetMethod("RegisterTransformation");
                if (method == null)
                {
                    // Try alternate method names
                    method = iface.GetMethod("Register")
                          ?? iface.GetMethod("AddTransformation");
                }

                if (method == null)
                {
                    DiagFtStatus = "PluginInterface found but RegisterTransformation method missing";
                    _logger.LogWarning("[StarTrack] RegisterTransformation not found. Available methods: {M}",
                        string.Join(", ", iface.GetMethods().Select(m => m.Name)));
                    return false;
                }

                var payload = new FileTransformPayload
                {
                    id               = Plugin.Instance!.Id.ToString(),
                    fileNamePattern  = "index\\.html",
                    callbackAssembly = typeof(TransformationPatches).Assembly.FullName!,
                    callbackClass    = typeof(TransformationPatches).FullName!,
                    callbackMethod   = nameof(TransformationPatches.PatchIndexHtml)
                };

                method.Invoke(null, new object?[] { payload });
                DiagFtStatus = "registered OK";
                _logger.LogWarning("[StarTrack] Registered with File Transformation — widget will be injected per-request.");
                return true;
            }
            catch (Exception ex)
            {
                DiagFtStatus = $"exception: {ex.Message}";
                DiagLastError = ex.ToString();
                _logger.LogWarning(ex, "[StarTrack] File Transformation registration failed.");
                return false;
            }
        }

        // ── Strategy 2: patch index.html on disk ─────────────────────────

        private async Task TryPatchIndexHtmlAsync()
        {
            // Build candidate paths to try (in order of preference)
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
                    _logger.LogWarning("[StarTrack] Trying to patch {P}", path);

                    if (!File.Exists(path))
                    {
                        _logger.LogWarning("[StarTrack] Not found: {P}", path);
                        continue;
                    }

                    DiagIndexFound = true;

                    var html = await File.ReadAllTextAsync(path).ConfigureAwait(false);

                    if (html.Contains(Marker, StringComparison.Ordinal))
                    {
                        DiagIndexPatched  = true;
                        DiagPatchedPath   = path;
                        _logger.LogWarning("[StarTrack] index.html already patched at {P}", path);
                        return;
                    }

                    if (!html.Contains("</body>", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("[StarTrack] index.html at {P} has no </body> tag — skipping.", path);
                        continue;
                    }

                    var patched = html.Replace("</body>",
                        $"{Marker}{ScriptTag}</body>",
                        StringComparison.OrdinalIgnoreCase);

                    await File.WriteAllTextAsync(path, patched).ConfigureAwait(false);

                    DiagIndexPatched = true;
                    DiagPatchedPath  = path;
                    _logger.LogWarning("[StarTrack] SUCCESS — patched index.html at {P}", path);
                    return;
                }
                catch (UnauthorizedAccessException uex)
                {
                    DiagLastError = uex.Message;
                    _logger.LogWarning(uex, "[StarTrack] Permission denied patching {P}", path);
                }
                catch (Exception ex)
                {
                    DiagLastError = ex.Message;
                    _logger.LogError(ex, "[StarTrack] Error patching {P}", path);
                }
            }

            _logger.LogError("[StarTrack] Could not patch any index.html. Tried: {Paths}",
                string.Join(", ", candidates));
        }

        // ── Payload POCO (no external deps) ──────────────────────────────

        private sealed class FileTransformPayload
        {
            public string id               { get; set; } = string.Empty;
            public string fileNamePattern  { get; set; } = string.Empty;
            public string callbackAssembly { get; set; } = string.Empty;
            public string callbackClass    { get; set; } = string.Empty;
            public string callbackMethod   { get; set; } = string.Empty;
        }
    }
}
