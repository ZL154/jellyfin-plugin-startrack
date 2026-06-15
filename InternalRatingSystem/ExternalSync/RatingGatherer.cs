using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Data;

namespace Jellyfin.Plugin.InternalRating.ExternalSync
{
    /// <summary>
    /// Gathers all of a user's StarTrack ratings and maps them to
    /// <see cref="ExternalRating"/> records suitable for export or sync.
    /// Items whose Jellyfin library entry cannot be resolved are silently skipped.
    /// </summary>
    public sealed class RatingGatherer
    {
        private readonly IRatingReader _reader;
        private readonly IExternalIdResolver _resolver;

        public RatingGatherer(IRatingReader reader, IExternalIdResolver resolver)
        {
            _reader   = reader;
            _resolver = resolver;
        }

        /// <summary>
        /// Returns all <see cref="ExternalRating"/> records for <paramref name="userId"/>.
        /// Rows whose item GUID cannot be resolved in the Jellyfin library are skipped.
        /// </summary>
        public async Task<IReadOnlyList<ExternalRating>> GatherAsync(string userId)
        {
            var rows   = await _reader.GetUserRatingsAsync(userId).ConfigureAwait(false);
            var result = new List<ExternalRating>();

            foreach (var row in rows)
            {
                var er = _resolver.ResolveExternalIds(row.ItemId, row.Stars, row.RatedAt);
                if (er != null)
                    result.Add(er);
            }

            return result;
        }
    }
}
