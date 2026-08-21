using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Data;
using Jellyfin.Plugin.InternalRating.ExternalSync;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Letterboxd
{
    /// <summary>
    /// What a push run did.
    ///
    /// EVERY property needs an explicit JsonPropertyName. Jellyfin 10.11's host
    /// serializer defaults to PascalCase, so without these the API returns
    /// "Rated"/"Error" while the widget reads r.rated/r.error — every field
    /// comes back undefined, the error never displays, and a failed push reports
    /// "Nothing new to push". This is the same trap that made the v1.1.4
    /// diagnose button print "undefined" (see the note atop LetterboxdSettings.cs);
    /// it was caught here only by calling the endpoint over real HTTP, because
    /// unit tests construct the object directly and never serialise it.
    /// </summary>
    public sealed class LetterboxdPushResult
    {
        /// <summary>Ratings written or updated.</summary>
        [JsonPropertyName("rated")]                public int Rated { get; set; }

        /// <summary>Films marked watched.</summary>
        [JsonPropertyName("watched")]              public int Watched { get; set; }

        /// <summary>Films liked.</summary>
        [JsonPropertyName("liked")]                public int Liked { get; set; }

        /// <summary>Dated diary entries created.</summary>
        [JsonPropertyName("diaryEntries")]         public int DiaryEntries { get; set; }

        /// <summary>Items skipped because the ledger says they're already logged.</summary>
        [JsonPropertyName("skippedAlreadyLogged")] public int SkippedAlreadyLogged { get; set; }

        /// <summary>Films skipped entirely because nothing about them changed since the last push.</summary>
        [JsonPropertyName("unchanged")]            public int Unchanged { get; set; }

        /// <summary>Films added to the Letterboxd watchlist.</summary>
        [JsonPropertyName("watchlisted")]          public int Watchlisted { get; set; }

        /// <summary>
        /// Films still needing work when this run hit its cap. Non-zero simply
        /// means the next run will continue; it is not an error.
        /// </summary>
        [JsonPropertyName("remaining")]            public int Remaining { get; set; }

        /// <summary>Items Letterboxd doesn't have, or with no TMDb id to match on.</summary>
        [JsonPropertyName("unmatched")]            public int Unmatched { get; set; }

        /// <summary>Fatal error, if the run stopped early.</summary>
        [JsonPropertyName("error")]                public string? Error { get; set; }

        /// <summary>Total successful writes.</summary>
        [JsonPropertyName("totalWritten")]         public int TotalWritten => Rated + Watched + Liked + DiaryEntries + Watchlisted;
    }

    /// <summary>
    /// Pushes a user's StarTrack activity to Letterboxd.
    ///
    /// Ordering and idempotency are the whole design here:
    ///
    ///  * Ratings and watched-flags go through Letterboxd's idempotent
    ///    /rate/ and /watch/ endpoints. Re-sending an unchanged value is a
    ///    no-op, so this path is safe to run on every tick with no local state,
    ///    and a changed rating correctly updates in place instead of stacking.
    ///
    ///  * Dated diary entries are append-only on Letterboxd's side, so they are
    ///    opt-in and guarded by <see cref="LetterboxdPushLedger"/>.
    ///
    /// The run is best-effort per item: one film failing must not abandon the
    /// rest. A failure that means the whole session is dead (expired login,
    /// Cloudflare) stops the run immediately rather than burning through the
    /// user's whole library generating identical errors.
    /// </summary>
    public sealed class LetterboxdPushService
    {
        private readonly IRatingGatherer _ratings;
        private readonly ILikedGatherer? _liked;
        private readonly LetterboxdPushLedger _ledger;
        private readonly ILogger<LetterboxdPushService> _logger;
        private readonly IWatchDiaryReader? _diary;
        private readonly IWatchlistReader? _watchlist;
        private readonly IExternalIdResolver? _resolver;

        public LetterboxdPushService(
            IRatingGatherer ratings,
            LetterboxdPushLedger ledger,
            ILogger<LetterboxdPushService> logger,
            ILikedGatherer? liked = null,
            IWatchDiaryReader? diary = null,
            IWatchlistReader? watchlist = null,
            IExternalIdResolver? resolver = null)
        {
            _ratings   = ratings;
            _ledger    = ledger;
            _logger    = logger;
            _liked     = liked;
            _diary     = diary;
            _watchlist = watchlist;
            _resolver  = resolver;
        }

        /// <summary>
        /// Runs one push cycle for a user against an authenticated writer.
        /// Never throws; failures land in <see cref="LetterboxdPushResult.Error"/>.
        /// </summary>
        /// <param name="userId">StarTrack user id ("N"-format GUID).</param>
        /// <param name="writer">Authenticated Letterboxd writer.</param>
        /// <param name="settings">The user's Letterboxd settings (direction + push toggles).</param>
        /// <param name="writeDiaryEntries">
        /// When true, also create dated diary entries. Off by default because
        /// diary writes cannot be undone by a later sync.
        /// </param>
        /// <param name="ct">Cancellation.</param>
        /// <param name="maxFilms">
        /// Most films to touch in one run. The FIRST run for an established
        /// library has thousands of films to seed; firing them as fast as the
        /// loop allows is indistinguishable from an attack to Cloudflare. Work
        /// is spread across runs instead, and the CSV export exists for anyone
        /// who wants everything at once.
        /// </param>
        /// <param name="delayMs">Pause between films that actually touch the network.</param>
        public async Task<LetterboxdPushResult> PushAsync(
            string userId,
            ILetterboxdWriter writer,
            LetterboxdUserSettings settings,
            bool writeDiaryEntries,
            CancellationToken ct = default,
            int maxFilms = 200,
            int delayMs = 250)
        {
            var result = new LetterboxdPushResult();

            if (settings.Direction is not (LetterboxdDirection.ExportOnly or LetterboxdDirection.TwoWay))
                return result;

            try
            {
                var rated = await _ratings.GatherAsync(userId).ConfigureAwait(false);

                // Liked films are a separate list from ratings — a user can like
                // something they never rated, so this is a union, not a filter.
                var likedKeys = new HashSet<int>();
                if (settings.PushLiked && _liked != null)
                {
                    foreach (var l in await _liked.GatherLikedAsync(userId).ConfigureAwait(false))
                        if (l.Tmdb is int t) likedKeys.Add(t);
                }

                // Only movies. Letterboxd is a film service: pushing a TV series
                // there would either 404 or, worse, match some unrelated film of
                // the same name.
                var work = rated.Where(r => r.MediaType == "movie").ToList();
                foreach (var t in likedKeys)
                    if (!work.Any(w => w.Tmdb == t))
                        work.Add(new ExternalRating(null, t, null, string.Empty, null, "movie", 0, DateTime.UtcNow));

                var touched = 0;

                foreach (var item in work)
                {
                    ct.ThrowIfCancellationRequested();

                    // Cap reached: count what is left and stop. The next run picks
                    // up exactly where this one stopped, because the push-state
                    // cache makes everything already done free to skip.
                    if (touched >= maxFilms)
                    {
                        result.Remaining++;
                        continue;
                    }

                    if (item.Tmdb is not int tmdbId)
                    {
                        // No TMDb id means no reliable way to identify the film
                        // on Letterboxd. Title matching across catalogues is how
                        // you end up rating the wrong movie.
                        result.Unmatched++;
                        continue;
                    }

                    var isLiked = likedKeys.Contains(tmdbId);
                    var hasRating = item.Stars >= 0.5;

                    // Skip untouched films BEFORE spending a single request.
                    //
                    // The writes are idempotent, so re-sending is harmless for
                    // correctness — but a 1300-film library would re-resolve and
                    // re-write everything every hour, thousands of requests at a
                    // site behind Cloudflare. Steady state has to be free.
                    var signature = LetterboxdPushLedger.Signature(
                        item.Stars, isLiked, settings.PushRatings, settings.PushWatched, settings.PushLiked);

                    if (await _ledger.IsUnchangedAsync(userId, tmdbId, signature).ConfigureAwait(false))
                    {
                        result.Unchanged++;
                        continue;
                    }

                    // Resolution is cached globally: which Letterboxd film a TMDb
                    // id maps to is the same for every user, and resolving costs a
                    // redirect plus a full HTML page.
                    var film = await ResolveCachedAsync(writer, tmdbId, ct).ConfigureAwait(false);
                    if (film == null) { result.Unmatched++; continue; }

                    // ---- rating (idempotent) ----
                    if (settings.PushRatings && hasRating)
                    {
                        var r = await writer.SetRatingAsync(film, item.Stars, ct).ConfigureAwait(false);
                        if (IsFatal(r, result)) return result;
                        if (r.Ok) result.Rated++;
                    }

                    // ---- watched (idempotent) ----
                    if (settings.PushWatched && hasRating)
                    {
                        var w = await writer.SetWatchedAsync(film, ct).ConfigureAwait(false);
                        if (IsFatal(w, result)) return result;
                        if (w.Ok) result.Watched++;
                    }

                    // ---- liked (idempotent, additive only) ----
                    // Previously a like only rode along as a field on a diary
                    // entry, so with diary logging off (the default) likes were
                    // never sent at all — while the counter still reported them.
                    if (settings.PushLiked && isLiked)
                    {
                        var lk = await writer.SetLikedAsync(film, ct).ConfigureAwait(false);
                        if (IsFatal(lk, result)) return result;
                        // Counted only on a confirmed write. A 404 here means this
                        // Letterboxd build has no standalone like endpoint, and
                        // reporting a success we cannot prove is worse than a zero.
                        if (lk.Ok) result.Liked++;
                    }

                    // Remember what this film now looks like remotely, so the next
                    // run can skip it without a request.
                    await _ledger.SetStateAsync(userId, tmdbId, signature).ConfigureAwait(false);

                    // Pace only films that actually hit the network; skipped ones
                    // cost nothing and must not slow the sweep down.
                    touched++;
                    if (delayMs > 0) await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }

                // Diary and watchlist are separate passes over different local
                // data, so they run after the rating sweep rather than being
                // squeezed into it.
                if (writeDiaryEntries)
                    if (await PushDiaryAsync(userId, writer, settings, result, delayMs, ct).ConfigureAwait(false)) return result;

                if (settings.PushWatchlist)
                    if (await PushWatchlistAsync(userId, writer, result, delayMs, ct).ConfigureAwait(false)) return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StarTrack] Letterboxd push failed for user {UserId}", userId);
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Pushes real diary entries.
        ///
        /// This used to derive a diary date from when a rating was CREATED,
        /// which is not when the film was watched — an imported back catalogue
        /// would have logged every film on the day it was imported. Now that
        /// PlaybackDiaryService records genuine watches, the diary is the
        /// source of truth: real date, real rewatch flag, and the review that
        /// was actually written for that viewing.
        /// </summary>
        /// <returns>True when the whole run should abort.</returns>
        private async Task<bool> PushDiaryAsync(
            string userId, ILetterboxdWriter writer, LetterboxdUserSettings settings,
            LetterboxdPushResult result, int delayMs, CancellationToken ct)
        {
            if (_diary == null || _resolver == null) return false;
            if (settings.DiaryLoggingSince is not DateTime since) return false;

            var entries = await _diary.GetEntriesAsync(userId).ConfigureAwait(false);

            // Oldest first, so a partial run leaves the diary in chronological
            // order rather than scattering entries as the cap cuts in.
            foreach (var entry in entries.Where(e => e.WatchedAt >= since).OrderBy(e => e.WatchedAt))
            {
                ct.ThrowIfCancellationRequested();

                var mapped = _resolver.ResolveExternalIds(entry.ItemId, entry.Stars ?? 0, entry.WatchedAt);
                if (mapped == null || mapped.MediaType != "movie" || mapped.Tmdb is not int tmdbId) continue;

                var day = entry.WatchedAt.ToLocalTime().Date;
                if (await _ledger.HasAsync(userId, tmdbId, day).ConfigureAwait(false))
                {
                    result.SkippedAlreadyLogged++;
                    continue;
                }

                var film = await ResolveCachedAsync(writer, tmdbId, ct).ConfigureAwait(false);
                if (film == null) { result.Unmatched++; continue; }

                var d = await writer.LogEntryAsync(
                    film, day, entry.Stars, liked: false, rewatch: entry.Rewatch,
                    review: settings.PushReviews ? entry.Review : null,
                    containsSpoilers: false, ct: ct).ConfigureAwait(false);

                if (IsFatal(d, result)) return true;
                if (d.Ok)
                {
                    // Recorded only after Letterboxd confirms, so a failure is
                    // retried next run instead of being silently marked done.
                    await _ledger.AddAsync(userId, tmdbId, day).ConfigureAwait(false);
                    result.DiaryEntries++;
                    if (delayMs > 0) await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
            }

            return false;
        }

        /// <summary>
        /// Mirrors the StarTrack watchlist into the Letterboxd watchlist.
        /// Additive only — see AddToWatchlistAsync for why nothing is removed.
        /// </summary>
        /// <returns>True when the whole run should abort.</returns>
        private async Task<bool> PushWatchlistAsync(
            string userId, ILetterboxdWriter writer,
            LetterboxdPushResult result, int delayMs, CancellationToken ct)
        {
            if (_watchlist == null || _resolver == null) return false;

            var items = await _watchlist.GetWatchlistAsync(userId).ConfigureAwait(false);

            foreach (var w in items)
            {
                ct.ThrowIfCancellationRequested();

                var mapped = _resolver.ResolveExternalIds(w.ItemId, 0, w.AddedAt);
                if (mapped == null || mapped.MediaType != "movie" || mapped.Tmdb is not int tmdbId) continue;

                // Reuse the diary ledger keyed on a fixed sentinel date, so a
                // film is only ever offered to the watchlist once. Adding is
                // idempotent on Letterboxd's side, but there is no reason to
                // spend a request per film on every run.
                var key = new DateTime(1900, 1, 1);
                if (await _ledger.HasAsync(userId + ":wl", tmdbId, key).ConfigureAwait(false)) continue;

                var film = await ResolveCachedAsync(writer, tmdbId, ct).ConfigureAwait(false);
                if (film == null) { result.Unmatched++; continue; }

                var r = await writer.AddToWatchlistAsync(film, ct).ConfigureAwait(false);
                if (IsFatal(r, result)) return true;
                if (r.Ok)
                {
                    await _ledger.AddAsync(userId + ":wl", tmdbId, key).ConfigureAwait(false);
                    result.Watchlisted++;
                    if (delayMs > 0) await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
            }

            return false;
        }

        /// <summary>Film lookup with the global resolution cache in front of it.</summary>
        private async Task<LetterboxdFilm?> ResolveCachedAsync(ILetterboxdWriter writer, int tmdbId, CancellationToken ct)
        {
            var cached = await _ledger.GetFilmAsync(tmdbId).ConfigureAwait(false);
            if (cached is { } c) return new LetterboxdFilm(c.Slug, c.FilmId, c.ProductionId);

            var film = await writer.ResolveFilmAsync(tmdbId, ct).ConfigureAwait(false);
            if (film != null)
                await _ledger.SetFilmAsync(tmdbId, film.Slug, film.FilmId, film.ProductionId).ConfigureAwait(false);
            return film;
        }

        /// <summary>
        /// Diary entries are only written for watches at or after the moment the
        /// user switched diary logging on.
        ///
        /// This is what stops StarTrack dumping a user's entire back catalogue
        /// into a Letterboxd diary that already contains years of their own
        /// entries. We have no way to recognise an existing entry as "the same
        /// watch", so anything older is left alone rather than duplicated. A
        /// missing cutoff means logging was never enabled, so nothing qualifies.
        /// </summary>
        private static bool IsAfterDiaryCutoff(ExternalRating item, LetterboxdUserSettings settings)
            => settings.DiaryLoggingSince is DateTime since && item.RatedAt >= since;

        /// <summary>
        /// Session-level failures abort the whole run. Continuing would issue one
        /// doomed request per film in the library and, on Cloudflare, look
        /// exactly like the abuse that got us blocked in the first place.
        /// </summary>
        private static bool IsFatal(LetterboxdWriteResult r, LetterboxdPushResult result)
        {
            switch (r.Status)
            {
                case LetterboxdWriteStatus.NeedsReauth:
                case LetterboxdWriteStatus.Cloudflare:
                    result.Error = r.Message ?? r.Status.ToString();
                    return true;
                default:
                    return false;
            }
        }
    }
}
