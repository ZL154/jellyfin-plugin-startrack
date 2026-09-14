using System;
using System.Net;
using System.Net.Http;
using System.Threading;

namespace Jellyfin.Plugin.InternalRating.Letterboxd
{
    /// <summary>
    /// Process-wide back-off for the two Letterboxd fetches Cloudflare now
    /// challenges: <c>/{user}/watchlist/rss/</c> and <c>/{user}/likes/films/</c>.
    ///
    /// WHY THIS EXISTS: measured on a real server, those two paths return
    /// <c>403</c> with <c>cf-mitigated: challenge</c> (Cloudflare's JavaScript
    /// challenge page) while <c>/{user}/rss/</c> — the diary feed — returns 200
    /// from the same client with the same headers. It is not the User-Agent:
    /// Chrome, Feedly, FreshRSS and Googlebot strings are all challenged
    /// identically. A JS challenge cannot be passed by server-side code, so
    /// retrying is pointless.
    ///
    /// And retry is exactly what happened: every user, every sync tick, both
    /// paths — 1,613 challenged requests in 72 hours on a four-user server,
    /// logged as a warning each time and reported to nobody. Beyond the noise,
    /// that volume is the wrong thing to be sending from an IP that the same
    /// users are also trying to log in to Letterboxd from.
    ///
    /// The challenge is issued against the SERVER'S IP, not a user, so one
    /// challenged fetch tells us the answer for every user. Hence one static
    /// gate rather than per-user state: the first challenge closes it for
    /// <see cref="BackoffPeriod"/>, everything skips until then, one probe
    /// re-tests when it expires, and a success re-opens it.
    ///
    /// The diary RSS feed is untouched by this — it still works and still runs.
    /// </summary>
    public static class LetterboxdFeedGate
    {
        /// <summary>
        /// How long to stop asking after a challenge. Cloudflare rules change on
        /// Letterboxd's schedule, not ours; hours is the right order of
        /// magnitude — long enough to stop the hammering, short enough that a
        /// rule being lifted is noticed the same day.
        /// </summary>
        public static readonly TimeSpan BackoffPeriod = TimeSpan.FromHours(6);

        private static long _challengedUntilTicks;   // 0 = open
        private static long _firstChallengedTicks;   // when it first closed; kept across renewals for the UI
        private static long _challengeCount;

        /// <summary>True while fetches should be skipped.</summary>
        public static bool IsClosed => DateTime.UtcNow.Ticks < Interlocked.Read(ref _challengedUntilTicks);

        /// <summary>When the gate first closed, or null if open. For the UI.</summary>
        public static DateTime? ClosedSince
        {
            get
            {
                if (!IsClosed) return null;
                var t = Interlocked.Read(ref _firstChallengedTicks);
                return t == 0 ? null : new DateTime(t, DateTimeKind.Utc);
            }
        }

        /// <summary>When the next probe is allowed, or null if open.</summary>
        public static DateTime? RetryAt
            => IsClosed ? new DateTime(Interlocked.Read(ref _challengedUntilTicks), DateTimeKind.Utc) : null;

        /// <summary>How many challenges have been seen since the gate first closed.</summary>
        public static long ChallengeCount => Interlocked.Read(ref _challengeCount);

        /// <summary>
        /// True when this response is Cloudflare telling us to run JavaScript —
        /// something a server cannot do. Keyed on the <c>cf-mitigated</c> header,
        /// which Cloudflare sets on exactly this outcome, with the 403 as a
        /// sanity check so an origin 403 for some other reason is not mistaken
        /// for it.
        /// </summary>
        public static bool IsChallenge(HttpResponseMessage response)
        {
            if (response.StatusCode != HttpStatusCode.Forbidden) return false;
            return response.Headers.TryGetValues("cf-mitigated", out var values)
                && string.Join(",", values).Contains("challenge", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Record a challenge. Returns true if this call is the one that closed
        /// the gate (so the caller can log once, loudly, instead of every time).
        /// </summary>
        public static bool NoteChallenge()
        {
            var now = DateTime.UtcNow;
            var wasOpen = !IsClosed;
            Interlocked.Exchange(ref _challengedUntilTicks, (now + BackoffPeriod).Ticks);
            Interlocked.Increment(ref _challengeCount);
            if (wasOpen)
            {
                Interlocked.Exchange(ref _firstChallengedTicks, now.Ticks);
                return true;
            }
            return false;
        }

        /// <summary>A fetch got through: re-open fully and forget the history.</summary>
        public static void NoteSuccess()
        {
            Interlocked.Exchange(ref _challengedUntilTicks, 0);
            Interlocked.Exchange(ref _firstChallengedTicks, 0);
            Interlocked.Exchange(ref _challengeCount, 0);
        }

        /// <summary>Test hook.</summary>
        internal static void Reset() => NoteSuccess();
    }
}
