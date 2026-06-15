using System;

namespace Jellyfin.Plugin.InternalRating.ExternalSync
{
    /// <summary>StarTrack stores 0.5–5.0 half-stars; Trakt/Simkl use integer 1–10.
    /// Rating scale verified from RatingController (0.5 ≤ Stars ≤ 5).</summary>
    public static class RatingScale
    {
        public static int ToService10(double stars)
            => Math.Clamp((int)Math.Round(stars * 2, MidpointRounding.AwayFromZero), 1, 10);

        public static double FromService10(int rating)
            => Math.Clamp(rating, 1, 10) / 2.0;
    }
}
