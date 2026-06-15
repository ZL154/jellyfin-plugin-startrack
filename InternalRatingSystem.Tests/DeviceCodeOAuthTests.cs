using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.ExternalSync;
using Xunit;

namespace Jellyfin.Plugin.InternalRating.Tests
{
    /// <summary>
    /// Minimal stub HttpMessageHandler: takes a delegate so each test can
    /// control what HTTP responses look like without any live network calls.
    /// </summary>
    internal sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }

    public class DeviceCodeOAuthTests
    {
        // ---------- helpers ------------------------------------------------

        private static HttpClient MakeClient(Func<HttpRequestMessage, HttpResponseMessage> fn)
            => new HttpClient(new StubHandler(fn));

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
            => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

        // ---------- RequestCodeAsync ---------------------------------------

        [Fact]
        public async Task RequestCodeAsync_ParsesTraktStyleResponse()
        {
            const string responseJson = """
            {
                "device_code": "abc-device",
                "user_code": "XYZ123",
                "verification_url": "https://trakt.tv/activate",
                "interval": 5,
                "expires_in": 600
            }
            """;

            var client = MakeClient(_ => Json(responseJson));
            var oauth = new DeviceCodeOAuth(client);

            var info = await oauth.RequestCodeAsync(
                "https://api.trakt.tv/oauth/device/code",
                new Dictionary<string, string> { ["client_id"] = "myclid" },
                headers: null,
                CancellationToken.None);

            Assert.Equal("abc-device", info.DeviceCode);
            Assert.Equal("XYZ123", info.UserCode);
            Assert.Equal("https://trakt.tv/activate", info.VerificationUrl);
            Assert.Equal(5, info.IntervalSeconds);
            Assert.Equal(600, info.ExpiresInSeconds);
        }

        // ---------- PollOnceAsync -----------------------------------------

        [Fact]
        public async Task PollOnceAsync_ReturnsNull_OnAuthorizationPending()
        {
            var client = MakeClient(_ => Json(
                """{"error":"authorization_pending"}""",
                HttpStatusCode.BadRequest));

            var oauth = new DeviceCodeOAuth(client);

            var result = await oauth.PollOnceAsync(
                "https://api.trakt.tv/oauth/device/token",
                new Dictionary<string, string> { ["device_code"] = "abc-device" },
                headers: null,
                CancellationToken.None);

            Assert.Null(result);
        }

        [Fact]
        public async Task PollOnceAsync_ReturnsTokenResult_OnSuccess()
        {
            var before = DateTime.UtcNow;
            const string responseJson = """
            {
                "access_token": "myaccess",
                "refresh_token": "myrefresh",
                "expires_in": 7776000
            }
            """;

            var client = MakeClient(_ => Json(responseJson));
            var oauth = new DeviceCodeOAuth(client);

            var result = await oauth.PollOnceAsync(
                "https://api.trakt.tv/oauth/device/token",
                new Dictionary<string, string> { ["device_code"] = "abc-device" },
                headers: null,
                CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("myaccess", result!.AccessToken);
            Assert.Equal("myrefresh", result.RefreshToken);
            // ExpiresAt should be roughly UtcNow + 7776000 seconds
            Assert.True(result.ExpiresAt > before.AddSeconds(7776000 - 5));
        }

        [Fact]
        public async Task PollOnceAsync_Throws_OnHardError()
        {
            // Any non-200 response that is NOT authorization_pending is a hard error
            var client = MakeClient(_ => Json(
                """{"error":"denied"}""",
                HttpStatusCode.BadRequest));

            var oauth = new DeviceCodeOAuth(client);

            await Assert.ThrowsAsync<HttpRequestException>(() => oauth.PollOnceAsync(
                "https://api.trakt.tv/oauth/device/token",
                new Dictionary<string, string> { ["device_code"] = "abc-device" },
                headers: null,
                CancellationToken.None));
        }

        // ---------- RefreshAsync ------------------------------------------

        [Fact]
        public async Task RefreshAsync_ParsesTokenResponse()
        {
            const string responseJson = """
            {
                "access_token": "newaccess",
                "refresh_token": "newrefresh",
                "expires_in": 7776000
            }
            """;

            var client = MakeClient(_ => Json(responseJson));
            var oauth = new DeviceCodeOAuth(client);

            var result = await oauth.RefreshAsync(
                "https://api.trakt.tv/oauth/token",
                new Dictionary<string, string>
                {
                    ["grant_type"]    = "refresh_token",
                    ["refresh_token"] = "myrefresh",
                    ["client_id"]     = "cid",
                    ["client_secret"] = "csecret"
                },
                headers: null,
                CancellationToken.None);

            Assert.Equal("newaccess", result.AccessToken);
            Assert.Equal("newrefresh", result.RefreshToken);
            Assert.True(result.ExpiresAt > DateTime.UtcNow.AddSeconds(7776000 - 10));
        }

        [Fact]
        public async Task RequestCodeAsync_ForwardsCustomHeaders()
        {
            string? capturedHeader = null;

            var client = MakeClient(req =>
            {
                req.Headers.TryGetValues("trakt-api-version", out var vals);
                capturedHeader = vals != null ? string.Join(",", vals) : null;
                return Json("""
                {
                    "device_code":"d","user_code":"u",
                    "verification_url":"https://x","interval":5,"expires_in":600
                }
                """);
            });

            var oauth = new DeviceCodeOAuth(client);
            await oauth.RequestCodeAsync(
                "https://api.trakt.tv/oauth/device/code",
                new Dictionary<string, string> { ["client_id"] = "cid" },
                new Dictionary<string, string> { ["trakt-api-version"] = "2" },
                CancellationToken.None);

            Assert.Equal("2", capturedHeader);
        }
    }
}
