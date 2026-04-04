using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating
{
    /// <summary>
    /// Intercepts HTTP responses for Jellyfin's index.html and injects the
    /// StarTrack widget script tag. This approach requires no file system
    /// permissions and is independent of any third-party plugins.
    /// </summary>
    public class ScriptInjectionMiddleware
    {
        private const string ScriptTag = "<script src=\"/Plugins/StarTrack/Widget\"></script>";
        private const string Marker    = "/Plugins/StarTrack/Widget";

        private readonly RequestDelegate _next;
        private readonly ILogger<ScriptInjectionMiddleware> _logger;

        public ScriptInjectionMiddleware(RequestDelegate next, ILogger<ScriptInjectionMiddleware> logger)
        {
            _next   = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value ?? string.Empty;

            if (!IsIndexHtmlRequest(path))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            // Remove Accept-Encoding so we always get plain text (not gzip)
            context.Request.Headers.Remove("Accept-Encoding");

            var originalBody = context.Response.Body;

            try
            {
                using var ms = new MemoryStream();
                context.Response.Body = ms;

                await _next(context).ConfigureAwait(false);

                // Only modify successful HTML responses
                if (context.Response.StatusCode != 200)
                {
                    await CopyBack(ms, originalBody).ConfigureAwait(false);
                    return;
                }

                var contentType = context.Response.ContentType ?? string.Empty;
                if (!contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    await CopyBack(ms, originalBody).ConfigureAwait(false);
                    return;
                }

                ms.Position = 0;
                string html;
                using (var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
                    html = await reader.ReadToEndAsync().ConfigureAwait(false);

                if (string.IsNullOrEmpty(html) || html.Contains(Marker, StringComparison.OrdinalIgnoreCase))
                {
                    await CopyBack(ms, originalBody).ConfigureAwait(false);
                    return;
                }

                var bodyIdx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (bodyIdx == -1)
                {
                    await CopyBack(ms, originalBody).ConfigureAwait(false);
                    return;
                }

                var modified = html.Insert(bodyIdx, ScriptTag + "\n");
                var bytes    = Encoding.UTF8.GetBytes(modified);

                context.Response.Headers.Remove("Content-Length");
                context.Response.ContentLength = bytes.Length;
                await originalBody.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);

                _logger.LogInformation("[StarTrack] Injected widget script into index.html response.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StarTrack] Script injection failed; passing through original response.");
                try { await CopyBack(context.Response.Body as MemoryStream ?? new MemoryStream(), originalBody).ConfigureAwait(false); }
                catch { /* best effort */ }
            }
            finally
            {
                context.Response.Body = originalBody;
            }
        }

        private static async Task CopyBack(MemoryStream ms, Stream original)
        {
            if (ms.Length > 0)
            {
                ms.Position = 0;
                await ms.CopyToAsync(original).ConfigureAwait(false);
            }
        }

        private static bool IsIndexHtmlRequest(string path) =>
            path.Equals("/",                   StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/index.html",         StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/web",                StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/web/",               StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/web/index.html",     StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/web",              StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/web/",             StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/web/index.html",   StringComparison.OrdinalIgnoreCase);
    }
}
