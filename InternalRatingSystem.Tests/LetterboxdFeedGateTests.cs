using System.Net;
using System.Net.Http;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using Xunit;

namespace Jellyfin.Plugin.InternalRating.Tests
{
    /// <summary>
    /// The back-off for Letterboxd's Cloudflare-challenged feeds. The
    /// behaviour under test is the one that was measured missing: 1,613
    /// identical challenged requests in 72 hours, because nothing remembered
    /// the answer between ticks.
    /// </summary>
    [Collection("LetterboxdFeedGate")] // static state — never run these in parallel with each other
    public class LetterboxdFeedGateTests
    {
        public LetterboxdFeedGateTests() => LetterboxdFeedGate.Reset();

        private static HttpResponseMessage Response(HttpStatusCode code, string? cfMitigated = null)
        {
            var r = new HttpResponseMessage(code);
            if (cfMitigated != null) r.Headers.TryAddWithoutValidation("cf-mitigated", cfMitigated);
            return r;
        }

        // ---- recognising a challenge ----

        [Fact]
        public void ACloudflareChallengeIsA403WithTheMitigatedHeader()
        {
            Assert.True(LetterboxdFeedGate.IsChallenge(Response(HttpStatusCode.Forbidden, "challenge")));
        }

        [Fact]
        public void AnOrdinary403IsNotMistakenForOne()
        {
            // An origin 403 (private profile, say) has no cf-mitigated header and
            // must not close the gate for every other user on the server.
            Assert.False(LetterboxdFeedGate.IsChallenge(Response(HttpStatusCode.Forbidden)));
        }

        [Fact]
        public void TheHeaderAloneWithoutA403IsNotOne()
        {
            Assert.False(LetterboxdFeedGate.IsChallenge(Response(HttpStatusCode.OK, "challenge")));
        }

        // ---- the back-off ----

        [Fact]
        public void StartsOpen()
        {
            Assert.False(LetterboxdFeedGate.IsClosed);
            Assert.Null(LetterboxdFeedGate.ClosedSince);
            Assert.Null(LetterboxdFeedGate.RetryAt);
        }

        [Fact]
        public void OneChallengeClosesTheGateForTheBackoffPeriod()
        {
            LetterboxdFeedGate.NoteChallenge();

            Assert.True(LetterboxdFeedGate.IsClosed);
            Assert.NotNull(LetterboxdFeedGate.ClosedSince);
            var retry = LetterboxdFeedGate.RetryAt!.Value - LetterboxdFeedGate.ClosedSince!.Value;
            Assert.InRange(retry, LetterboxdFeedGate.BackoffPeriod - System.TimeSpan.FromSeconds(5), LetterboxdFeedGate.BackoffPeriod + System.TimeSpan.FromSeconds(5));
        }

        [Fact]
        public void OnlyTheFirstChallengeReportsThatItClosedTheGate()
        {
            // This is what turns 1,613 log lines into one.
            Assert.True(LetterboxdFeedGate.NoteChallenge());
            Assert.False(LetterboxdFeedGate.NoteChallenge());
            Assert.False(LetterboxdFeedGate.NoteChallenge());
            Assert.Equal(3, LetterboxdFeedGate.ChallengeCount);
        }

        [Fact]
        public void RepeatedChallengesKeepTheOriginalClosedSinceForTheUi()
        {
            // "Blocked since 2 days ago" must not reset to "since just now" on
            // every re-probe that fails.
            LetterboxdFeedGate.NoteChallenge();
            var first = LetterboxdFeedGate.ClosedSince;
            LetterboxdFeedGate.NoteChallenge();
            Assert.Equal(first, LetterboxdFeedGate.ClosedSince);
        }

        [Fact]
        public void ASuccessReopensAndForgets()
        {
            LetterboxdFeedGate.NoteChallenge();
            LetterboxdFeedGate.NoteSuccess();

            Assert.False(LetterboxdFeedGate.IsClosed);
            Assert.Null(LetterboxdFeedGate.ClosedSince);
            Assert.Equal(0, LetterboxdFeedGate.ChallengeCount);
        }
    }
}
