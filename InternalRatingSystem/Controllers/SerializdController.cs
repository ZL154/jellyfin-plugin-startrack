using System;
using System.Net.Mime;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using Jellyfin.Plugin.InternalRating.Serializd;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Controllers
{
    /// <summary>
    /// Serializd write-back endpoints. Every route is scoped to the calling
    /// user — there is no admin-on-behalf-of variant, because linking an account
    /// here means handing over that account's password and only its owner can do
    /// that meaningfully.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("Plugins/StarTrack/Serializd")]
    [Produces(MediaTypeNames.Application.Json)]
    public class SerializdController : ControllerBase
    {
        private readonly SerializdSettingsRepository _settings;
        private readonly SerializdPushRunner _runner;
        private readonly IAuthorizationContext _authContext;
        private readonly ILogger<SerializdController> _logger;

        public SerializdController(
            SerializdSettingsRepository settings,
            SerializdPushRunner runner,
            IAuthorizationContext authContext,
            ILogger<SerializdController> logger)
        {
            _settings    = settings;
            _runner      = runner;
            _authContext = authContext;
            _logger      = logger;
        }

        /// <summary>The caller's Serializd configuration. Never returns the password.</summary>
        [HttpGet("Account")]
        [ProducesResponseType(typeof(SerializdAccountDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAccount()
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            var s = await _settings.GetAsync(userId.Value.ToString("N")).ConfigureAwait(false);
            return Ok(new SerializdAccountDto
            {
                Email           = s.Email,
                Username        = s.Username,
                Direction       = (int)s.Direction,
                HasPassword     = !string.IsNullOrEmpty(s.PasswordEnc),
                PushSeries      = s.PushSeries,
                PushEpisodes    = s.PushEpisodes,
                PushReviews     = s.PushReviews,
                LastPushedAt    = s.LastPushedAt,
                LastPushedCount = s.LastPushedCount,
                LastPushError   = s.LastPushError
            });
        }

        /// <summary>
        /// Saves the caller's configuration. Omit the password to keep the stored
        /// one; send an empty string to clear it.
        /// </summary>
        [HttpPost("Account")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> SetAccount([FromBody] SerializdAccountRequest req)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            if (req.Direction < 0 || req.Direction > 1) return BadRequest("Unknown sync direction.");
            var direction = (SerializdDirection)req.Direction;

            var email = (req.Email ?? string.Empty).Trim();

            // Refuse to arm an export with no way to sign in, rather than
            // accepting it and failing quietly an hour later.
            var stored = await _settings.GetAsync(userId.Value.ToString("N")).ConfigureAwait(false);
            var willHavePassword = req.Password == null
                ? !string.IsNullOrEmpty(stored.PasswordEnc)
                : req.Password.Length > 0;

            if (direction == SerializdDirection.ExportOnly)
            {
                if (email.Length == 0)
                    return BadRequest("Serializd signs in by email address, so one is required.");
                if (!willHavePassword)
                    return BadRequest("Pushing to Serializd needs the account password. Serializd has no public API tokens.");
            }

            var ok = await _settings.SetAccountAsync(
                userId.Value.ToString("N"), email, req.Password, direction,
                req.PushSeries, req.PushEpisodes, req.PushReviews).ConfigureAwait(false);

            if (!ok)
                return BadRequest("Could not encrypt the credentials for storage, so nothing was saved. StarTrack will not fall back to storing them in plain text.");

            return Ok();
        }

        /// <summary>
        /// Tests a Serializd sign-in without writing anything, so a bad password
        /// is visible immediately instead of an hour later.
        /// </summary>
        [HttpPost("VerifyLogin")]
        [ProducesResponseType(typeof(SerializdVerifyDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> VerifyLogin([FromBody] SerializdVerifyRequest req)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            var s = await _settings.GetAsync(userId.Value.ToString("N")).ConfigureAwait(false);
            var email = string.IsNullOrWhiteSpace(req.Email) ? s.Email : req.Email.Trim();

            // Fall back to the stored password so Verify works without the
            // browser having to hold the secret again just to re-check it.
            var password = string.IsNullOrEmpty(req.Password)
                ? LetterboxdSecretProtector.Unprotect(s.PasswordEnc)
                : req.Password;

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
                return Ok(new SerializdVerifyDto
                {
                    Ok = false,
                    Status = nameof(SerializdAuthStatus.BadCredentials),
                    Message = "Email and password are both required."
                });

            var r = await _runner.VerifyAsync(email, password, HttpContext.RequestAborted).ConfigureAwait(false);
            return Ok(new SerializdVerifyDto
            {
                Ok       = r.Ok,
                Status   = r.Status.ToString(),
                Message  = r.Message,
                Username = r.Username
            });
        }

        /// <summary>Runs a push for the caller immediately and returns the report.</summary>
        [HttpPost("PushNow")]
        [ProducesResponseType(typeof(SerializdPushResult), StatusCodes.Status200OK)]
        public async Task<IActionResult> PushNow()
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            // Smaller cap and tighter pacing than the hourly task: this is a
            // button press and has to come back while the user is still looking
            // at it. Anything left over is reported as remaining and picked up by
            // the task, or by pressing again.
            var r = await _runner.RunForUserAsync(
                userId.Value.ToString("N"), HttpContext.RequestAborted, maxItems: 25, delayMs: 120)
                .ConfigureAwait(false);

            _logger.LogInformation("[StarTrack] Serializd PushNow for {User}: written={W} error={E}",
                userId.Value, r.TotalWritten, r.Error ?? "none");

            return Ok(r);
        }

        private async Task<Guid?> GetCurrentUserIdAsync()
        {
            try
            {
                var info = await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
                if (info?.UserId != null && info.UserId != Guid.Empty)
                    return info.UserId;
            }
            catch { }

            var value = User.FindFirst("Jellyfin-UserId")?.Value
                ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("uid")?.Value
                ?? User.FindFirst("sub")?.Value;
            return Guid.TryParse(value, out var id) ? id : null;
        }

        // ------------------------------------------------------------------
        // DTOs. Every property carries an explicit JsonPropertyName: Jellyfin
        // 10.11 serialises PascalCase by default, and a DTO without them reads
        // back as undefined in the widget — the exact bug that made the
        // Letterboxd push report look like it had done nothing.
        // ------------------------------------------------------------------

        /// <summary>Serializd account state for the settings UI.</summary>
        public sealed class SerializdAccountDto
        {
            [JsonPropertyName("email")]           public string    Email           { get; set; } = string.Empty;
            [JsonPropertyName("username")]        public string?   Username        { get; set; }
            [JsonPropertyName("direction")]       public int       Direction       { get; set; }
            [JsonPropertyName("hasPassword")]     public bool      HasPassword     { get; set; }
            [JsonPropertyName("pushSeries")]      public bool      PushSeries      { get; set; }
            [JsonPropertyName("pushEpisodes")]    public bool      PushEpisodes    { get; set; }
            [JsonPropertyName("pushReviews")]     public bool      PushReviews     { get; set; }
            [JsonPropertyName("lastPushedAt")]    public DateTime? LastPushedAt    { get; set; }
            [JsonPropertyName("lastPushedCount")] public int       LastPushedCount { get; set; }
            [JsonPropertyName("lastPushError")]   public string?   LastPushError   { get; set; }
        }

        /// <summary>Body of POST Account.</summary>
        public sealed class SerializdAccountRequest
        {
            [JsonPropertyName("email")]        public string? Email { get; set; }

            /// <summary>Null keeps the stored password; empty clears it.</summary>
            [JsonPropertyName("password")]     public string? Password { get; set; }

            [JsonPropertyName("direction")]    public int  Direction    { get; set; }
            [JsonPropertyName("pushSeries")]   public bool PushSeries   { get; set; } = true;
            [JsonPropertyName("pushEpisodes")] public bool PushEpisodes { get; set; }
            [JsonPropertyName("pushReviews")]  public bool PushReviews  { get; set; }
        }

        /// <summary>Body of POST VerifyLogin.</summary>
        public sealed class SerializdVerifyRequest
        {
            [JsonPropertyName("email")]    public string? Email { get; set; }
            [JsonPropertyName("password")] public string? Password { get; set; }
        }

        /// <summary>Result of POST VerifyLogin.</summary>
        public sealed class SerializdVerifyDto
        {
            [JsonPropertyName("ok")]       public bool    Ok       { get; set; }
            [JsonPropertyName("status")]   public string? Status   { get; set; }
            [JsonPropertyName("message")]  public string? Message  { get; set; }
            [JsonPropertyName("username")] public string? Username { get; set; }
        }
    }
}
