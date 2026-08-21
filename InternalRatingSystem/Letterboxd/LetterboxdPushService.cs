using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.ExternalSync;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Letterboxd
{
    /// <summary>What a push run did.</summary>
    public sealed class LetterboxdPushResult
    {
        /// <summary>Ratings written or updated.</summary>
        public int Rated { get; set; }
        /// <summary>Films marked watched.</summary>
        public int Watched { get; set; }
        /// <summary>Films liked.</summary>
        public int Liked { get; set; }
        /// <summary>Dated diary entries created.</summary>
        public int DiaryEntries { get; set; }
        /// <summary>Items skipped because the ledger says they're already logged.</summary>
        public int SkippedAlreadyLogged { get; set; }

        /// <summary>Films skipped entirely because nothing about them changed since the last push.</summary>
        public int Unchanged { get; set; }
        /// <summary>Items Letterboxd doesn't have, or with no TMDb id to match on.</summary>
        public int Unmatched { get; set; }
        /// <summary>Fatal error, if the run stopped early.</summary>
        public string? Error { get; set; }

        /// <summary>Total successful writes.</summary>
        public int TotalWritten => Rated + Watched + Liked + DiaryEntries;
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

        public LetterboxdPushService(
            IRatingGatherer ratings,
            LetterboxdPushLedger ledger,
            ILogger<LetterboxdPushService> logger,
            ILikedGatherer? liked = null)
        {
            _ratings = ratings;
            _ledger  = ledger;
            _logger  = logger;
            _liked   = liked;
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
        public async Task<LetterboxdPushResult> PushAsync(
            string userId,
            ILetterboxdWriter writer,
            LetterboxdUserSettings settings,
            bool writeDiaryEntries,
            CancellationToken ct = default)
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

                foreach (var item in work)
                {
                    ct.ThrowIfCancellationRequested();

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

                    var diaryPending = writeDiaryEntries && hasRating && IsAfterDiaryCutoff(item, settings)
                                       && !await _ledger.HasAsync(userId, tmdbId, item.RatedAt.ToLocalTime().Date).ConfigureAwait(false);

                    if (!diaryPending && await _ledger.IsUnchangedAsync(userId, tmdbId, signature).ConfigureAwait(false))
                    {
                        result.Unchanged++;
                        continue;
                    }

                    // Resolution is cached globally: which Letterboxd film a TMDb
                    // id maps to is the same for every user, and resolving costs a
                    // redirect plus a full HTML page.
                    LetterboxdFilm? film;
                    var cached = await _ledger.GetFilmAsync(tmdbId).ConfigureAwait(false);
                    if (cached is { } c)
                    {
                        film = new LetterboxdFilm(c.Slug, c.FilmId, c.ProductionId);
                    }
                    else
                    {
                        film = await writer.ResolveFilmAsync(tmdbId, ct).ConfigureAwait(false);
                        if (film != null)
                            await _ledger.SetFilmAsync(tmdbId, film.Slug, film.FilmId, film.ProductionId).ConfigureAwait(false);
                    }
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

                    // ---- diary entry (append-only — ledger-guarded) ----
                    if (writeDiaryEntries && hasRating && IsAfterDiaryCutoff(item, settings))
                    {
                        var day = item.RatedAt.ToLocalTime().Date;
                        if (await _ledger.HasAsync(userId, tmdbId, day).ConfigureAwait(false))
                        {
                            result.SkippedAlreadyLogged++;
                        }
                        else
                        {
                            var d = await writer.LogEntryAsync(
                                film, day, item.Stars, isLiked, rewatch: false,
                                review: null, containsSpoilers: false, ct: ct).ConfigureAwait(false);
                            if (IsFatal(d, result)) return result;
                            if (d.Ok)
                            {
                                // Recorded ONLY after Letterboxd confirms, so a
                                // failed write is retried next run rather than
                                // being silently marked done.
                                await _ledger.AddAsync(userId, tmdbId, day).ConfigureAwait(false);
                                result.DiaryEntries++;
                            }
                        }
                    }

                    // Remember what this film now looks like remotely, so the next
                    // run can skip it without a request.
                    await _ledger.SetStateAsync(userId, tmdbId, signature).ConfigureAwait(false);
                }
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
