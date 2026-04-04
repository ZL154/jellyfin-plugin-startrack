using System;

namespace Jellyfin.Plugin.InternalRating
{
    /// <summary>
    /// Static callback class invoked by the File Transformation plugin when index.html is served.
    /// The method signature must be public static void MethodName(object payload).
    /// </summary>
    public static class TransformationPatches
    {
        private const string Marker    = "<!-- startrack-widget -->";
        private const string Injection =
            "<!-- startrack-widget -->" +
            "<script src=\"/Plugins/StarTrack/Widget\"></script>";

        /// <summary>
        /// Called by File Transformation plugin. Payload has a "contents" property
        /// containing the current HTML string — we inject the widget script tag.
        /// </summary>
        public static void PatchIndexHtml(object payload)
        {
            try
            {
                var type = payload.GetType();

                // File Transformation passes a JObject; "contents" is lowercase
                var prop = type.GetProperty("contents")
                        ?? type.GetProperty("Contents");

                if (prop == null) return;

                var contents = prop.GetValue(payload) as string;
                if (string.IsNullOrEmpty(contents)) return;
                if (contents.Contains(Marker, StringComparison.Ordinal)) return;
                if (!contents.Contains("</body>", StringComparison.OrdinalIgnoreCase)) return;

                prop.SetValue(payload,
                    contents.Replace("</body>", Injection + "</body>",
                        StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                // Fail silently — File Transformation handles errors
            }
        }
    }
}
