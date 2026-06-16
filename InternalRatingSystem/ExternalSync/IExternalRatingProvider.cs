using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.InternalRating.ExternalSync
{
    public enum ProviderId { Trakt, Simkl, Yamtrack }

    public interface IExternalRatingProvider
    {
        ProviderId Id { get; }
        Task<IReadOnlyList<ExternalRating>> PullRatingsAsync(ProviderConnection conn, CancellationToken ct);
        Task<int> PushRatingsAsync(ProviderConnection conn, IReadOnlyList<ExternalRating> ratings, CancellationToken ct);
        Task<bool> EnsureTokenAsync(ProviderConnection conn, CancellationToken ct);
    }

    /// <summary>
    /// Optional capability implemented by providers that can also reflect a
    /// user's library beyond star ratings — marking items watched and pushing
    /// "liked" items. Only Trakt implements this; the sync flow checks
    /// <c>provider is ISupportsLibrarySync</c> so Simkl/Yamtrack are unaffected.
    /// Implementations MUST be idempotent (pull existing state and only add
    /// what's missing) so repeated syncs don't create duplicate plays/items.
    /// </summary>
    public interface ISupportsLibrarySync
    {
        /// <summary>Marks the given items as watched on the remote service. Returns the number newly added.</summary>
        Task<int> MarkWatchedAsync(ProviderConnection conn, IReadOnlyList<ExternalRating> watched, CancellationToken ct);

        /// <summary>Pushes the given "liked" items to the remote service (e.g. Favorites + a dedicated list). Returns the number newly added.</summary>
        Task<int> PushLikedAsync(ProviderConnection conn, IReadOnlyList<ExternalRating> liked, CancellationToken ct);
    }
}
