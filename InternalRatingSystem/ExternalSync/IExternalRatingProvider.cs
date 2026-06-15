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
}
