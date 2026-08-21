using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Letterboxd
{
    /// <summary>
    /// Ties the pieces together for one user: load settings, decrypt the
    /// credentials, sign in to letterboxd.com, push, then persist the outcome.
    ///
    /// Split from <see cref="LetterboxdPushService"/> so the orchestration logic
    /// (what to write, what to skip) stays unit-testable against a fake writer,
    /// while credential handling and session lifetime live here where they can
    /// be reasoned about in one place.
    /// </summary>
    public sealed class LetterboxdPushRunner
    {
        private readonly LetterboxdSettingsRepository _settings;
        private readonly LetterboxdPushService _push;
        private readonly ILogger<LetterboxdPushRunner> _logger;

        public LetterboxdPushRunner(
            LetterboxdSettingsRepository settings,
            LetterboxdPushService push,
            ILogger<LetterboxdPushRunner> logger)
        {
            _settings = settings;
            _push     = push;
            _logger   = logger;
        }

        /// <summary>
        /// Runs a push for one user. Never throws — every failure is reported
        /// through the result and persisted to LastPushError, so the UI can show
        /// a reason instead of a silent "0 pushed".
        /// </summary>
        /// <param name="maxFilms">Per-run cap. Interactive callers pass a small value so the request returns promptly.</param>
        /// <param name="delayMs">Pace between networked films.</param>
        public async Task<LetterboxdPushResult> RunForUserAsync(
            string userId, CancellationToken ct = default, int maxFilms = 200, int delayMs = 250)
        {
            var result = new LetterboxdPushResult();
            var settings = await _settings.GetAsync(userId).ConfigureAwait(false);

            if (settings.Direction is not (LetterboxdDirection.ExportOnly or LetterboxdDirection.TwoWay))
                return result;

            if (string.IsNullOrWhiteSpace(settings.Username))
            {
                result.Error = "No Letterboxd username is linked.";
                await PersistAsync(userId, result).ConfigureAwait(false);
                return result;
            }

            var password = LetterboxdSecretProtector.Unprotect(settings.PasswordEnc);
            if (string.IsNullOrEmpty(password))
            {
                // Distinguish "never set" from "set but unreadable". The second
                // means the Data Protection key ring was rotated or lost, and the
                // only fix is the user re-entering it — saying so beats a generic
                // failure they cannot act on.
                result.Error = !string.IsNullOrEmpty(settings.PasswordEnc)
                    ? "Stored Letterboxd password could not be decrypted (the key ring changed). Please re-enter it."
                    : "Pushing to Letterboxd needs the account password, which has not been set.";
                await PersistAsync(userId, result).ConfigureAwait(false);
                return result;
            }

            using var session = new LetterboxdSession(_logger, settings.UserAgent);
            session.SeedRawCookies(LetterboxdSecretProtector.Unprotect(settings.RawCookiesEnc));

            var auth = await session.AuthenticateAsync(settings.Username, password, ct).ConfigureAwait(false);
            if (!auth.Ok)
            {
                result.Error = auth.Message ?? auth.Status.ToString();
                await PersistAsync(userId, result).ConfigureAwait(false);
                return result;
            }

            var writer = new LetterboxdWriteService(session, _logger);
            result = await _push.PushAsync(userId, writer, settings, settings.PushDiary, ct, maxFilms, delayMs)
                                .ConfigureAwait(false);

            await PersistAsync(userId, result).ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// Verifies credentials without writing anything. Backs the "Verify
        /// login" button, so a wrong password or a Cloudflare block surfaces the
        /// moment it is saved rather than silently an hour later.
        /// </summary>
        public async Task<LetterboxdAuthResult> VerifyAsync(
            string username, string password, string? rawCookies, string? userAgent, CancellationToken ct = default)
        {
            using var session = new LetterboxdSession(_logger, userAgent);
            session.SeedRawCookies(rawCookies);
            return await session.AuthenticateAsync(username, password, ct).ConfigureAwait(false);
        }

        private Task PersistAsync(string userId, LetterboxdPushResult r) =>
            _settings.SetPushStateAsync(
                userId,
                r.Error == null ? DateTime.UtcNow : null,
                r.TotalWritten,
                r.Error);
    }
}
