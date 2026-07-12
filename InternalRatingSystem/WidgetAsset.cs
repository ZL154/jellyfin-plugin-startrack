using System;
using System.Reflection;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.InternalRating
{
    /// <summary>
    /// Computes a stable cache-busting token for the embedded widget.js and
    /// builds the injected &lt;script&gt; tag with that token as a query string.
    ///
    /// WHY: the widget is injected as a fixed URL (/Plugins/StarTrack/Widget).
    /// Without a version query, browsers cache the script indefinitely, so
    /// after a plugin update users keep running the OLD widget until they hard
    /// refresh. By appending ?v=&lt;contenthash&gt;, the URL changes whenever
    /// widget.js changes (every build / hot-swap), forcing a fresh fetch — while
    /// staying byte-identical between identical builds so normal caching still
    /// works.
    /// </summary>
    public static class WidgetAsset
    {
        private const string ResourceName = "Jellyfin.Plugin.InternalRating.Web.widget.js";

        private static readonly Lazy<string> _version = new(ComputeVersion);

        /// <summary>Short content hash of the embedded widget.js (8 hex chars).</summary>
        public static string Version => _version.Value;

        /// <summary>[v1.6.2] (#13) Jellyfin's base path ("" or "/sub"), so the widget
        /// URL routes correctly when Jellyfin is served under a reverse-proxy sub-path.</summary>
        private static string Base => Plugin.Instance?.BaseUrl ?? string.Empty;

        /// <summary>The widget endpoint path including the base prefix + cache-busting query.</summary>
        public static string WidgetUrl => Base + "/Plugins/StarTrack/Widget?v=" + Version;

        /// <summary>The full &lt;script&gt; tag injected into index.html.</summary>
        public static string ScriptTag => "<script src=\"" + WidgetUrl + "\"></script>";

        private static string ComputeVersion()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream(ResourceName);
                if (stream == null) return "0";

                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(stream);
                // First 4 bytes -> 8 hex chars is plenty to distinguish builds.
                return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
            }
            catch
            {
                return "0";
            }
        }
    }
}
