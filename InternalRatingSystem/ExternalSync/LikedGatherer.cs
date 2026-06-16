using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Data;

namespace Jellyfin.Plugin.InternalRating.ExternalSync
{
    /// <summary>
    /// Seam that lets <c>SyncOrchestrator</c> gather a user's "liked" (♡) items
    /// as <see cref="ExternalRating"/> records (resolved to external ids) for
    /// pushing to providers that support liked/favorites sync.
    /// </summary>
    public interface ILikedGatherer
    {
        /// <summary>
        /// Returns the user's liked items as <see cref="ExternalRating"/> records.
        /// Stars are not meaningful here (set to 0) — only the ids/title matter.
        /// Items whose Jellyfin library entry can't be resolved are skipped.
        /// </summary>
        Task<IReadOnlyList<ExternalRating>> GatherLikedAsync(string userId);
    }

    /// <summary>
    /// Resolves a user's StarTrack "liked" items (from
    /// <see cref="UserInteractionsRepository"/>) into <see cref="ExternalRating"/>
    /// records via <see cref="IExternalIdResolver"/>.
    /// </summary>
    public sealed class LikedGatherer : ILikedGatherer
    {
        private readonly UserInteractionsRepository _interactions;
        private readonly IExternalIdResolver _resolver;

        public LikedGatherer(UserInteractionsRepository interactions, IExternalIdResolver resolver)
        {
            _interactions = interactions;
            _resolver     = resolver;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<ExternalRating>> GatherLikedAsync(string userId)
        {
            var liked  = await _interactions.GetLikedAsync(userId).ConfigureAwait(false);
            var result = new List<ExternalRating>();

            foreach (var e in liked)
            {
                // Stars are irrelevant for liked/favorites — use 0; LikedAt as the timestamp.
                var er = _resolver.ResolveExternalIds(e.ItemId, 0, e.LikedAt);
                if (er != null)
                    result.Add(er);
            }

            return result;
        }
    }
}
