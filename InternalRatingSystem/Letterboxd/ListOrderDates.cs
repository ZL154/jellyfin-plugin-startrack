using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.InternalRating.Letterboxd
{
    /// <summary>
    /// Dates for a list Letterboxd hands us newest-first without dates.
    ///
    /// The ratings list (<c>/films/by/rated-date/</c>) and the likes list are
    /// ordered by when the member rated or liked each film, but the pages
    /// carry no dates. The diary does — for the films that were logged. A
    /// member's first full sync brought in two hundred films they had rated
    /// but never logged, every one stamped "now": the My Ratings page went
    /// from newest-rated to alphabetical, and the activity feed became a wall
    /// of "rated today". The order was there all along, on the page.
    ///
    /// So: every film with a real date keeps it; every film without one is
    /// slotted between its nearest dated neighbours, evenly, so the result
    /// sorts in the page's own order as far as the known dates allow. Above
    /// the newest dated film the ceiling is "now"; below the oldest, one
    /// second per position. With no dates at all, the order alone survives.
    /// Dated neighbours can be out of order (a rewatch logged after newer
    /// ratings), so the bounds used for slotting are the running minimum,
    /// which never goes up as the list goes down.
    /// </summary>
    internal static class ListOrderDates
    {
        /// <param name="knownNewestFirst">One entry per list position, newest first; null where no date is known.</param>
        /// <param name="now">The ceiling for undated films above the first dated one.</param>
        /// <returns>A date per position; known ones unchanged.</returns>
        public static DateTime[] Fill(IReadOnlyList<DateTime?> knownNewestFirst, DateTime now)
        {
            var n = knownNewestFirst.Count;
            var result = new DateTime[n];
            if (n == 0) return result;

            // Bounds: for each dated position, the smallest date at or above it.
            var bound = new DateTime?[n];
            DateTime? runMin = null;
            for (var i = 0; i < n; i++)
            {
                if (knownNewestFirst[i] is DateTime d)
                {
                    if (runMin == null || d < runMin.Value) runMin = d;
                    bound[i] = runMin;
                }
            }

            var i0 = 0;
            while (i0 < n)
            {
                if (knownNewestFirst[i0] is DateTime known)
                {
                    result[i0] = known;
                    i0++;
                    continue;
                }

                // The run of undated positions [i0, i1).
                var i1 = i0;
                while (i1 < n && knownNewestFirst[i1] == null) i1++;
                var newer = i0 > 0 ? bound[i0 - 1] : null;       // dated film just above the run
                var older = i1 < n ? bound[i1] : null;            // dated film just below the run
                var count = i1 - i0;

                if (older != null)
                {
                    var ceiling = newer ?? now;
                    if (ceiling <= older.Value) ceiling = older.Value.AddSeconds(count + 1);
                    var step = (ceiling - older.Value).Ticks / (count + 1);
                    for (var k = 0; k < count; k++)
                        result[i0 + k] = older.Value.AddTicks(step * (count - k));     // top of the run is newest
                }
                else if (newer != null)
                {
                    for (var k = 0; k < count; k++)
                        result[i0 + k] = newer.Value.AddSeconds(-(k + 1));
                }
                else
                {
                    for (var k = 0; k < count; k++)
                        result[i0 + k] = now.AddSeconds(-k);
                }
                i0 = i1;
            }
            return result;
        }
    }
}
