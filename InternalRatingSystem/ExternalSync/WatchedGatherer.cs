using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.InternalRating.ExternalSync
{
    /// <summary>
    /// Gathers a user's ACTUAL Jellyfin watch state (every fully-played movie
    /// and episode) — independent of StarTrack ratings — for a one-shot backfill
    /// of watched history to providers that support it (Trakt).
    /// </summary>
    public interface IWatchedGatherer
    {
        /// <summary>
        /// Returns the user's fully-played movies + episodes as
        /// <see cref="ExternalRating"/> records (stars unused; RatedAt carries the
        /// Jellyfin LastPlayedDate so Trakt records the right watched_at). Items
        /// that can't be resolved to external ids are skipped.
        /// </summary>
        Task<IReadOnlyList<ExternalRating>> GatherWatchedAsync(Guid userId);
    }

    /// <summary>
    /// Reads Jellyfin's per-user played state via <see cref="ILibraryManager"/> +
    /// <see cref="IUserDataManager"/> and maps it to <see cref="ExternalRating"/>
    /// records (reusing <see cref="IExternalIdResolver"/> for id resolution).
    /// </summary>
    public sealed class WatchedGatherer : IWatchedGatherer
    {
        private readonly ILibraryManager _library;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userData;
        private readonly IExternalIdResolver _resolver;

        public WatchedGatherer(
            ILibraryManager library,
            IUserManager userManager,
            IUserDataManager userData,
            IExternalIdResolver resolver)
        {
            _library     = library;
            _userManager = userManager;
            _userData    = userData;
            _resolver    = resolver;
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<ExternalRating>> GatherWatchedAsync(Guid userId)
        {
            var result = new List<ExternalRating>();

            var user = _userManager.GetUserById(userId);
            if (user == null)
                return Task.FromResult<IReadOnlyList<ExternalRating>>(result);

            // Jellyfin's own "played" flag — the real watch history, not ratings.
            var query = new InternalItemsQuery(user)
            {
                IsPlayed         = true,
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                Recursive        = true
            };

            IReadOnlyList<BaseItem> items;
            try { items = _library.GetItemList(query); }
            catch { items = Array.Empty<BaseItem>(); }

            foreach (var item in items)
            {
                if (item == null) continue;

                DateTime watchedAt;
                try
                {
                    var ud = _userData.GetUserData(user, item);
                    watchedAt = ud?.LastPlayedDate?.ToUniversalTime() ?? DateTime.UtcNow;
                }
                catch { watchedAt = DateTime.UtcNow; }

                // Reuse the resolver (handles Movie/Episode media-type + provider ids).
                var er = _resolver.ResolveExternalIds(item.Id.ToString("N"), 0, watchedAt);
                if (er != null)
                    result.Add(er);
            }

            return Task.FromResult<IReadOnlyList<ExternalRating>>(result);
        }
    }
}
