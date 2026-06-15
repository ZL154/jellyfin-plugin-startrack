using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.ExternalSync;
using Jellyfin.Plugin.InternalRating.ExternalSync.Providers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Controllers
{
    /// <summary>
    /// Export / import + OAuth / external-sync endpoints for StarTrack ratings.
    /// All routes require a valid Jellyfin session token.
    /// Route prefix: /Plugins/StarTrack/ExternalSync
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("Plugins/StarTrack/ExternalSync")]
    [Produces(MediaTypeNames.Application.Json)]
    public sealed class ExternalSyncController : ControllerBase
    {
        // ------------------------------------------------------------------ //
        // Server-side cache for in-flight device-code flows.
        // Key = "{userId}:{providerKey}" (lowercase), Value = (DeviceCode, ExpiresAt)
        // ------------------------------------------------------------------ //
        private static readonly ConcurrentDictionary<string, (string DeviceCode, DateTime ExpiresAt)>
            _pendingCodes = new(StringComparer.OrdinalIgnoreCase);

        // Trakt API constants
        private const string TraktCodeEndpoint  = "https://api.trakt.tv/oauth/device/code";
        private const string TraktTokenEndpoint = "https://api.trakt.tv/oauth/device/token";

        private readonly RatingGatherer _gatherer;
        private readonly ExternalIdResolver _resolver;
        private readonly FileExportService _exportService;
        private readonly IAuthorizationContext _authContext;
        private readonly ILogger<ExternalSyncController> _logger;

        // New dependencies for OAuth / sync endpoints
        private readonly DeviceCodeOAuth _deviceCodeOAuth;
        private readonly SyncOrchestrator _orchestrator;
        private readonly ExternalSyncSettingsRepository _settingsRepo;
        private readonly IEnumerable<IExternalRatingProvider> _providers;
        private readonly IUserManager _userManager;

        // HttpClient for Simkl PIN flow (GET-based, not using DeviceCodeOAuth helper)
        private readonly HttpClient _simklHttp;

        public ExternalSyncController(
            RatingGatherer gatherer,
            ExternalIdResolver resolver,
            FileExportService exportService,
            IAuthorizationContext authContext,
            ILogger<ExternalSyncController> logger,
            DeviceCodeOAuth deviceCodeOAuth,
            SyncOrchestrator orchestrator,
            ExternalSyncSettingsRepository settingsRepo,
            IEnumerable<IExternalRatingProvider> providers,
            IUserManager userManager,
            IHttpClientFactory httpClientFactory)
        {
            _gatherer        = gatherer;
            _resolver        = resolver;
            _exportService   = exportService;
            _authContext     = authContext;
            _logger          = logger;
            _deviceCodeOAuth = deviceCodeOAuth;
            _orchestrator    = orchestrator;
            _settingsRepo    = settingsRepo;
            _providers       = providers;
            _userManager     = userManager;
            _simklHttp       = httpClientFactory.CreateClient();
        }

        // ================================================================== //
        // File export / import (existing)
        // ================================================================== //

        /// <summary>
        /// Exports the current user's StarTrack ratings as a downloadable CSV or JSON file.
        /// GET /Plugins/StarTrack/ExternalSync/Export?format=csv|json
        /// </summary>
        [HttpGet("Export")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> Export([FromQuery] string format = "csv")
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            var userIdStr = userId.Value.ToString("N");
            _logger.LogInformation("[StarTrack] ExternalSync Export request from user={User}, format={Fmt}", userIdStr, format);

            var ratings = await _gatherer.GatherAsync(userIdStr).ConfigureAwait(false);
            var dateSuffix = DateTime.UtcNow.ToString("yyyy-MM-dd");

            bool useJson = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);

            if (useJson)
            {
                var json = _exportService.BuildJson(ratings);
                var jsonBytes = Encoding.UTF8.GetBytes(json);
                return File(jsonBytes, "application/json", $"startrack-ratings-{dateSuffix}.json");
            }
            else
            {
                var csv = _exportService.BuildLetterboxdCsv(ratings);
                var csvBytes = Encoding.UTF8.GetBytes(csv);
                return File(csvBytes, "text/csv", $"startrack-ratings-{dateSuffix}.csv");
            }
        }

        /// <summary>
        /// Imports ratings from a CSV or JSON file body into the current user's StarTrack library.
        /// POST /Plugins/StarTrack/ExternalSync/Import?format=csv|json
        /// </summary>
        [HttpPost("Import")]
        [RequestSizeLimit(5 * 1024 * 1024)]
        [Consumes("text/csv", "text/plain", "application/json", "application/octet-stream")]
        [ProducesResponseType(typeof(SyncResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
        public async Task<IActionResult> Import([FromQuery] string format = "csv")
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            var userIdStr = userId.Value.ToString("N");
            var userName  = GetCurrentUserName();

            _logger.LogInformation("[StarTrack] ExternalSync Import request from user={User}, format={Fmt}, contentLength={Len}",
                userIdStr, format, Request.ContentLength);

            // Cap uploads at 5 MB to prevent OOM via unbounded MemoryStream.
            const long MaxBytes = 5L * 1024 * 1024;
            if (Request.ContentLength > MaxBytes)
                return StatusCode(StatusCodes.Status413PayloadTooLarge, "Upload exceeds 5 MB.");

            // Buffer the body
            using var ms = new System.IO.MemoryStream();
            var buf = new byte[64 * 1024];
            int read;
            long total = 0;
            while ((read = await Request.Body.ReadAsync(buf.AsMemory(0, buf.Length)).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxBytes)
                    return StatusCode(StatusCodes.Status413PayloadTooLarge, "Upload exceeded 5 MB during transfer.");
                ms.Write(buf, 0, read);
            }

            var bodyText = Encoding.UTF8.GetString(ms.ToArray());

            bool useJson = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase)
                           || (Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);

            System.Collections.Generic.IReadOnlyList<ExternalRating> parsed;
            try
            {
                parsed = useJson
                    ? _exportService.ParseJson(bodyText)
                    : _exportService.ParseCsv(bodyText);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] ExternalSync Import parse failed for user={User}", userIdStr);
                return Ok(new SyncResult { Error = ex.Message });
            }

            if (Plugin.Instance?.Repository is not { } repository)
            {
                _logger.LogWarning("[StarTrack] ExternalSync Import: plugin/repository not initialised");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Plugin not ready.");
            }

            var result = new SyncResult();

            foreach (var r in parsed)
            {
                // Try to find the item in the Jellyfin library
                string? itemId;
                try { itemId = _resolver.FindItemId(r); }
                catch (Exception ex) { _logger.LogWarning(ex, "[StarTrack] FindItemId failed for title={Title}", r.Title); itemId = null; }

                if (itemId == null)
                {
                    result.Skipped++;
                    continue;
                }

                // Validate star range (matches RatingController which enforces 0.5–5)
                if (r.Stars < 0.5 || r.Stars > 5)
                {
                    result.Skipped++;
                    continue;
                }

                try
                {
                    await repository.SaveRatingAsync(itemId, userIdStr, userName, r.Stars, null, r.RatedAt)
                        .ConfigureAwait(false);
                    result.Pulled++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[StarTrack] ExternalSync Import: failed to save rating for item={Item}", itemId);
                    result.Error = ex.Message;
                    result.Skipped++;
                }
            }

            _logger.LogInformation("[StarTrack] ExternalSync Import done for user={User}: pulled={P} skipped={S}",
                userIdStr, result.Pulled, result.Skipped);
            return Ok(result);
        }

        // ================================================================== //
        // OAuth device-code flow (Task 15)
        // ================================================================== //

        /// <summary>
        /// Starts a device-code OAuth flow for the specified provider.
        /// POST /Plugins/StarTrack/ExternalSync/{provider}/StartAuth
        /// Currently supports: trakt
        /// </summary>
        [HttpPost("{provider}/StartAuth")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> StartAuth(string provider, CancellationToken ct)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            var pid = ParseProvider(provider);
            if (pid == null)
                return BadRequest(new { error = $"Unknown provider '{provider}'" });

            EvictExpiredCodes();

            if (pid == ProviderId.Trakt)
            {
                var clientId = Plugin.Instance?.Configuration.TraktClientId ?? string.Empty;
                if (string.IsNullOrWhiteSpace(clientId))
                    return BadRequest(new { error = "Trakt isn't set up yet. Ask the server admin to add a Trakt Client ID in the StarTrack plugin settings." });

                var clientSecret = Plugin.Instance?.Configuration.TraktClientSecret ?? string.Empty;

                var bodyForm = new Dictionary<string, string>
                {
                    ["client_id"]     = clientId,
                    ["client_secret"] = clientSecret
                };
                var headers = new Dictionary<string, string>
                {
                    ["trakt-api-version"] = "2",
                    ["trakt-api-key"]     = clientId
                };

                DeviceCodeInfo info;
                try
                {
                    info = await _deviceCodeOAuth.RequestCodeAsync(TraktCodeEndpoint, bodyForm, headers, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[StarTrack] StartAuth Trakt device-code request failed");
                    return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
                }

                // Cache the device_code server-side so PollAuth can use it.
                var cacheKey = $"{userId.Value:N}:{provider.ToLowerInvariant()}";
                var expiresAt = DateTime.UtcNow.AddSeconds(info.ExpiresInSeconds);
                _pendingCodes[cacheKey] = (info.DeviceCode, expiresAt);

                _logger.LogInformation("[StarTrack] StartAuth Trakt: userCode={Code} expiresAt={Exp}",
                    info.UserCode, expiresAt);

                return Ok(new
                {
                    userCode        = info.UserCode,
                    verificationUrl = info.VerificationUrl,
                    interval        = info.IntervalSeconds,
                    expiresIn       = info.ExpiresInSeconds
                });
            }

            if (pid == ProviderId.Simkl)
            {
                var simklClientId = Plugin.Instance?.Configuration.SimklClientId ?? string.Empty;
                if (string.IsNullOrWhiteSpace(simklClientId))
                    return BadRequest(new { error = "Simkl isn't set up yet. Ask the server admin to add a Simkl Client ID in the StarTrack plugin settings." });

                // Simkl PIN flow: GET https://api.simkl.com/oauth/pin?client_id=<id>
                // Returns: { "user_code":"ABC123", "verification_url":"https://simkl.com/pin",
                //            "device_code":"...", "interval":5, "expires_in":900 }
                string simklPinUrl  = $"https://api.simkl.com/oauth/pin?client_id={Uri.EscapeDataString(simklClientId)}";
                string userCode, deviceCode, verificationUrl;
                int    interval, expiresIn;

                try
                {
                    using var pinReq  = new HttpRequestMessage(HttpMethod.Get, simklPinUrl);
                    using var pinResp = await _simklHttp.SendAsync(pinReq, ct).ConfigureAwait(false);
                    pinResp.EnsureSuccessStatusCode();

                    var pinJson = await pinResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var pinDoc = System.Text.Json.JsonDocument.Parse(pinJson);
                    var root = pinDoc.RootElement;

                    // VERIFY at smoke-test: field names per Simkl PIN docs
                    userCode        = root.TryGetProperty("user_code",        out var uc)  ? uc.GetString()  ?? string.Empty : string.Empty;
                    deviceCode      = root.TryGetProperty("device_code",      out var dc)  ? dc.GetString()  ?? string.Empty : string.Empty;
                    verificationUrl = root.TryGetProperty("verification_url", out var vu)  ? vu.GetString()  ?? string.Empty : string.Empty;
                    interval        = root.TryGetProperty("interval",         out var iv)  ? iv.GetInt32()   : 5;
                    expiresIn       = root.TryGetProperty("expires_in",       out var ei)  ? ei.GetInt32()   : 900;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[StarTrack] StartAuth Simkl PIN request failed");
                    return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
                }

                // Cache the user_code (used as the polling key for Simkl PIN flow)
                var cacheKey  = $"{userId.Value:N}:{provider.ToLowerInvariant()}";
                var expiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
                // Store user_code as DeviceCode so PollAuth can use it without extra fields
                _pendingCodes[cacheKey] = (userCode, expiresAt);

                _logger.LogInformation("[StarTrack] StartAuth Simkl: userCode={Code} expiresAt={Exp}", userCode, expiresAt);

                return Ok(new
                {
                    userCode,
                    verificationUrl,
                    interval,
                    expiresIn
                });
            }

            // Yamtrack: uses API token, not device-code — use /Yamtrack/Connect instead
            return BadRequest(new { error = $"Provider '{provider}' does not support device-code auth. Use /Connect for token-based providers." });
        }

        /// <summary>
        /// Polls the token endpoint once for the in-flight device-code flow.
        /// POST /Plugins/StarTrack/ExternalSync/{provider}/PollAuth
        /// Returns {status:"pending"} while the user hasn't approved yet,
        /// or {status:"connected"} on success.
        /// </summary>
        [HttpPost("{provider}/PollAuth")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> PollAuth(string provider, CancellationToken ct)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            var pid = ParseProvider(provider);
            if (pid == null)
                return BadRequest(new { error = $"Unknown provider '{provider}'" });

            EvictExpiredCodes();

            var cacheKey = $"{userId.Value:N}:{provider.ToLowerInvariant()}";
            if (!_pendingCodes.TryGetValue(cacheKey, out var cached))
                return BadRequest(new { error = "No active device-code flow. Call StartAuth first." });

            if (DateTime.UtcNow > cached.ExpiresAt)
            {
                _pendingCodes.TryRemove(cacheKey, out _);
                return BadRequest(new { error = "Device code expired. Call StartAuth to restart." });
            }

            if (pid == ProviderId.Trakt)
            {
                var clientId     = Plugin.Instance?.Configuration.TraktClientId     ?? string.Empty;
                var clientSecret = Plugin.Instance?.Configuration.TraktClientSecret ?? string.Empty;

                var bodyForm = new Dictionary<string, string>
                {
                    ["code"]          = cached.DeviceCode,
                    ["client_id"]     = clientId,
                    ["client_secret"] = clientSecret
                };
                var headers = new Dictionary<string, string>
                {
                    ["trakt-api-version"] = "2",
                    ["trakt-api-key"]     = clientId
                };

                TokenResult? token;
                try
                {
                    token = await _deviceCodeOAuth.PollOnceAsync(TraktTokenEndpoint, bodyForm, headers, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[StarTrack] PollAuth Trakt failed");
                    return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
                }

                if (token == null)
                    return Ok(new { status = "pending" });

                // Token obtained — persist the connection.
                var userIdStr    = userId.Value.ToString("N");
                var providerKey  = pid.Value.ToString();  // "Trakt"
                var conn = await _settingsRepo.GetConnectionAsync(userIdStr, providerKey).ConfigureAwait(false)
                           ?? new ProviderConnection();

                conn.AccessToken    = token.AccessToken;
                conn.RefreshToken   = token.RefreshToken;
                conn.TokenExpiresAt = token.ExpiresAt;
                if (conn.Direction == SyncDirection.Off)
                    conn.Direction = SyncDirection.TwoWay;

                await _settingsRepo.SetConnectionAsync(userIdStr, providerKey, conn).ConfigureAwait(false);

                // Clear the cached device-code now that auth is complete.
                _pendingCodes.TryRemove(cacheKey, out _);

                _logger.LogInformation("[StarTrack] PollAuth Trakt: connected for user={User}", userIdStr);
                return Ok(new { status = "connected" });
            }

            if (pid == ProviderId.Simkl)
            {
                var simklClientId = Plugin.Instance?.Configuration.SimklClientId ?? string.Empty;

                // Simkl PIN poll: GET https://api.simkl.com/oauth/pin/{user_code}?client_id=<id>
                // Returns: { "result":"OK", "access_token":"..." } when authorized
                //          { "result":"KO" }                       when still pending
                // VERIFY at smoke-test: field names per Simkl PIN docs
                var userCode = cached.DeviceCode;  // stored as DeviceCode in StartAuth
                string pollUrl = $"https://api.simkl.com/oauth/pin/{Uri.EscapeDataString(userCode)}?client_id={Uri.EscapeDataString(simklClientId)}";

                string? accessToken = null;
                try
                {
                    using var pollReq  = new HttpRequestMessage(HttpMethod.Get, pollUrl);
                    using var pollResp = await _simklHttp.SendAsync(pollReq, ct).ConfigureAwait(false);

                    // 404/410 = PIN expired or revoked on Simkl's side
                    if (pollResp.StatusCode == System.Net.HttpStatusCode.NotFound ||
                        pollResp.StatusCode == System.Net.HttpStatusCode.Gone)
                    {
                        _pendingCodes.TryRemove(cacheKey, out _);
                        return Ok(new { status = "expired", error = "Simkl PIN expired. Start auth again." });
                    }
                    if (!pollResp.IsSuccessStatusCode)
                        return Ok(new { status = "pending", error = $"Simkl poll returned {(int)pollResp.StatusCode}" });

                    var pollJson = await pollResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var pollDoc = System.Text.Json.JsonDocument.Parse(pollJson);
                    var root = pollDoc.RootElement;

                    var result = root.TryGetProperty("result", out var res) ? res.GetString() : null;
                    if (!string.Equals(result, "OK", StringComparison.OrdinalIgnoreCase))
                        return Ok(new { status = "pending" });

                    accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[StarTrack] PollAuth Simkl failed");
                    return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
                }

                if (string.IsNullOrEmpty(accessToken))
                    return Ok(new { status = "pending" });

                // Token obtained — persist the connection.
                // Simkl tokens do not expire; no refresh token or expiry stored.
                var userIdStr   = userId.Value.ToString("N");
                var providerKey = pid.Value.ToString();  // "Simkl"
                var conn = await _settingsRepo.GetConnectionAsync(userIdStr, providerKey).ConfigureAwait(false)
                           ?? new ProviderConnection();

                conn.AccessToken    = accessToken;
                conn.RefreshToken   = null;           // Simkl: no refresh token
                conn.TokenExpiresAt = null;           // Simkl: tokens do not expire
                if (conn.Direction == SyncDirection.Off)
                    conn.Direction = SyncDirection.TwoWay;

                await _settingsRepo.SetConnectionAsync(userIdStr, providerKey, conn).ConfigureAwait(false);

                _pendingCodes.TryRemove(cacheKey, out _);

                _logger.LogInformation("[StarTrack] PollAuth Simkl: connected for user={User}", userIdStr);
                return Ok(new { status = "connected" });
            }

            return BadRequest(new { error = $"Provider '{provider}' does not support device-code polling." });
        }

        // ================================================================== //
        // Status, direction, sync, disconnect (Task 15 cont.)
        // ================================================================== //

        /// <summary>
        /// Returns connection status for all providers for the current user.
        /// GET /Plugins/StarTrack/ExternalSync/Status
        /// Never returns tokens.
        /// </summary>
        [HttpGet("Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> GetStatus()
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            var userIdStr = userId.Value.ToString("N");
            var statuses  = new List<object>();

            foreach (ProviderId pid in Enum.GetValues<ProviderId>())
            {
                var key  = pid.ToString();
                var conn = await _settingsRepo.GetConnectionAsync(userIdStr, key).ConfigureAwait(false);

                statuses.Add(new
                {
                    provider     = key.ToLowerInvariant(),
                    connected    = conn != null && (!string.IsNullOrEmpty(conn.AccessToken) || !string.IsNullOrEmpty(conn.ApiToken)),
                    direction    = conn?.Direction.ToString() ?? SyncDirection.Off.ToString(),
                    lastSyncedAt = conn?.LastSyncedAt,
                    lastPushed   = conn?.LastPushed ?? 0,
                    lastPulled   = conn?.LastPulled ?? 0,
                    lastError    = conn?.LastError
                });
            }

            return Ok(statuses);
        }

        /// <summary>
        /// Updates the sync direction for a provider connection.
        /// POST /Plugins/StarTrack/ExternalSync/{provider}/SetDirection
        /// Body: { "direction": "Off|ExportOnly|ImportOnly|TwoWay" }
        /// </summary>
        [HttpPost("{provider}/SetDirection")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> SetDirection(string provider, [FromBody] SetDirectionRequest req)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            var pid = ParseProvider(provider);
            if (pid == null)
                return BadRequest(new { error = $"Unknown provider '{provider}'" });

            if (!Enum.TryParse<SyncDirection>(req.Direction, ignoreCase: true, out var direction))
                return BadRequest(new { error = $"Unknown direction '{req.Direction}'" });

            var userIdStr   = userId.Value.ToString("N");
            var providerKey = pid.Value.ToString();
            var conn = await _settingsRepo.GetConnectionAsync(userIdStr, providerKey).ConfigureAwait(false)
                       ?? new ProviderConnection();

            conn.Direction = direction;
            await _settingsRepo.SetConnectionAsync(userIdStr, providerKey, conn).ConfigureAwait(false);

            _logger.LogInformation("[StarTrack] SetDirection {User}/{Key} → {Dir}", userIdStr, providerKey, direction);
            return Ok(new { direction = direction.ToString() });
        }

        /// <summary>
        /// Triggers an immediate sync for the specified provider.
        /// POST /Plugins/StarTrack/ExternalSync/{provider}/Sync
        /// </summary>
        [HttpPost("{provider}/Sync")]
        [ProducesResponseType(typeof(SyncResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> SyncNow(string provider, CancellationToken ct)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            var pid = ParseProvider(provider);
            if (pid == null)
                return BadRequest(new { error = $"Unknown provider '{provider}'" });

            var userIdStr   = userId.Value.ToString("N");
            var providerKey = pid.Value.ToString();

            var conn = await _settingsRepo.GetConnectionAsync(userIdStr, providerKey).ConfigureAwait(false);
            if (conn == null || conn.Direction == SyncDirection.Off)
                return BadRequest(new { error = "Provider not connected or direction is Off." });

            var prov = GetProvider(pid.Value);
            if (prov == null)
                return BadRequest(new { error = $"Provider '{provider}' is not registered in DI." });

            // Resolve username via IUserManager (same pattern as LetterboxdSyncTask).
            string userName = GetCurrentUserName();
            try
            {
                var user = _userManager.GetUserById(userId.Value);
                if (user != null) userName = user.Username;
            }
            catch (Exception ex) { _logger.LogDebug(ex, "[StarTrack] username lookup failed; using fallback"); }

            _logger.LogInformation("[StarTrack] SyncNow {User}/{Key} direction={Dir}", userIdStr, providerKey, conn.Direction);

            var result = await _orchestrator.SyncOneAsync(userIdStr, userName, prov, conn, ct).ConfigureAwait(false);

            // Persist mutated connection state (LastSyncedAt, LastPushed, etc.).
            await _settingsRepo.SetConnectionAsync(userIdStr, providerKey, conn).ConfigureAwait(false);

            return Ok(result);
        }

        /// <summary>
        /// Disconnects the specified provider, removing all stored credentials.
        /// POST /Plugins/StarTrack/ExternalSync/{provider}/Disconnect
        /// </summary>
        [HttpPost("{provider}/Disconnect")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> Disconnect(string provider)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            var pid = ParseProvider(provider);
            if (pid == null)
                return BadRequest(new { error = $"Unknown provider '{provider}'" });

            var userIdStr   = userId.Value.ToString("N");
            var providerKey = pid.Value.ToString();

            await _settingsRepo.RemoveConnectionAsync(userIdStr, providerKey).ConfigureAwait(false);

            // Also evict any pending device-code cache entry.
            _pendingCodes.TryRemove($"{userId.Value:N}:{provider.ToLowerInvariant()}", out _);

            _logger.LogInformation("[StarTrack] Disconnect {User}/{Key}", userIdStr, providerKey);
            return Ok(new { disconnected = true });
        }

        /// <summary>
        /// Stores Yamtrack API credentials (baseUrl + apiToken).
        /// POST /Plugins/StarTrack/ExternalSync/Yamtrack/Connect
        /// Body: { "baseUrl": "...", "apiToken": "..." }
        /// </summary>
        [HttpPost("Yamtrack/Connect")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> YamtrackConnect([FromBody] YamtrackConnectRequest req)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();

            if (string.IsNullOrWhiteSpace(req.BaseUrl) || string.IsNullOrWhiteSpace(req.ApiToken))
                return BadRequest(new { error = "baseUrl and apiToken are required." });

            // Validate the URL is a well-formed absolute http/https URL.
            // We block only the most dangerous SSRF targets (cloud metadata + loopback)
            // while allowing all RFC-1918 private ranges — Yamtrack is normally
            // self-hosted on the LAN, so 192.168.x / 10.x / 172.16-31.x are legitimate.
            if (!Uri.TryCreate(req.BaseUrl, UriKind.Absolute, out var parsedUrl)
                || (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
                return BadRequest(new { error = "baseUrl must be an absolute http/https URL." });

            var host = parsedUrl.Host.ToLowerInvariant();
            if (host == "localhost" || host == "127.0.0.1" || host == "::1"
                || host == "169.254.169.254"
                || host.StartsWith("169.254.", StringComparison.Ordinal))
                return BadRequest(new { error = "baseUrl must not point to loopback or cloud-metadata addresses." });

            var userIdStr   = userId.Value.ToString("N");
            var providerKey = ProviderId.Yamtrack.ToString();

            var conn = await _settingsRepo.GetConnectionAsync(userIdStr, providerKey).ConfigureAwait(false)
                       ?? new ProviderConnection();

            conn.BaseUrl  = req.BaseUrl.TrimEnd('/');
            conn.ApiToken = req.ApiToken;
            if (conn.Direction == SyncDirection.Off)
                conn.Direction = SyncDirection.TwoWay;

            await _settingsRepo.SetConnectionAsync(userIdStr, providerKey, conn).ConfigureAwait(false);

            _logger.LogInformation("[StarTrack] YamtrackConnect {User}: baseUrl={Url}", userIdStr, conn.BaseUrl);
            return Ok(new { connected = true });
        }

        // ================================================================== //
        // Helpers
        // ================================================================== //

        /// <summary>
        /// Removes all entries from <see cref="_pendingCodes"/> whose TTL has passed.
        /// Called at the start of every StartAuth / PollAuth so the static dictionary
        /// stays bounded without needing a hosted background service.
        /// </summary>
        private static void EvictExpiredCodes()
        {
            var now = DateTime.UtcNow;
            foreach (var key in _pendingCodes.Keys.ToArray())
            {
                if (_pendingCodes.TryGetValue(key, out var entry) && entry.ExpiresAt < now)
                    _pendingCodes.TryRemove(key, out _);
            }
        }

        /// <summary>Parses a provider route segment to a <see cref="ProviderId"/>, case-insensitive.</summary>
        private static ProviderId? ParseProvider(string key) =>
            key.ToLowerInvariant() switch
            {
                "trakt"    => ProviderId.Trakt,
                "simkl"    => ProviderId.Simkl,
                "yamtrack" => ProviderId.Yamtrack,
                _          => null
            };

        /// <summary>Looks up a registered <see cref="IExternalRatingProvider"/> by its <see cref="ProviderId"/>.</summary>
        private IExternalRatingProvider? GetProvider(ProviderId id) =>
            _providers.FirstOrDefault(p => p.Id == id);

        // ------------------------------------------------------------------
        // Auth helpers (same approach as RatingController and LetterboxdController)
        // ------------------------------------------------------------------

        private async Task<Guid?> GetCurrentUserIdAsync()
        {
            // Primary: Jellyfin's IAuthorizationContext — most reliable
            try
            {
                var info = await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
                if (info?.UserId != null && info.UserId != Guid.Empty)
                    return info.UserId;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[StarTrack] GetAuthorizationInfo failed; falling back to claims"); }

            // Fallback: parse claims (Jellyfin 10.9–10.11 uses "Jellyfin-UserId")
            var value = User.FindFirst("Jellyfin-UserId")?.Value
                ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("uid")?.Value
                ?? User.FindFirst("sub")?.Value;

            return Guid.TryParse(value, out var id) ? id : null;
        }

        private string GetCurrentUserName()
        {
            return User.FindFirst("Jellyfin-User")?.Value
                ?? User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
                ?? User.Identity?.Name
                ?? "Unknown";
        }

        // ================================================================== //
        // Request DTOs
        // ================================================================== //

        public sealed class SetDirectionRequest
        {
            public string Direction { get; set; } = "Off";
        }

        public sealed class YamtrackConnectRequest
        {
            public string? BaseUrl  { get; set; }
            public string? ApiToken { get; set; }
        }
    }
}
