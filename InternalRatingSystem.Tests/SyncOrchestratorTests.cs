using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Data;
using Jellyfin.Plugin.InternalRating.ExternalSync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.InternalRating.Tests
{
    // =========================================================================
    // Fakes
    // =========================================================================

    /// <summary>
    /// In-memory IRatingSink. Stores ratings as (userId, itemId) -> stars.
    /// </summary>
    internal sealed class FakeSink : IRatingSink
    {
        // (userId, itemId) -> stars
        private readonly Dictionary<(string, string), double> _data = new();

        // Records of SaveRatingAsync calls for assertion.
        public List<(string itemId, string userId, string userName, double stars, string? review, DateTime? ratedAt)> Saves { get; } = new();

        public Task SaveRatingAsync(string itemId, string userId, string userName, double stars, string? review, DateTime? ratedAt)
        {
            _data[(userId, itemId)] = stars;
            Saves.Add((itemId, userId, userName, stars, review, ratedAt));
            return Task.CompletedTask;
        }

        public Task<double?> GetUserStarsAsync(string userId, string itemId)
        {
            double? val = _data.TryGetValue((userId, itemId), out var s) ? s : null;
            return Task.FromResult(val);
        }

        /// <summary>Seed a pre-existing rating so GetUserStarsAsync returns it.</summary>
        public void Seed(string userId, string itemId, double stars) => _data[(userId, itemId)] = stars;
    }

    /// <summary>
    /// IRatingGatherer that returns a fixed list of ExternalRatings.
    /// </summary>
    internal sealed class FakeGatherer : IRatingGatherer
    {
        private readonly IReadOnlyList<ExternalRating> _ratings;
        public FakeGatherer(IReadOnlyList<ExternalRating> ratings) => _ratings = ratings;
        public Task<IReadOnlyList<ExternalRating>> GatherAsync(string userId)
            => Task.FromResult(_ratings);
    }

    /// <summary>
    /// IExternalIdResolver that maps a specific ExternalRating (by Imdb) to a fixed itemId.
    /// All others return null.
    /// </summary>
    internal sealed class FakeResolver : IExternalIdResolver
    {
        // imdb -> itemId
        private readonly Dictionary<string, string> _map;
        public FakeResolver(Dictionary<string, string> map) => _map = map;

        public ExternalRating? ResolveExternalIds(string itemId, double stars, DateTime ratedAt) => null;

        public string? FindItemId(ExternalRating r)
        {
            if (r.Imdb != null && _map.TryGetValue(r.Imdb, out var id))
                return id;
            return null;
        }
    }

    /// <summary>
    /// IExternalRatingProvider fake. Configurable pull list, records push calls.
    /// </summary>
    internal sealed class FakeProvider : IExternalRatingProvider
    {
        public ProviderId Id => ProviderId.Trakt;

        private readonly IReadOnlyList<ExternalRating> _pullResult;
        public bool EnsureTokenCalled { get; private set; }
        public List<IReadOnlyList<ExternalRating>> PushCalls { get; } = new();
        public Func<ProviderConnection, CancellationToken, Task>? OnPull { get; set; }

        public FakeProvider(IReadOnlyList<ExternalRating>? pullResult = null)
            => _pullResult = pullResult ?? Array.Empty<ExternalRating>();

        public Task<bool> EnsureTokenAsync(ProviderConnection conn, CancellationToken ct)
        {
            EnsureTokenCalled = true;
            return Task.FromResult(false);
        }

        public async Task<IReadOnlyList<ExternalRating>> PullRatingsAsync(ProviderConnection conn, CancellationToken ct)
        {
            if (OnPull != null)
                await OnPull(conn, ct).ConfigureAwait(false);
            return _pullResult;
        }

        public Task<int> PushRatingsAsync(ProviderConnection conn, IReadOnlyList<ExternalRating> ratings, CancellationToken ct)
        {
            PushCalls.Add(ratings);
            return Task.FromResult(ratings.Count);
        }
    }

    // =========================================================================
    // Tests
    // =========================================================================

    public class SyncOrchestratorTests
    {
        private static readonly DateTime T0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static SyncOrchestrator BuildOrchestrator(
            IRatingGatherer? gatherer = null,
            IRatingSink? sink = null,
            IExternalIdResolver? resolver = null)
        {
            return new SyncOrchestrator(
                gatherer  ?? new FakeGatherer(Array.Empty<ExternalRating>()),
                sink      ?? new FakeSink(),
                resolver  ?? new FakeResolver(new Dictionary<string, string>()),
                NullLogger<SyncOrchestrator>.Instance);
        }

        // ------------------------------------------------------------------
        // Test 1: Direction = Off -> no calls, empty result
        // ------------------------------------------------------------------

        [Fact]
        public async Task Direction_Off_ReturnsEmptyResult_NoProviderCalls()
        {
            var provider = new FakeProvider(new[] { new ExternalRating("tt1", null, null, "Film", 2020, "movie", 4.0, T0) });
            var sink     = new FakeSink();
            var conn     = new ProviderConnection { Direction = SyncDirection.Off };

            var orch   = BuildOrchestrator(sink: sink);
            var result = await orch.SyncOneAsync("u1", "Alice", provider, conn, CancellationToken.None);

            Assert.Equal(0, result.Pushed);
            Assert.Equal(0, result.Pulled);
            Assert.Equal(0, result.Skipped);
            Assert.Null(result.Error);
            Assert.False(provider.EnsureTokenCalled);
            Assert.Empty(sink.Saves);
        }

        // ------------------------------------------------------------------
        // Test 2: TwoWay -- remote has 1 not in local -> imported;
        //                    local has 1 not on remote -> pushed
        // ------------------------------------------------------------------

        [Fact]
        public async Task TwoWay_ImportsPullAndPushesLocal()
        {
            // Remote has "tt-remote" (not in local)
            var remoteRating = new ExternalRating("tt-remote", null, null, "Remote Film", 2021, "movie", 3.5, T0);
            var provider     = new FakeProvider(new[] { remoteRating });

            // Local has "tt-local" (not on remote)
            var localRating = new ExternalRating("tt-local", null, null, "Local Film", 2022, "movie", 4.5, T0);
            var gatherer    = new FakeGatherer(new[] { localRating });

            // Resolver maps "tt-remote" -> "item-remote-id"
            var resolver = new FakeResolver(new Dictionary<string, string>
            {
                ["tt-remote"] = "item-remote-id"
            });

            var sink = new FakeSink();
            var conn = new ProviderConnection { Direction = SyncDirection.TwoWay };

            var orch   = BuildOrchestrator(gatherer, sink, resolver);
            var result = await orch.SyncOneAsync("user1", "Alice", provider, conn, CancellationToken.None);

            // Import: remoteRating resolved and saved
            Assert.Equal(1, result.Pulled);
            Assert.Single(sink.Saves);
            Assert.Equal("item-remote-id", sink.Saves[0].itemId);
            Assert.Equal(3.5, sink.Saves[0].stars);

            // Export: localRating not on remote -> pushed
            Assert.Equal(1, result.Pushed);
            Assert.Single(provider.PushCalls);
            Assert.Single(provider.PushCalls[0]);
            Assert.Equal("tt-local", provider.PushCalls[0][0].Imdb);

            // conn state updated
            Assert.NotNull(conn.LastSyncedAt);
            Assert.Equal(1, conn.LastPushed);
            Assert.Equal(1, conn.LastPulled);
            Assert.Null(conn.LastError);
        }

        // ------------------------------------------------------------------
        // Test 3: Dedup -- same rating on both sides with same stars
        //         -> not re-imported (Skipped), not re-pushed (excluded from toPush)
        // ------------------------------------------------------------------

        [Fact]
        public async Task TwoWay_Dedup_SameRatingBothSides_NotReImportedNotRePushed()
        {
            var sharedImdb  = "tt-shared";
            var sharedStars = 4.0;
            var sharedRating = new ExternalRating(sharedImdb, null, null, "Shared Film", 2020, "movie", sharedStars, T0);

            // Both remote and local have the same rating.
            var provider = new FakeProvider(new[] { sharedRating });
            var gatherer = new FakeGatherer(new[] { sharedRating });

            // Resolver maps it to "item-shared"
            var resolver = new FakeResolver(new Dictionary<string, string>
            {
                [sharedImdb] = "item-shared"
            });

            // Pre-seed the sink so GetUserStarsAsync returns the current stars.
            var sink = new FakeSink();
            sink.Seed("user1", "item-shared", sharedStars);

            var conn = new ProviderConnection { Direction = SyncDirection.TwoWay };
            var orch = BuildOrchestrator(gatherer, sink, resolver);

            var result = await orch.SyncOneAsync("user1", "Alice", provider, conn, CancellationToken.None);

            // Import: resolved but stars match -> Skipped (not Pulled)
            Assert.Equal(1, result.Skipped);
            Assert.Equal(0, result.Pulled);
            Assert.Empty(sink.Saves);

            // Export: local rating already on remote with same stars -> not pushed
            Assert.Equal(0, result.Pushed);
            Assert.Empty(provider.PushCalls);
        }

        // ------------------------------------------------------------------
        // Test 4: ImportOnly -- FindItemId returns null -> Skipped, not saved
        // ------------------------------------------------------------------

        [Fact]
        public async Task ImportOnly_UnresolvableItem_IsSkipped()
        {
            var remoteRating = new ExternalRating("tt-unknown", null, null, "Unknown Film", 2019, "movie", 2.5, T0);
            var provider     = new FakeProvider(new[] { remoteRating });

            // Resolver returns null for everything
            var resolver = new FakeResolver(new Dictionary<string, string>());
            var sink     = new FakeSink();
            var conn     = new ProviderConnection { Direction = SyncDirection.ImportOnly };

            var orch   = BuildOrchestrator(sink: sink, resolver: resolver);
            var result = await orch.SyncOneAsync("user1", "Alice", provider, conn, CancellationToken.None);

            Assert.Equal(1, result.Skipped);
            Assert.Equal(0, result.Pulled);
            Assert.Empty(sink.Saves);
        }

        // ------------------------------------------------------------------
        // Test 5: Provider throws on Pull -> result.Error set, no throw,
        //         conn.LastError set
        // ------------------------------------------------------------------

        [Fact]
        public async Task ProviderThrows_ErrorCaptured_NoThrow_ConnLastErrorSet()
        {
            var provider = new FakeProvider();
            provider.OnPull = (_, _) => throw new InvalidOperationException("network exploded");

            var conn = new ProviderConnection { Direction = SyncDirection.TwoWay };
            var orch = BuildOrchestrator();

            // Must NOT throw
            var result = await orch.SyncOneAsync("user1", "Alice", provider, conn, CancellationToken.None);

            Assert.NotNull(result.Error);
            Assert.Contains("network exploded", result.Error);
            Assert.NotNull(conn.LastError);
            Assert.Contains("network exploded", conn.LastError);

            // conn timestamp must NOT have been set (exception happened before step 6)
            Assert.Null(conn.LastSyncedAt);
        }

        // ------------------------------------------------------------------
        // Test 6: ExportOnly -- pulls to dedup but does NOT import
        // ------------------------------------------------------------------

        [Fact]
        public async Task ExportOnly_PullsForDedup_DoesNotImport()
        {
            // Remote has a rating (should NOT be imported)
            var remoteRating = new ExternalRating("tt-remote", null, null, "Remote Film", 2021, "movie", 3.0, T0);
            var provider     = new FakeProvider(new[] { remoteRating });

            // Local has a DIFFERENT rating (should be pushed because remote lacks it)
            var localRating = new ExternalRating("tt-local", null, null, "Local Film", 2022, "movie", 5.0, T0);
            var gatherer    = new FakeGatherer(new[] { localRating });

            // Resolver: map "tt-remote" so import WOULD work if it ran
            var resolver = new FakeResolver(new Dictionary<string, string>
            {
                ["tt-remote"] = "item-remote-id"
            });

            var sink = new FakeSink();
            var conn = new ProviderConnection { Direction = SyncDirection.ExportOnly };

            var orch   = BuildOrchestrator(gatherer, sink, resolver);
            var result = await orch.SyncOneAsync("user1", "Alice", provider, conn, CancellationToken.None);

            // Import must NOT have happened (ExportOnly)
            Assert.Equal(0, result.Pulled);
            Assert.Empty(sink.Saves);

            // Export: local rating not on remote -> pushed
            Assert.Equal(1, result.Pushed);
            Assert.Single(provider.PushCalls);

            // EnsureToken was called
            Assert.True(provider.EnsureTokenCalled);
        }

        // ------------------------------------------------------------------
        // Test 7: ExportOnly dedup -- local rating already on remote with same
        //         stars -> NOT re-pushed (dedup works for ExportOnly too)
        // ------------------------------------------------------------------

        [Fact]
        public async Task ExportOnly_AlreadyOnRemote_SameStars_NotPushed()
        {
            var sharedRating = new ExternalRating("tt-shared", null, null, "Shared", 2020, "movie", 4.0, T0);

            // Remote already has it at the same stars
            var provider = new FakeProvider(new[] { sharedRating });
            var gatherer = new FakeGatherer(new[] { sharedRating });
            var sink     = new FakeSink();
            var conn     = new ProviderConnection { Direction = SyncDirection.ExportOnly };

            var orch   = BuildOrchestrator(gatherer, sink, new FakeResolver(new Dictionary<string, string>()));
            var result = await orch.SyncOneAsync("user1", "Alice", provider, conn, CancellationToken.None);

            Assert.Equal(0, result.Pushed);
            Assert.Empty(provider.PushCalls);
        }

        // ------------------------------------------------------------------
        // Test 8: Import where stars differ -> re-imported (not skipped)
        // ------------------------------------------------------------------

        [Fact]
        public async Task ImportOnly_StarsDiffer_IsImportedNotSkipped()
        {
            var remoteRating = new ExternalRating("tt-updated", null, null, "Updated Film", 2020, "movie", 4.5, T0);
            var provider     = new FakeProvider(new[] { remoteRating });

            var resolver = new FakeResolver(new Dictionary<string, string>
            {
                ["tt-updated"] = "item-updated"
            });

            var sink = new FakeSink();
            sink.Seed("user1", "item-updated", 3.0); // old stars differ from remote 4.5

            var conn = new ProviderConnection { Direction = SyncDirection.ImportOnly };
            var orch = BuildOrchestrator(sink: sink, resolver: resolver);

            var result = await orch.SyncOneAsync("user1", "Alice", provider, conn, CancellationToken.None);

            Assert.Equal(1, result.Pulled);
            Assert.Equal(0, result.Skipped);
            Assert.Single(sink.Saves);
            Assert.Equal(4.5, sink.Saves[0].stars);
        }

        // ------------------------------------------------------------------
        // Test 9: Import clamp — a rating of 0 stars is skipped, not saved
        // ------------------------------------------------------------------

        [Fact]
        public async Task ImportOnly_ZeroStars_IsSkipped_NotSaved()
        {
            // A remote rating with 0 stars (invalid — below the 0.5 floor)
            var zeroRating = new ExternalRating("tt-zero", null, null, "Zero Stars Film", 2020, "movie", 0.0, T0);
            var provider   = new FakeProvider(new[] { zeroRating });

            var resolver = new FakeResolver(new Dictionary<string, string>
            {
                ["tt-zero"] = "item-zero"
            });

            var sink = new FakeSink();
            var conn = new ProviderConnection { Direction = SyncDirection.ImportOnly };
            var orch = BuildOrchestrator(sink: sink, resolver: resolver);

            var result = await orch.SyncOneAsync("user1", "Alice", provider, conn, CancellationToken.None);

            // 0 stars is below the 0.5 floor -> must be skipped, not saved
            Assert.Equal(1, result.Skipped);
            Assert.Equal(0, result.Pulled);
            Assert.Empty(sink.Saves);
        }
    }
}
