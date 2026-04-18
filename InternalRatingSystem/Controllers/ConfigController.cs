using System.IO;
using System.Net.Mime;
using System.Reflection;
using System.Text;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.InternalRating.Controllers
{
    /// <summary>
    /// Public configuration + translation endpoints.
    /// </summary>
    [ApiController]
    [Route("Plugins/StarTrack")]
    public class ConfigController : ControllerBase
    {
        private static readonly string[] SupportedLanguages =
            { "en", "fr", "es", "de", "it", "pt", "zh", "ja" };

        private readonly IConfigurationManager _configManager;

        public ConfigController(IConfigurationManager configManager)
        {
            _configManager = configManager;
        }

        /// <summary>Admin-exposed flags used by widget.js.</summary>
        [HttpGet("PublicConfig")]
        [AllowAnonymous]
        [Produces(MediaTypeNames.Application.Json)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult GetPublicConfig()
        {
            var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var hiddenViews = (cfg.HiddenOverlayViews ?? string.Empty)
                .Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < hiddenViews.Length; i++) hiddenViews[i] = hiddenViews[i].Trim().ToLowerInvariant();
            Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            return Ok(new
            {
                language                     = cfg.Language ?? "en",
                hideRecentButton             = cfg.HideRecentButton,
                hideLetterboxdButton         = cfg.HideLetterboxdButton,
                rateButtonOnlyInMediaItem    = cfg.RateButtonOnlyInMediaItem,
                replaceMediaDetailsRating    = cfg.ReplaceMediaDetailsRating,
                replaceMediaBarRating        = cfg.ReplaceMediaBarRating,
                showRatingsOnPosters         = cfg.ShowRatingsOnPosters,
                postPlaybackRatingPopup      = cfg.PostPlaybackRatingPopup,
                communityRecentMode          = cfg.CommunityRecentMode,
                hiddenOverlayViews           = hiddenViews,
                supportedLanguages           = SupportedLanguages
            });
        }

        /// <summary>Returns the translation bundle for a given language.</summary>
        [HttpGet("Translations/{lang}")]
        [AllowAnonymous]
        [Produces(MediaTypeNames.Application.Json)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetTranslations([FromRoute] string lang)
        {
            if (string.IsNullOrWhiteSpace(lang)) lang = "en";
            lang = lang.ToLowerInvariant();

            // Map common aliases
            if (lang.StartsWith("zh")) lang = "zh";
            if (lang.StartsWith("pt")) lang = "pt";

            var isSupported = false;
            foreach (var l in SupportedLanguages)
            {
                if (l == lang) { isSupported = true; break; }
            }
            if (!isSupported) lang = "en";

            var asm = Assembly.GetExecutingAssembly();
            var res = $"Jellyfin.Plugin.InternalRating.Web.translations.{lang}.json";
            var stream = asm.GetManifestResourceStream(res);
            if (stream == null && lang != "en")
            {
                res = "Jellyfin.Plugin.InternalRating.Web.translations.en.json";
                stream = asm.GetManifestResourceStream(res);
            }
            if (stream == null) return NotFound();

            Response.Headers["Cache-Control"] = "public, max-age=300";
            return File(stream, "application/json; charset=utf-8");
        }

        /// <summary>Admin-only: save the StarTrack configuration.</summary>
        [HttpPost("AdminConfig")]
        [Authorize(Policy = "RequiresElevation")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult SaveAdminConfig([FromBody] PluginConfiguration body)
        {
            if (Plugin.Instance == null) return StatusCode(500);
            Plugin.Instance.UpdateConfiguration(body);
            return Ok();
        }

        /// <summary>Admin-only: get the current configuration (round-trip).</summary>
        [HttpGet("AdminConfig")]
        [Authorize(Policy = "RequiresElevation")]
        [Produces(MediaTypeNames.Application.Json)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult GetAdminConfig()
        {
            return Ok(Plugin.Instance?.Configuration ?? new PluginConfiguration());
        }
    }
}
