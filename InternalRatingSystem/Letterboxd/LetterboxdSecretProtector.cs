using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Jellyfin.Plugin.InternalRating.Letterboxd
{
    /// <summary>
    /// Encrypts the Letterboxd account secrets (password, raw Cloudflare cookies)
    /// at rest so <c>letterboxd.json</c> never holds them in plaintext.
    ///
    /// WHY THIS EXISTS AT ALL: Letterboxd has no public write API. The only way
    /// to push a rating back is to hold the user's actual letterboxd.com
    /// password and drive the website's own session endpoints. That is a real
    /// cost, so the least StarTrack can do is not leave the password sitting in
    /// readable JSON next to the ratings.
    ///
    /// THREAT MODEL — read this before trusting it:
    ///   * PROTECTS against letterboxd.json being copied out on its own: into a
    ///     backup, a git repo, a screenshot, or pasted into a GitHub issue.
    ///     The ciphertext is useless without the separate key ring.
    ///   * DOES NOT protect against anyone with read access to the whole
    ///     Jellyfin data volume, because the plugin has to be able to read the
    ///     key ring unattended, so the key necessarily lives on the same disk.
    ///     On Linux there is no OS keystore to wrap it with (DPAPI is
    ///     Windows-only). That ceiling is inherent to unattended self-hosted
    ///     secret storage without an external KMS — no plugin can beat it.
    ///
    /// The key ring lives beside the plugin's other data, under
    /// &lt;jellyfin-data&gt;/data/InternalRating/letterboxd-keys/.
    /// </summary>
    internal static class LetterboxdSecretProtector
    {
        private const string Purpose = "StarTrack.Letterboxd.Secrets.v1";

        /// <summary>
        /// Marks a value as ciphertext. Values without it are treated as legacy
        /// plaintext and re-encrypted on the next save, so enabling encryption
        /// never strands an existing install.
        /// </summary>
        private const string Prefix = "enc:v1:";

        /// <summary>
        /// Set once at startup from IApplicationPaths (see PluginServiceRegistrator).
        /// Also the test hook — tests point it at a temp directory.
        /// </summary>
        internal static string? KeyDirectory { get; set; }

        private static IDataProtector? _protector;
        private static readonly object _lock = new();

        /// <summary>Test hook: drop the cached protector so the next access re-derives it.</summary>
        internal static void ResetForTesting()
        {
            lock (_lock) { _protector = null; }
        }

        private static IDataProtector Protector
        {
            get
            {
                if (_protector != null) return _protector;
                lock (_lock)
                {
                    _protector ??= Create();
                    return _protector;
                }
            }
        }

        private static IDataProtector Create()
        {
            var dir = KeyDirectory ?? Path.Combine(AppContext.BaseDirectory, "letterboxd-keys");
            Directory.CreateDirectory(dir);
            TryRestrictPermissions(dir);
            return DataProtectionProvider.Create(new DirectoryInfo(dir)).CreateProtector(Purpose);
        }

        // Best effort. Jellyfin containers run as a single PUID/PGID user, so this
        // mainly keeps the key ring off any world-readable default umask. A chmod
        // failure must never block startup.
        private static void TryRestrictPermissions(string dir)
        {
            if (OperatingSystem.IsWindows()) return;
            try
            {
                File.SetUnixFileMode(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
                // Non-fatal: the key ring still works, it's just not tightened.
            }
        }

        /// <summary>
        /// Encrypts a secret for storage. Null/empty pass through unchanged so
        /// "not set" stays distinguishable from "set to empty".
        /// </summary>
        internal static string? Protect(string? plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return plaintext;
            try
            {
                return Prefix + Protector.Protect(plaintext);
            }
            catch (Exception)
            {
                // Refuse to silently persist plaintext if protection is broken —
                // the caller treats null as "could not store" and surfaces it.
                return null;
            }
        }

        /// <summary>
        /// Decrypts a stored secret. An unprefixed value is legacy plaintext and
        /// is returned as-is. Returns null when a prefixed value cannot be
        /// decrypted (key ring rotated, missing or corrupt) so callers can tell
        /// "no password" apart from "password present but unreadable" and ask
        /// the user to re-enter it rather than silently syncing nothing.
        /// </summary>
        internal static string? Unprotect(string? stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;
            if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;

            try
            {
                return Protector.Unprotect(stored[Prefix.Length..]);
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        /// <summary>True when the value is already encrypted at rest.</summary>
        internal static bool IsProtected(string? stored) =>
            !string.IsNullOrEmpty(stored) && stored.StartsWith(Prefix, StringComparison.Ordinal);
    }
}
