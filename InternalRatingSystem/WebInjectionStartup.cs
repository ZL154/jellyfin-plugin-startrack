using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating
{
    /// <summary>
    /// Registers all StarTrack server-side services.
    /// Two injection strategies run in parallel — whichever lands first wins.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
        {
            // Strategy A: patch index.html on disk at startup
            services.AddHostedService<WebInjectionService>();
            // Strategy B: intercept every index.html HTTP response via middleware
            services.AddSingleton<IStartupFilter, WidgetInjectionStartupFilter>();
        }
    }

    // ── Strategy A: patch file on disk ─────────────────────────────────────

    public class WebInjectionService : IHostedService
    {
        private const string Marker    = "<!-- startrack-widget -->";
        private const string Injection = "\n<!-- startrack-widget -->\n<script src=\"/Plugins/StarTrack/Widget\"></script>\n<!-- startrack-widget-end -->\n";

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
                var root = _env.WebRootPath;
                if (string.IsNullOrEmpty(root)) { _logger.LogWarning("[StarTrack] WebRootPath empty."); return Task.CompletedTask; }

                var path = Path.Combine(root, "index.html");
                if (!File.Exists(path)) { _logger.LogWarning("[StarTrack] index.html not found at {P}", path); return Task.CompletedTask; }

                var html = File.ReadAllText(path);
                if (html.Contains(Marker, StringComparison.Ordinal)) { _logger.LogDebug("[StarTrack] Already injected (file)."); return Task.CompletedTask; }

                File.WriteAllText(path, html.Replace("</body>", Injection + "</body>", StringComparison.OrdinalIgnoreCase));
                _logger.LogInformation("[StarTrack] Injected widget into index.html (file strategy).");
            }
            catch (Exception ex) { _logger.LogError(ex, "[StarTrack] File injection failed."); }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // ── Strategy B: intercept index.html HTTP response ─────────────────────

    public class WidgetInjectionStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.UseMiddleware<WidgetInjectionMiddleware>();
                next(app);
            };
        }
    }

    public class WidgetInjectionMiddleware
    {
        private const string Marker    = "<!-- startrack-widget -->";
        private const string Injection = "<!-- startrack-widget --><script src=\"/Plugins/StarTrack/Widget\"></script>";
        private readonly RequestDelegate _next;

        public WidgetInjectionMiddleware(RequestDelegate next) => _next = next;

        public async Task InvokeAsync(HttpContext ctx)
        {
            var path = ctx.Request.Path.Value ?? string.Empty;

            // Only intercept requests that could be index.html
            bool couldBeIndex =
                path.Equals("/", StringComparison.OrdinalIgnoreCase) ||
                path.Equals(string.Empty, StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("index.html", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/web", StringComparison.OrdinalIgnoreCase);

            if (!couldBeIndex) { await _next(ctx); return; }

            // Buffer the downstream response
            var original = ctx.Response.Body;
            using var buffer = new MemoryStream();
            ctx.Response.Body = buffer;

            try { await _next(ctx); }
            catch { ctx.Response.Body = original; throw; }
            finally { ctx.Response.Body = original; }

            buffer.Seek(0, SeekOrigin.Begin);

            var ct = ctx.Response.ContentType ?? string.Empty;
            if (!ct.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                // Not HTML — pass through unchanged
                buffer.Seek(0, SeekOrigin.Begin);
                await buffer.CopyToAsync(original);
                return;
            }

            var html = await new StreamReader(buffer, Encoding.UTF8).ReadToEndAsync();

            if (!html.Contains(Marker, StringComparison.Ordinal) &&
                html.Contains("</body>", StringComparison.OrdinalIgnoreCase))
            {
                html = html.Replace("</body>", Injection + "</body>", StringComparison.OrdinalIgnoreCase);
            }

            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentLength = bytes.Length;
            await original.WriteAsync(bytes, ctx.RequestAborted);
        }
    }
}
