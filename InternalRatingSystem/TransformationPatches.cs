using System;
using System.Reflection;

namespace Jellyfin.Plugin.InternalRating
{
    /// <summary>
    /// Static callback invoked by the File Transformation plugin each time index.html is served.
    /// Injects the StarTrack widget script tag before &lt;/body&gt;.
    /// </summary>
    public static class TransformationPatches
    {
        private const string Marker    = "<!-- startrack-widget -->";
        private const string ScriptTag = "<script src=\"/Plugins/StarTrack/Widget\"></script>";

        /// <summary>
        /// Called by File Transformation. Payload is a JObject with a "contents" key
        /// holding the current HTML string. We inject our script tag and write it back.
        /// </summary>
        public static void PatchIndexHtml(object payload)
        {
            try
            {
                var contents = GetContents(payload);
                if (string.IsNullOrEmpty(contents)) return;
                if (contents.Contains(Marker, StringComparison.Ordinal)) return;
                if (!contents.Contains("</body>", StringComparison.OrdinalIgnoreCase)) return;

                var modified = contents.Replace("</body>",
                    $"{Marker}{ScriptTag}</body>",
                    StringComparison.OrdinalIgnoreCase);

                SetContents(payload, modified);
            }
            catch
            {
                // Fail silently — File Transformation will log its own errors
            }
        }

        private static string? GetContents(object payload)
        {
            var type = payload.GetType();

            // Regular POCO / anonymous type property
            var prop = type.GetProperty("contents", BindingFlags.Public | BindingFlags.Instance)
                    ?? type.GetProperty("Contents", BindingFlags.Public | BindingFlags.Instance);
            if (prop != null)
                return prop.GetValue(payload) as string;

            // JObject / dictionary indexer — File Transformation may pass a JObject
            var indexer = type.GetProperty("Item",
                BindingFlags.Public | BindingFlags.Instance,
                null, null, new[] { typeof(string) }, null);
            if (indexer != null)
            {
                var val = indexer.GetValue(payload, new object[] { "contents" });
                // JToken.ToString() returns the string value for JValue
                return val?.ToString();
            }

            return null;
        }

        private static void SetContents(object payload, string value)
        {
            var type = payload.GetType();

            var prop = type.GetProperty("contents", BindingFlags.Public | BindingFlags.Instance)
                    ?? type.GetProperty("Contents", BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(payload, value);
                return;
            }

            var indexer = type.GetProperty("Item",
                BindingFlags.Public | BindingFlags.Instance,
                null, null, new[] { typeof(string) }, null);
            indexer?.SetValue(payload, value, new object[] { "contents" });
        }
    }
}
