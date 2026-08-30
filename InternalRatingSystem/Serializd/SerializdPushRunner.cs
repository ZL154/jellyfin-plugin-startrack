using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Serializd
{
    /// <summary>
    /// Loads settings, decrypts the password, signs in, pushes, persists the
    /// outcome. Split from <see cref="SerializdPushService"/> for the same reason
    /// as the Letterboxd pair: the orchestration stays testable against a fake
    /// writer while credentials and session lifetime live in one place.
    /// </summary>
    public sealed class SerializdPushRunner
    {
        private readonly SerializdSettingsRepository _settings;
        private readonly SerializdPushService _push;
        private readonly ILogger<SerializdPushRunner> _logger;

        public SerializdPushRunner(
            SerializdSettingsRepository settings,
            SerializdPushService push,
            ILogger<SerializdPushRunner> logger)
        {
            _settings = settings;
            _push     = push;
            _logger   = logger;
        }

        /// <summary>
        /// Runs a push for one user. Never throws — failures are persisted to
        /// LastPushError so the UI can show a reason rather than a silent zero.
        /// </summary>
        public async Task<SerializdPushResult> RunForUserAsync(
            string userId, CancellationToken ct = default, int maxItems = 200, int delayMs = 250)
        {
            var result = new SerializdPushResult();
            var settings = await _settings.GetAsync(userId).ConfigureAwait(false);

            if (settings.Direction is not (SerializdDirection.ExportOnly or SerializdDirection.TwoWay))
                return result;

            if (string.IsNullOrWhiteSpace(settings.Email))
            {
                result.Error = "No Serializd account is linked.";
                await PersistAsync(userId, result, null).ConfigureAwait(false);
                return result;
            }

            var password = LetterboxdSecretProtector.Unprotect(settings.PasswordEnc);
            if (string.IsNullOrEmpty(password))
            {
                // "Never set" and "set but undecryptable" need different advice:
                // the second means the key ring changed and only re-entering it
                // will help.
                result.Error = !string.IsNullOrEmpty(settings.PasswordEnc)
                    ? "Stored Serializd password could not be decrypted (the key ring changed). Please re-enter it."
                    : "Pushing to Serializd needs the account password, which has not been set.";
                await PersistAsync(userId, result, null).ConfigureAwait(false);
                return result;
            }

            using var session = new SerializdSession(_logger);
            var auth = await session.AuthenticateAsync(settings.Email, password, ct).ConfigureAwait(false);
            if (!auth.Ok)
            {
                result.Error = auth.Message ?? auth.Status.ToString();
                await PersistAsync(userId, result, null).ConfigureAwait(false);
                return result;
            }

            var writer = new SerializdWriteService(session, _logger);
            result = await _push.PushAsync(userId, writer, settings, ct, maxItems, delayMs).ConfigureAwait(false);

            await PersistAsync(userId, result, auth.Username).ConfigureAwait(false);
            return result;
        }

        /// <summary>Verifies credentials without writing anything.</summary>
        public async Task<SerializdAuthResult> VerifyAsync(string email, string password, CancellationToken ct = default)
        {
            using var session = new SerializdSession(_logger);
            return await session.AuthenticateAsync(email, password, ct).ConfigureAwait(false);
        }

        private Task PersistAsync(string userId, SerializdPushResult r, string? username) =>
            _settings.SetPushStateAsync(
                userId,
                r.Error == null ? DateTime.UtcNow : null,
                r.TotalWritten,
                r.Error,
                username);
    }
}
