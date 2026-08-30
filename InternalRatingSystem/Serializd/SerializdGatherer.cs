using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Data;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Serializd
{
    /// <summary>
    /// One TV rating in the shape Serializd needs.
    ///
    /// Season and episode numbers are the reason this exists rather than reusing
    /// <c>ExternalRating</c>: that type carries neither, so an episode rating
    /// could not be placed at all and a "push episodes" toggle built on it would
    /// have been a switch that did nothing. Review text is here for the same
    /// reason.
    /// </summary>
    public sealed record SerializdRating(
        int TmdbId,
        int? SeasonNumber,
        int? EpisodeNumber,
        double Stars,
        string? Review)
    {
        /// <summary>A single episode.</summary>
        public bool IsEpisode => SeasonNumber.HasValue && EpisodeNumber.HasValue;

        /// <summary>A whole season — Serializd's native unit.</summary>
        public bool IsSeason  => SeasonNumber.HasValue && !EpisodeNumber.HasValue;

        /// <summary>A whole series.</summary>
        public bool IsSeries  => !SeasonNumber.HasValue;
    }

    /// <summary>Read side, so the push can be tested without a Jellyfin library.</summary>
    public interface ISerializdGatherer
    {
        /// <summary>Every TV rating a user has, resolved to Serializd's identifiers.</summary>
        Task<IReadOnlyList<SerializdRating>> GatherAsync(string userId);
    }

    /// <summary>
    /// Turns StarTrack ratings into Serializd-shaped TV ratings.
    ///
    /// Movies are dropped here rather than downstream: Serializd is television
    /// only, and a film reaching the writer would be filed against whatever show
    /// happened to share its TMDb id.
    ///
    /// Episodes are resolved to the SERIES TMDb id plus season/episode numbers,
    /// because Serializd addresses an episode as
    /// (show_id, season_id, episode_number) and derives season_id from the show.
    /// </summary>
    public sealed class SerializdGatherer : ISerializdGatherer
    {
        private readonly IRatingReader _reader;
        private readonly ILibraryManager _library;
        private readonly ILogger<SerializdGatherer> _logger;

        public SerializdGatherer(IRatingReader reader, ILibraryManager library, ILogger<SerializdGatherer> logger)
        {
            _reader  = reader;
            _library = library;
            _logger  = logger;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<SerializdRating>> GatherAsync(string userId)
        {
            var result = new List<SerializdRating>();
            var rows = await _reader.GetUserRatingsAsync(userId).ConfigureAwait(false);

            foreach (var row in rows)
            {
                if (!Guid.TryParse(row.ItemId, out var guid)) continue;

                try
                {
                    var item = _library.GetItemById(guid);
                    if (item == null) continue;

                    switch (item)
                    {
                        case Series series:
                        {
                            if (TmdbOf(series) is int showId)
                                result.Add(new SerializdRating(showId, null, null, row.Stars, Clean(row.Review)));
                            break;
                        }

                        case MediaBrowser.Controller.Entities.TV.Season season:
                        {
                            // Serializd addresses a season as (show, season
                            // number), so the parent series carries the id.
                            var parentSeries = season.Series;
                            if (parentSeries == null) break;
                            if (TmdbOf(parentSeries) is not int seasonShowId) break;

                            var seasonNo = season.IndexNumber;
                            if (seasonNo is null or <= 0) break;   // specials, as below

                            result.Add(new SerializdRating(seasonShowId, seasonNo, null, row.Stars, Clean(row.Review)));
                            break;
                        }

                        case Episode ep:
                        {
                            // Serializd wants the SERIES id, not the episode's own.
                            var parent = ep.Series;
                            if (parent == null) break;
                            if (TmdbOf(parent) is not int showId) break;

                            // Jellyfin exposes season as ParentIndexNumber and the
                            // episode as IndexNumber. Specials (season 0) are left
                            // out: Serializd numbers them differently and a wrong
                            // season is worse than a missing one.
                            var season = ep.ParentIndexNumber;
                            var number = ep.IndexNumber;
                            if (season is null or <= 0 || number is null or <= 0) break;

                            result.Add(new SerializdRating(showId, season, number, row.Stars, Clean(row.Review)));
                            break;
                        }

                        // Movies and everything else are Letterboxd's business.
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[StarTrack] Serializd: could not resolve item {Item}", row.ItemId);
                }
            }

            return result;
        }

        private static int? TmdbOf(MediaBrowser.Controller.Entities.BaseItem item)
        {
            if (item.ProviderIds != null &&
                item.ProviderIds.TryGetValue("Tmdb", out var raw) &&
                int.TryParse(raw, out var id))
                return id;
            return null;
        }

        private static string? Clean(string? review) =>
            string.IsNullOrWhiteSpace(review) ? null : review.Trim();
    }
}
