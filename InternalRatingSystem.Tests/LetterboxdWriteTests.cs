using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.InternalRating.Letterboxd;
using Xunit;

namespace InternalRatingSystem.Tests
{
    /// <summary>
    /// Covers the Letterboxd write-back payload and the at-rest secret handling.
    ///
    /// The scale assertions here exist because of issue #19: Trakt and Simkl use
    /// a 1–10 integer scale, Letterboxd uses 0.5–5.0 half-stars, and a
    /// well-meaning "convert to the service scale" step would silently halve or
    /// double every rating a user pushes.
    /// </summary>
    public class LetterboxdWriteTests
    {
        private static readonly LetterboxdFilm Film = new("the-batman", "51923", null);
        private static readonly LetterboxdFilm FilmWithProduction = new("dune", "40412", "prod-99");
        private static readonly DateTime Watched = new(2026, 8, 11, 21, 45, 0, DateTimeKind.Utc);

        private static Dictionary<string, object?> Build(
            double? rating = null, bool liked = false, bool rewatch = false,
            string? review = null, bool spoilers = false,
            string endpoint = "/api/v0/log-entries", LetterboxdFilm? film = null)
            => LetterboxdWriteService.BuildPayload(
                endpoint, film ?? Film, Watched, rating, liked, rewatch, review, spoilers);

        // ---- rating scale -------------------------------------------------

        [Theory]
        [InlineData(0.5, 0.5)]
        [InlineData(2.5, 2.5)]
        [InlineData(4.0, 4.0)]
        [InlineData(5.0, 5.0)]
        public void Rating_IsSentOnLetterboxdsOwnHalfStarScale_NotDoubled(double stars, double expected)
        {
            var p = Build(rating: stars);
            Assert.Equal(expected, Assert.IsType<double>(p["rating"]));
        }

        [Fact]
        public void Rating_IsSnappedToHalfStars()
        {
            // 3.3 is not a value Letterboxd accepts; nearest half-star is 3.5.
            Assert.Equal(3.5, Assert.IsType<double>(Build(rating: 3.3)["rating"]));
            Assert.Equal(3.0, Assert.IsType<double>(Build(rating: 3.2)["rating"]));
        }

        [Fact]
        public void Rating_IsClampedIntoRange()
        {
            Assert.Equal(5.0, Assert.IsType<double>(Build(rating: 9.0)["rating"]));
            Assert.Equal(0.5, Assert.IsType<double>(Build(rating: 0.0)["rating"]));
        }

        [Fact]
        public void Rating_IsOmittedEntirelyWhenNull()
        {
            // A watch with no rating must not send rating:0 — Letterboxd would
            // treat that as an actual score.
            Assert.False(Build(rating: null).ContainsKey("rating"));
        }

        // ---- diary details ------------------------------------------------

        [Fact]
        public void DiaryDate_IsInvariantYearMonthDay()
        {
            var details = Assert.IsType<Dictionary<string, object>>(Build()["diaryDetails"]);
            Assert.Equal("2026-08-11", details["diaryDate"]);
        }

        [Fact]
        public void RewatchAndLike_AreCarried()
        {
            var p = Build(liked: true, rewatch: true);
            Assert.True(Assert.IsType<bool>(p["like"]));
            var details = Assert.IsType<Dictionary<string, object>>(p["diaryDetails"]);
            Assert.True(Assert.IsType<bool>(details["rewatch"]));
        }

        // ---- review -------------------------------------------------------

        [Fact]
        public void Review_IsOmittedWhenBlank()
        {
            Assert.False(Build(review: null).ContainsKey("review"));
            Assert.False(Build(review: "   ").ContainsKey("review"));
            Assert.False(Build(review: null).ContainsKey("containsSpoilers"));
        }

        [Fact]
        public void Review_CarriesTextAndSpoilerFlag()
        {
            var p = Build(review: "Loved it.", spoilers: true);
            Assert.Equal("Loved it.", p["review"]);
            Assert.True(Assert.IsType<bool>(p["containsSpoilers"]));
        }

        // ---- film vs production id ----------------------------------------

        [Fact]
        public void LogEntriesEndpoint_UsesFilmId()
        {
            var p = Build(endpoint: "/api/v0/log-entries");
            Assert.Equal("51923", p["filmId"]);
            Assert.False(p.ContainsKey("productionId"));
        }

        [Fact]
        public void ProductionEndpoint_UsesProductionIdWhenAvailable()
        {
            var p = Build(endpoint: "/api/v0/production-log-entries", film: FilmWithProduction);
            Assert.Equal("prod-99", p["productionId"]);
            Assert.False(p.ContainsKey("filmId"));
        }

        [Fact]
        public void ProductionEndpoint_FallsBackToFilmIdWhenNoProductionId()
        {
            // Film has no production record — must still send something usable.
            var p = Build(endpoint: "/api/v0/production-log-entries", film: Film);
            Assert.Equal("51923", p["filmId"]);
        }
    }

    /// <summary>At-rest protection for the Letterboxd password and raw cookies.</summary>
    public class LetterboxdSecretProtectorTests : IDisposable
    {
        private readonly string _dir;

        public LetterboxdSecretProtectorTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "startrack-lb-keys-" + Guid.NewGuid().ToString("N"));
            LetterboxdSecretProtector.KeyDirectory = _dir;
            LetterboxdSecretProtector.ResetForTesting();
        }

        public void Dispose()
        {
            LetterboxdSecretProtector.KeyDirectory = null;
            LetterboxdSecretProtector.ResetForTesting();
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void RoundTrips_ASecret()
        {
            const string secret = "hunter2-with-üñïçø∂é";
            var enc = LetterboxdSecretProtector.Protect(secret);

            Assert.NotNull(enc);
            Assert.NotEqual(secret, enc);
            Assert.DoesNotContain(secret, enc, StringComparison.Ordinal);   // must not be recoverable by eye
            Assert.Equal(secret, LetterboxdSecretProtector.Unprotect(enc));
        }

        [Fact]
        public void MarksCiphertext_SoLegacyPlaintextIsDetectable()
        {
            var enc = LetterboxdSecretProtector.Protect("pw");
            Assert.True(LetterboxdSecretProtector.IsProtected(enc));
            Assert.False(LetterboxdSecretProtector.IsProtected("pw"));
        }

        [Fact]
        public void LegacyPlaintext_PassesThroughUnchanged()
        {
            // A value written before encryption existed must keep working, and
            // get re-encrypted on the next save rather than stranding the user.
            Assert.Equal("old-plaintext", LetterboxdSecretProtector.Unprotect("old-plaintext"));
        }

        [Fact]
        public void NullAndEmpty_PassThrough()
        {
            Assert.Null(LetterboxdSecretProtector.Protect(null));
            Assert.Equal(string.Empty, LetterboxdSecretProtector.Protect(string.Empty));
            Assert.Null(LetterboxdSecretProtector.Unprotect(null));
        }

        [Fact]
        public void UndecryptableCiphertext_ReturnsNull_NotGarbage()
        {
            // Key ring rotated/lost. Callers need to tell this apart from "no
            // password set" so they can ask the user to re-enter it.
            Assert.Null(LetterboxdSecretProtector.Unprotect("enc:v1:not-actually-valid-ciphertext"));
        }
    }
}
