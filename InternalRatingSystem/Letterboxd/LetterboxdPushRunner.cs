using System;
using System.Linq;
using System.Collections.Generic;
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
        private readonly FlareSolverrClient _solver;

        public LetterboxdPushRunner(
            LetterboxdSettingsRepository settings,
            LetterboxdPushService push,
            ILogger<LetterboxdPushRunner> logger)
        {
            _settings = settings;
            _push     = push;
            _logger   = logger;
            _solver   = new FlareSolverrClient(logger);
        }

        /// <summary>
        /// A sign-in session, seeded with whatever Cloudflare clearance the
        /// server can get its hands on.
        ///
        /// Letterboxd's sign-in is behind a Cloudflare JavaScript challenge. The
        /// original workaround was for the user to paste raw browser cookies
        /// (cf_clearance and friends) plus the matching User-Agent — which is
        /// bound to their IP, expires within the hour, and cannot work at all
        /// for 2FA accounts. It got documented as a feature; it is a debugging
        /// trick. If they did paste cookies, they still win.
        ///
        /// Otherwise, with FlareSolverr configured, do the same thing without
        /// the user: have a real browser load /sign-in/, take the cf_clearance
        /// and User-Agent it earned, and seed the session with those. The
        /// clearance is cached server-wide and reused until Letterboxd rejects
        /// it, so most pushes never touch the solver.
        /// </summary>
        private Task<LetterboxdSession> OpenSessionAsync(LetterboxdUserSettings settings, CancellationToken ct)
            => OpenSessionAsync(LetterboxdSecretProtector.Unprotect(settings.RawCookiesEnc), settings.UserAgent, ct);

        private async Task<LetterboxdSession> OpenSessionAsync(string? manualCookies, string? userAgent, CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(manualCookies))
            {
                var manual = new LetterboxdSession(_logger, userAgent);
                manual.SeedRawCookies(manualCookies);
                return manual;
            }

            if (FlareSolverrClient.IsConfigured)
            {
                if (!LetterboxdClearance.Has)
                {
                    var solved = await _solver.GetAsync(LetterboxdSession.BaseUrl + "/sign-in/", ct).ConfigureAwait(false);
                    if (solved != null && solved.HasClearance)
                    {
                        LetterboxdClearance.Set(solved);
                        _logger.LogInformation("[StarTrack] Letterboxd sign-in clearance obtained via FlareSolverr.");
                    }
                    else
                        _logger.LogWarning("[StarTrack] FlareSolverr could not obtain a Letterboxd sign-in clearance; trying without.");
                }

                var clearance = LetterboxdClearance.Current;
                if (clearance != null)
                {
                    var solvedSession = new LetterboxdSession(_logger, clearance.Value.UserAgent);
                    solvedSession.SeedRawCookies(clearance.Value.CookieHeader);
                    return solvedSession;
                }
            }

            return new LetterboxdSession(_logger, userAgent);
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

            using var session = await OpenSessionAsync(settings, ct).ConfigureAwait(false);

            var auth = await session.AuthenticateAsync(settings.Username, password, ct).ConfigureAwait(false);
            if (!auth.Ok)
            {
                if (auth.Status == LetterboxdAuthStatus.Cloudflare) LetterboxdClearance.Drop();
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
            // [#24, khutede] Two bugs met here.
            //
            // One: this built a bare session from the pasted cookies alone. It
            // never touched FlareSolverr, while a real push did — so a server
            // that was configured correctly still failed "Verify login" with
            // "paste raw browser cookies including cf_clearance", sending the
            // user back to the one route that cannot be made to work reliably.
            //
            // Two: pasted cookies were used and never questioned. A stale or
            // mis-copied cf_clearance is WORSE than none — Cloudflare treats a
            // bad clearance as a signal in itself — so a user who pasted
            // cookies once could never sign in again, however healthy the
            // server's own connection was, and the failure told them to paste
            // more cookies.
            //
            // So: try it their way, then try it without their cookies, then try
            // it on a freshly solved challenge. Whichever works, works.
            var attempts = new List<(string? Cookies, bool FreshSolve)> { (rawCookies, false) };
            if (!string.IsNullOrWhiteSpace(rawCookies)) attempts.Add((null, false));
            if (FlareSolverrClient.IsConfigured)       attempts.Add((null, true));

            LetterboxdAuthResult? last = null;
            foreach (var (cookies, freshSolve) in attempts)
            {
                if (freshSolve) LetterboxdClearance.Drop();

                using var session = await OpenSessionAsync(cookies, userAgent, ct).ConfigureAwait(false);
                last = await session.AuthenticateAsync(username, password, ct).ConfigureAwait(false);

                // Only a Cloudflare block is worth another attempt: a wrong
                // password is wrong however the connection was opened, and
                // hammering sign-in with it is how accounts get locked.
                if (last.Status != LetterboxdAuthStatus.Cloudflare) return last;
            }

            return last ?? new LetterboxdAuthResult(LetterboxdAuthStatus.Cloudflare, "Letterboxd sign-in could not be reached.");
        }

        /// <summary>
        /// Signs in and probes candidate action names for one TMDb film, so the
        /// correct watchlist URL can be established from the site itself rather
        /// than guessed release after release.
        /// </summary>
        public async Task<Dictionary<string, string>> ProbeAsync(
            string userId, int tmdbId, IEnumerable<string> actions, CancellationToken ct = default)
        {
            var settings = await _settings.GetAsync(userId).ConfigureAwait(false);
            var password = LetterboxdSecretProtector.Unprotect(settings.PasswordEnc);
            if (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrEmpty(password))
                return new Dictionary<string, string> { ["_error"] = "no linked account" };

            using var session = await OpenSessionAsync(settings, ct).ConfigureAwait(false);

            var auth = await session.AuthenticateAsync(settings.Username, password, ct).ConfigureAwait(false);
            if (!auth.Ok) return new Dictionary<string, string> { ["_error"] = auth.Message ?? auth.Status.ToString() };

            var writer = new LetterboxdWriteService(session, _logger);
            var film = await writer.ResolveFilmAsync(tmdbId, ct).ConfigureAwait(false);
            if (film == null) return new Dictionary<string, string> { ["_error"] = "film not found on Letterboxd" };

            var report = actions.Any(a => a == "_inspect")
                ? await writer.InspectWatchlistMarkupAsync(film, ct).ConfigureAwait(false)
                : await writer.ProbeActionsAsync(film, actions, ct).ConfigureAwait(false);
            report["_film"] = film.Slug + " (id " + film.FilmId + ")";
            return report;
        }

        private Task PersistAsync(string userId, LetterboxdPushResult r) =>
            _settings.SetPushStateAsync(
                userId,
                r.Error == null ? DateTime.UtcNow : null,
                r.TotalWritten,
                r.Error);
    }
}
