using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InternalRatingSystem.Tests
{
    /// <summary>
    /// What a Cloudflare block tells the user to do.
    ///
    /// [#24, khutede] It used to say "paste raw browser cookies including
    /// cf_clearance, with the matching User-Agent" and nothing else — so a user
    /// who had already pasted exactly that, correctly, was told to go and do it
    /// again. That route is the least likely of the three to work: Cloudflare
    /// pins the clearance to the User-Agent and the public IP that earned it,
    /// expires it within the hour, and fingerprints the TLS handshake as well.
    /// The message has to name the routes that do work, and say that importing
    /// needs none of them.
    /// </summary>
    public class LetterboxdCloudflareAdviceTests
    {
        /// <summary>GET answers the sign-in page; POST answers the login call.</summary>
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _getCode, _postCode;
            private readonly string _getBody;
            public StubHandler(HttpStatusCode getCode, string getBody, HttpStatusCode postCode)
            { _getCode = getCode; _getBody = getBody; _postCode = postCode; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => Task.FromResult(request.Method == HttpMethod.Post
                    ? new HttpResponseMessage(_postCode) { Content = new StringContent("{}") }
                    : new HttpResponseMessage(_getCode) { Content = new StringContent(_getBody) });
        }

        // What Cloudflare actually serves instead of the sign-in form.
        private const string ChallengePage =
            "<html><head><title>Just a moment...</title></head><body>" +
            "<div id=\"challenge-running\"></div></body></html>";

        private static LetterboxdSession Session(HttpStatusCode getCode, string getBody, HttpStatusCode postCode = HttpStatusCode.OK)
            => new(NullLogger.Instance, "UA/1.0", new StubHandler(getCode, getBody, postCode));

        private static void AssertUsefulAdvice(string? message)
        {
            Assert.NotNull(message);
            // The route that always works has to be named.
            Assert.Contains("letterboxd.com/import", message!, StringComparison.OrdinalIgnoreCase);
            // The part that would have saved khutede a week: a plain import
            // never needed the login they were fighting with.
            Assert.Contains("username and nothing else", message!, StringComparison.OrdinalIgnoreCase);
            // And the honest reason a careful paste can still be refused —
            // measured against real Letterboxd: even a clearance FlareSolverr
            // earned is refused when reused from our own HTTP client.
            Assert.Contains("TLS fingerprint", message!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("FlareSolverr", message!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task AChallengedSignInPageExplainsWhatToDoInstead()
        {
            using var session = Session(HttpStatusCode.OK, ChallengePage);
            var r = await session.AuthenticateAsync("someone", "hunter2");

            Assert.Equal(LetterboxdAuthStatus.Cloudflare, r.Status);
            AssertUsefulAdvice(r.Message);
        }

        [Fact]
        public async Task A403OnSignInSaysItIsNotThePassword()
        {
            // The CSRF bootstrap has to succeed for the POST to be reached.
            using var session = Session(HttpStatusCode.OK, "<html><input name=\"__csrf\" value=\"abc\" /></html>", HttpStatusCode.Forbidden);
            var r = await session.AuthenticateAsync("someone", "hunter2");

            Assert.Equal(LetterboxdAuthStatus.Cloudflare, r.Status);
            Assert.Contains("not your password", r.Message!, StringComparison.OrdinalIgnoreCase);
            AssertUsefulAdvice(r.Message);
        }

        [Fact]
        public async Task ABlockedSignInDoesNotSendTheUserBackToPastingCookies()
        {
            // [#24] The whole defect: the only instruction was "paste raw
            // browser cookies including cf_clearance", given to someone who
            // had already done exactly that, correctly.
            using var session = Session(HttpStatusCode.OK, ChallengePage);
            var r = await session.AuthenticateAsync("someone", "hunter2");

            Assert.DoesNotContain("Paste raw browser cookies", r.Message!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ABlockedSignInNeverBlamesTheCredentials()
        {
            using var session = Session(HttpStatusCode.OK, ChallengePage);
            var r = await session.AuthenticateAsync("someone", "hunter2");

            Assert.NotEqual(LetterboxdAuthStatus.BadCredentials, r.Status);
            Assert.DoesNotContain("rejected that username", r.Message!, StringComparison.OrdinalIgnoreCase);
        }
    }
}
