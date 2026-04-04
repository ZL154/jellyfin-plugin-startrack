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
                var ftAsm = AssemblyLoadContext.All
                    .SelectMany(c => c.Assemblies)
                    .FirstOrDefault(a => a.FullName?.Contains(".FileTransformation") ?? false);

                if (ftAsm == null)
                {
                    DiagFtStatus = "not installed";
                    _logger.LogWarning("[StarTrack] File Transformation plugin not present — will patch file.");
                    return false;
                }

                DiagFtStatus = $"found: {ftAsm.GetName().Name}";

                var iface = ftAsm.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
                if (iface == null)
                {
                    DiagFtStatus = "assembly found but PluginInterface type missing";
                    _logger.LogWarning("[StarTrack] PluginInterface type not found in File Transformation assembly.");
                    return false;
                }

                var method = iface.GetMethod("RegisterTransformation")
                          ?? iface.GetMethod("Register")
                          ?? iface.GetMethod("AddTransformation");

                if (method == null)
                {
                    DiagFtStatus = "PluginInterface found but RegisterTransformation method missing";
                    _logger.LogWarning("[StarTrack] RegisterTransformation not found. Available methods: {M}",
                        string.Join(", ", iface.GetMethods().Select(m => m.Name)));
                    return false;
                }

                // File Transformation expects a JObject payload.
                // Newtonsoft.Json is already loaded in memory by File Transformation,
                // so we create the JObject via reflection — no compile-time dependency needed.
                var payload = BuildJObjectPayload();
                if (payload == null)
                {
                    DiagFtStatus = "could not create JObject payload (Newtonsoft.Json not in memory)";
                    _logger.LogWarning("[StarTrack] Newtonsoft.Json not found in loaded assemblies.");
                    return false;
                }

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

        /// <summary>
        /// Creates a JObject via reflection using the Newtonsoft.Json assembly
        /// that File Transformation has already loaded. This avoids a compile-time
        /// dependency on Newtonsoft.Json while still passing the correct type.
        /// </summary>
        private static object? BuildJObjectPayload()
        {
            try
            {
                var newtonsoftAsm = AssemblyLoadContext.All
                    .SelectMany(c => c.Assemblies)
                    .FirstOrDefault(a => a.GetName().Name == "Newtonsoft.Json");

                if (newtonsoftAsm == null) return null;

                var jObjectType = newtonsoftAsm.GetType("Newtonsoft.Json.Linq.JObject");
                var jTokenType  = newtonsoftAsm.GetType("Newtonsoft.Json.Linq.JToken");
                if (jObjectType == null || jTokenType == null) return null;

                // JToken.FromObject(object value) — converts a CLR value to a JToken
                var fromObject = jTokenType.GetMethod("FromObject", new[] { typeof(object) });
                if (fromObject == null) return null;

                // JObject indexer: jObject["key"] = jToken
                var indexer = jObjectType.GetProperty("Item",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null, null, new[] { typeof(string) }, null);
                if (indexer == null) return null;

                var jObj = Activator.CreateInstance(jObjectType)!;

                void Set(string key, string value)
                {
                    var token = fromObject.Invoke(null, new object[] { value });
                    indexer.SetValue(jObj, token, new object[] { key });
                }

                Set("id",               Plugin.Instance!.Id.ToString());
                Set("fileNamePattern",  "index\\.html");
                Set("callbackAssembly", typeof(TransformationPatches).Assembly.FullName!);
                Set("callbackClass",    typeof(TransformationPatches).FullName!);
                Set("callbackMethod",   nameof(TransformationPatches.PatchIndexHtml));

                return jObj;
            }
            catch
            {
                return null;
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

    }
}
