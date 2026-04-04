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

            // Plain POCO property
            var prop = type.GetProperty("contents", BindingFlags.Public | BindingFlags.Instance)
                    ?? type.GetProperty("Contents", BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(payload, value);
                return;
            }

            // JObject indexer — the setter expects a JToken, NOT a plain string.
            // Passing a raw string via reflection throws ArgumentException (silently swallowed).
            // Fix: wrap the string in a JValue first, using reflection against the already-loaded assembly.
            var indexer = type.GetProperty("Item",
                BindingFlags.Public | BindingFlags.Instance,
                null, null, new[] { typeof(string) }, null);
            if (indexer == null) return;

            object tokenValue = value; // fallback (may fail, but worth trying)
            try
            {
                var jValueType = type.Assembly.GetType("Newtonsoft.Json.Linq.JValue");
                if (jValueType != null)
                {
                    var ctor = jValueType.GetConstructor(new[] { typeof(string) });
                    if (ctor != null)
                        tokenValue = ctor.Invoke(new object[] { value });
                }
            }
            catch { /* keep plain string fallback */ }

            indexer.SetValue(payload, tokenValue, new object[] { "contents" });
        }
    }
}
