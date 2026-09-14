using Jellyfin.Plugin.InternalRating.Letterboxd;
using Xunit;

namespace Jellyfin.Plugin.InternalRating.Tests
{
    /// <summary>
    /// Reading FlareSolverr's /v1 response. The fixture is the shape a real
    /// FlareSolverr 3.5.0 returned for a challenged Letterboxd page on
    /// 2026-09-14, with the HTML body shortened.
    /// </summary>
    public class FlareSolverrClientTests
    {
        private const string Solved = @"{
          ""status"": ""ok"",
          ""message"": ""Challenge solved!"",
          ""solution"": {
            ""url"": ""https://letterboxd.com/h201ha/likes/films/"",
            ""status"": 200,
            ""cookies"": [
              { ""name"": ""com.xk72.webparts.csrf"", ""value"": ""abc123"", ""domain"": ""letterboxd.com"" },
              { ""name"": ""cf_clearance"", ""value"": ""XyZ.clearance.token"", ""domain"": "".letterboxd.com"" }
            ],
            ""userAgent"": ""Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36"",
            ""response"": ""<html><head><title>Daniel's liked films</title></head><body>...</body></html>""
          },
          ""startTimestamp"": 1, ""endTimestamp"": 2, ""version"": ""3.5.0""
        }";

        [Fact]
        public void ASolvedResponseYieldsBodyCookiesAndUserAgent()
        {
            var r = FlareSolverrClient.Parse(Solved, "https://letterboxd.com/h201ha/likes/films/");

            Assert.NotNull(r);
            Assert.Equal(200, r!.Status);
            Assert.Contains("liked films", r.Body);
            Assert.StartsWith("Mozilla/5.0 (X11; Linux x86_64)", r.UserAgent);
            Assert.True(r.HasClearance);
            // Both cookies, as one header a plain HttpClient can send back.
            Assert.Contains("cf_clearance=XyZ.clearance.token", r.CookieHeader);
            Assert.Contains("com.xk72.webparts.csrf=abc123", r.CookieHeader);
            Assert.Contains("; ", r.CookieHeader);
        }

        [Fact]
        public void ASolverErrorIsNullNotAnException()
        {
            // FlareSolverr reports its own failures as status "error" with a
            // message; a scheduled task must not throw on that.
            var r = FlareSolverrClient.Parse(@"{""status"":""error"",""message"":""Error solving the challenge. Timeout after 60.0 seconds.""}", "u");
            Assert.Null(r);
        }

        [Fact]
        public void NonJsonIsNullNotAnException()
        {
            Assert.Null(FlareSolverrClient.Parse("<html>502 Bad Gateway</html>", "u"));
            Assert.Null(FlareSolverrClient.Parse(string.Empty, "u"));
        }

        [Fact]
        public void WithoutAClearanceCookieHasClearanceIsFalse()
        {
            // A solve that produced no cf_clearance (page was not challenged)
            // must not be cached as a clearance.
            var r = FlareSolverrClient.Parse(@"{""status"":""ok"",""solution"":{""status"":200,""cookies"":[{""name"":""a"",""value"":""b""}],""userAgent"":""UA"",""response"":""x""}}", "u");
            Assert.NotNull(r);
            Assert.False(r!.HasClearance);
        }

        [Fact]
        public void TheClearanceCacheOnlyAcceptsARealClearance()
        {
            LetterboxdClearance.Drop();
            var noClearance = FlareSolverrClient.Parse(@"{""status"":""ok"",""solution"":{""status"":200,""cookies"":[],""userAgent"":""UA"",""response"":""x""}}", "u")!;
            LetterboxdClearance.Set(noClearance);
            Assert.False(LetterboxdClearance.Has);

            var withClearance = FlareSolverrClient.Parse(Solved, "u")!;
            LetterboxdClearance.Set(withClearance);
            Assert.True(LetterboxdClearance.Has);
            Assert.Equal(withClearance.UserAgent, LetterboxdClearance.Current!.Value.UserAgent);

            LetterboxdClearance.Drop();
            Assert.False(LetterboxdClearance.Has);
        }
    }
}
