using System.Net.Mime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.InternalRating.Controllers
{
    /// <summary>
    /// Standalone full-screen StarTrack page — returns an HTML document that
    /// loads the widget script directly without the surrounding Jellyfin web
    /// shell, then auto-opens the My Ratings overlay. Designed to be embedded
    /// inside a native client's WebView (Compose Desktop, Android WebView,
    /// iOS WKWebView, etc.) so the plugin's full UI ships to every client
    /// without each client re-implementing it.
    ///
    /// Auth: anonymous endpoint so embedded WebViews can fetch the shell
    /// before they've planted credentials. Accepts a ?token=... query param
    /// so the client can pass the user's access token at navigation time.
    /// The page seeds localStorage with the token so the widget's existing
    /// credential resolver (widget.js line ~252) picks it up like any other
    /// Jellyfin web session.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("Plugins/StarTrack")]
    public class StandalonePageController : ControllerBase
    {
        [HttpGet("StandalonePage")]
        [Produces(MediaTypeNames.Text.Html)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> GetStandalonePage(
            [FromQuery] string? token,
            [FromQuery] string? userId,
            [FromQuery] string? serverId,
            [FromQuery] string? view)
        {
            await Task.CompletedTask.ConfigureAwait(false);

            // Strip anything that could escape the JS string literal.
            // Tokens are base64-ish, userIds are 32-char hex GUIDs — no spaces, quotes, etc.
            static string SafeJs(string? s)
            {
                if (string.IsNullOrEmpty(s)) return string.Empty;
                var sb = new System.Text.StringBuilder(s.Length + 8);
                foreach (var ch in s)
                {
                    if (ch == '\\' || ch == '\'' || ch == '"' || ch == '\n' || ch == '\r' || ch < 32) continue;
                    sb.Append(ch);
                }
                return sb.ToString();
            }

            var safeToken = SafeJs(token);
            var safeUserId = SafeJs(userId);
            var safeServerId = string.IsNullOrEmpty(serverId) ? "jelly-standalone" : SafeJs(serverId);
            var safeView = string.IsNullOrEmpty(view) ? "films" : SafeJs(view);
            var origin = $"{Request.Scheme}://{Request.Host}";

            var html = $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>StarTrack</title>
<link rel=""stylesheet"" href=""/web/css/site.css"" />
<style>
    html, body {{
        margin: 0;
        padding: 0;
        background: #0A0A0F;
        color: #E8E8EE;
        font-family: 'Inter', system-ui, -apple-system, 'Segoe UI', sans-serif;
        overflow: hidden;
        width: 100vw;
        height: 100vh;
    }}
    /* Hide every direct child of body until the StarTrack overlay is
       attached — we never want to flash a half-rendered DOM. */
    body > *:not(#ir-overlay):not(script) {{ display: none !important; }}
    /* Overlay fills the WebView; no Jellyfin chrome around it. */
    #ir-overlay {{
        display: flex !important;
        position: fixed !important;
        inset: 0 !important;
        width: 100vw !important;
        height: 100vh !important;
        z-index: 1 !important;
    }}
    /* The sidebar nav link the widget tries to inject has nowhere to go
       in standalone mode — hide it so it's not a confusing artifact. */
    #ir-nav-link {{ display: none !important; }}
    /* Pre-overlay splash so the user sees something while we boot. */
    #st-splash {{
        position: fixed; inset: 0;
        display: flex; align-items: center; justify-content: center;
        background: #0A0A0F; color: #E0153A;
        font-weight: 800; font-size: 1.1em; letter-spacing: 2px;
        z-index: 2;
    }}
    #st-splash.gone {{ display: none !important; }}
</style>
</head>
<body>
<!-- Hidden sidebar stub. The widget's injectSidebar() looks for
     #mainDrawer (or .navMenuSection / .scrollContainer) to attach
     its nav link to. With this present the widget injects normally
     and our auto-open click then targets #ir-nav-link successfully. -->
<div id=""mainDrawer"" style=""display:none;position:absolute;width:0;height:0;overflow:hidden""></div>
<div id=""st-splash"">★ STARTRACK</div>
<script>
(function() {{
    var TOKEN    = '{safeToken}';
    var USER_ID  = '{safeUserId}';
    var SERVER_ID= '{safeServerId}';
    var INITIAL_VIEW = '{safeView}';
    var ORIGIN   = '{origin}';

    // Seed Jellyfin web's credential storage so widget.js's existing
    // resolver finds the token like any normal Jellyfin web session.
    try {{
        if (TOKEN) {{
            localStorage.setItem('jellyfin_credentials', JSON.stringify({{
                Servers: [{{
                    Id: SERVER_ID,
                    Name: 'Jellyfin',
                    AccessToken: TOKEN,
                    UserId: USER_ID,
                    ManualAddress: ORIGIN,
                    LocalAddress: ORIGIN,
                    Type: 'Server',
                    DateLastAccessed: Date.now(),
                    LastConnectionMode: 1
                }}]
            }}));
        }}
    }} catch (e) {{ console.log('[StarTrack-standalone] seed failed', e); }}

    // Minimal ApiClient shim — widget.js's getCredentials() prefers
    // window.ApiClient over localStorage, so giving it one bypasses the
    // localStorage code path and is more reliable.
    if (!window.ApiClient && TOKEN) {{
        window.ApiClient = {{
            accessToken: function() {{ return TOKEN; }},
            getCurrentUserId: function() {{ return USER_ID; }},
            serverAddress: function() {{ return ORIGIN; }},
            _accessToken: TOKEN,
            _currentUser: USER_ID ? {{ Id: USER_ID }} : null
        }};
    }}

    // Once the overlay is in the DOM, hide the splash so the widget is
    // visible. Up to ~10s before we give up and show whatever we have.
    var splashAttempts = 0;
    var splashTimer = setInterval(function() {{
        splashAttempts++;
        var ov = document.getElementById('ir-overlay');
        if (ov || splashAttempts > 100) {{
            var sp = document.getElementById('st-splash');
            if (sp) sp.classList.add('gone');
            clearInterval(splashTimer);
        }}
    }}, 100);

    // Poll for any widget-injected entry point that triggers openMyRatings()
    // (IIFE-scoped, so we can only invoke it via DOM elements the widget
    // creates). Preference order:
    //   1. #ir-nav-link — sidebar nav entry, what we pre-inject #mainDrawer for
    //   2. .ir-rec-open-btn — View all button inside Recent panel
    //   3. .ir-rec-pill — floating Recent pill (last resort, opens panel not overlay)
    var openAttempts = 0;
    var openTimer = setInterval(function() {{
        openAttempts++;
        var link = document.getElementById('ir-nav-link');
        if (link) {{
            try {{ link.click(); }} catch (e) {{}}
            clearInterval(openTimer);
            return;
        }}
        // Fallback path — click View all in the recent panel if the sidebar
        // injection never succeeded.
        if (openAttempts > 20) {{
            var viewAll = document.querySelector('.ir-rec-open-btn');
            if (viewAll) {{
                try {{ viewAll.click(); }} catch (e) {{}}
                clearInterval(openTimer);
                return;
            }}
        }}
        if (openAttempts > 200) clearInterval(openTimer); // ~20s
    }}, 100);
}})();
</script>
<script src=""{WidgetAsset.WidgetUrl}""></script>
</body>
</html>";

            Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Content(html, "text/html; charset=utf-8");
        }
    }
}
