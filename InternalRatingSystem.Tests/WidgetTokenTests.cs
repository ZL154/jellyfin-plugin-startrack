using System.Text.RegularExpressions;
using Jellyfin.Plugin.InternalRating;
using Xunit;

namespace Jellyfin.Plugin.InternalRating.Tests
{
    /// <summary>
    /// The cache-busting token, and the promise the stale-widget nudge rests on.
    ///
    /// [#20, robwoodok] A browser holding Jellyfin's index.html in its cache
    /// keeps asking for the OLD widget URL, so a plugin update changes nothing
    /// for it — silently. The page can only notice that by comparing the token
    /// it was loaded with against the one the server reports as current, so
    /// that token has to be two things at once: STABLE for an unchanged build
    /// (or every page load cries wolf) and DIFFERENT when widget.js changes
    /// (or the nudge never fires when it matters).
    /// </summary>
    public class WidgetTokenTests
    {
        [Fact]
        public void TheTokenIsStableAcrossCalls()
        {
            // A token that varied per call would make every page think it was
            // stale and nag the user forever.
            Assert.Equal(WidgetAsset.Version, WidgetAsset.Version);
        }

        [Fact]
        public void TheTokenIsAShortHexHash()
        {
            // The widget parses it out of its own src with [A-Za-z0-9]+; a
            // token with other characters would never match and the check
            // would silently do nothing.
            Assert.Matches(new Regex("^[0-9a-f]{8}$"), WidgetAsset.Version);
        }

        [Fact]
        public void TheWidgetUrlCarriesTheTokenTheNudgeLooksFor()
        {
            // The page reads ?v=… from this exact URL shape.
            Assert.Contains("/Plugins/StarTrack/Widget?v=" + WidgetAsset.Version, WidgetAsset.WidgetUrl);
            Assert.Matches(new Regex(@"[?&]v=[A-Za-z0-9]+"), WidgetAsset.WidgetUrl);
        }

        [Fact]
        public void TheInjectedTagIsTheUrlInAScriptElement()
        {
            var tag = WidgetAsset.ScriptTag;
            Assert.StartsWith("<script src=\"", tag);
            Assert.EndsWith("></script>", tag);
            Assert.Contains(WidgetAsset.Version, tag);
        }
    }
}
