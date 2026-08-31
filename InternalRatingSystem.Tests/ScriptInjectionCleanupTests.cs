using Jellyfin.Plugin.InternalRating;
using Xunit;

namespace InternalRatingSystem.Tests
{
    /// <summary>
    /// The disk patcher rewrites index.html in place, so a bad cleanup does not
    /// just fail once - it leaves damage behind on every server it touches.
    /// </summary>
    public class ScriptInjectionCleanupTests
    {
        private const string Good =
            "<script src=\"/Plugins/StarTrack/Widget?v=abc12345\"></script>";

        [Fact]
        public void Removes_the_marker_and_a_well_formed_tag()
        {
            var html = "<body><p>hi</p><!-- startrack-widget -->" + Good + "</body>";
            var res = WebInjectionService.StripInjection(html);

            Assert.Equal("<body><p>hi</p></body>", res);
        }

        [Fact]
        public void Removes_a_tag_that_carries_a_reverse_proxy_base_path()
        {
            var html = "<body><script src=\"/jelly/Plugins/StarTrack/Widget?v=abc12345\"></script></body>";
            Assert.Equal("<body></body>", WebInjectionService.StripInjection(html));
        }

        [Fact]
        public void Repairs_the_self_referential_orphans_left_by_the_old_cleanup()
        {
            // These are already sitting in index.html on servers patched by an
            // earlier build: src is nothing but the cache-busting query, so the
            // browser fetches the page itself and parses HTML as a script.
            var html = "<body>"
                     + "<script src=\"?v=1f6eaff8\"></script>"
                     + "<script src=\"?v=14999de1\"></script>"
                     + "<script src=\"?v=e70c4c53\"></script>"
                     + Good
                     + "</body>";

            var res = WebInjectionService.StripInjection(html);

            Assert.DoesNotContain("?v=", res);
            Assert.Equal("<body></body>", res);
        }

        [Fact]
        public void Leaves_unrelated_scripts_alone()
        {
            // A version query is normal on Jellyfin's own bundles; only a src
            // that is ONLY a query is the broken shape.
            var html = "<body><script src=\"main.jellyfin.bundle.js?c72049eb\"></script>"
                     + "<script src=\"/other/plugin.js?v=deadbeef\"></script></body>";

            Assert.Equal(html, WebInjectionService.StripInjection(html));
        }

        [Fact]
        public void An_up_to_date_page_that_still_has_orphans_is_not_considered_clean()
        {
            // Otherwise a server whose widget never changes again keeps its
            // orphans permanently: the repair only runs when the token moves.
            var withJunk = "<body><script src=\"?v=deadbeef\"></script>" + Good + "</body>";
            var clean    = "<body>" + Good + "</body>";

            Assert.True(WebInjectionService.HasOrphanedTags(withJunk));
            Assert.False(WebInjectionService.HasOrphanedTags(clean));
        }

        [Fact]
        public void Is_idempotent_so_repeated_updates_cannot_accumulate()
        {
            var html = "<body><!-- startrack-widget -->" + Good + "</body>";
            var once = WebInjectionService.StripInjection(html);
            Assert.Equal(once, WebInjectionService.StripInjection(once));
        }
    }
}
