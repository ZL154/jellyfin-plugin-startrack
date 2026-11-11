using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.InternalRating.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating
{
    /// <summary>What one relink sweep did.</summary>
    public sealed class RelinkSummary
    {
        /// <summary>Stored ids examined.</summary>
        [JsonPropertyName("items")]      public int Items { get; set; }
        /// <summary>Identities written or refreshed in the ledger.</summary>
        [JsonPropertyName("recorded")]   public int Recorded { get; set; }
        /// <summary>Titles whose entries moved to a new id.</summary>
        [JsonPropertyName("relinked")]   public int Relinked { get; set; }
        /// <summary>Stored ids the library no longer knows and the ledger has nothing on. Nothing can be done for these.</summary>
        [JsonPropertyName("unrecorded")] public int Unrecorded { get; set; }
        /// <summary>Stored ids with a record but no item in the library carrying those provider ids: the title is gone, or its metadata has not arrived yet.</summary>
        [JsonPropertyName("unmatched")]  public int Unmatched { get; set; }
        /// <summary>Stored ids whose recorded ids fit more than one item. Left where they are; see the log.</summary>
        [JsonPropertyName("ambiguous")]  public int Ambiguous { get; set; }
        /// <summary>Ledger records dropped because no store references the id any more.</summary>
        [JsonPropertyName("pruned")]     public int Pruned { get; set; }
    }

    /// <summary>
    /// Keeps the stores attached to a title when Jellyfin gives it a new item id.
    ///
    /// Jellyfin derives an item's id from its file path. A Radarr or Sonarr
    /// upgrade renames the file, so the item is removed and re-added under a
    /// new id, and every rating, diary entry, watchlist entry and list entry
    /// keyed on the old id points at nothing. Jellyfin keeps its own per-user
    /// data through this by keying it on provider ids and reattaching the rows
    /// to the next item with the same key. This service does the same for the
    /// plugin's stores: it records what each stored id refers to in
    /// <see cref="ItemIdentityLedger"/>, and when a stored id stops resolving it
    /// looks the recorded provider ids up and moves the entries.
    ///
    /// The sweep runs a little after library item events go quiet, so a scan
    /// that touches a thousand items costs one pass, and once at startup for
    /// changes made while the server was off. Provider ids arrive with the
    /// metadata refresh, after <c>ItemAdded</c>, which is why the trigger is
    /// debounced rather than acted on per event.
    /// </summary>
    public sealed class LibraryRelinkService : IHostedService, IDisposable
    {
        /// <summary>How long the library has to be quiet before a sweep runs.</summary>
        internal static readonly TimeSpan Quiet = TimeSpan.FromSeconds(30);
        /// <summary>Delay before the startup sweep, so it does not compete with Jellyfin's own startup work.</summary>
        internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);

        private readonly ILibraryManager _library;
        private readonly ItemIdentityLedger _ledger;
        private readonly RatingRepository _ratings;
        private readonly DiaryRepository _diary;
        private readonly UserInteractionsRepository _interactions;
        private readonly ListsRepository _lists;
        private readonly ILogger<LibraryRelinkService> _logger;

        private readonly SemaphoreSlim _running = new(1, 1);
        private Timer? _timer;
        private volatile bool _stopping;

        public LibraryRelinkService(
            ILibraryManager library,
            ItemIdentityLedger ledger,
            RatingRepository ratings,
            DiaryRepository diary,
            UserInteractionsRepository interactions,
            ListsRepository lists,
            ILogger<LibraryRelinkService> logger)
        {
            _library      = library;
            _ledger       = ledger;
            _ratings      = ratings;
            _diary        = diary;
            _interactions = interactions;
            _lists        = lists;
            _logger       = logger;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _timer = new Timer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _library.ItemAdded   += OnLibraryChanged;
            _library.ItemUpdated += OnLibraryChanged;
            _library.ItemRemoved += OnLibraryChanged;
            Schedule(StartupDelay);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _stopping = true;
            _library.ItemAdded   -= OnLibraryChanged;
            _library.ItemUpdated -= OnLibraryChanged;
            _library.ItemRemoved -= OnLibraryChanged;
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            // Let a sweep in flight finish its writes before Dispose takes the lock away.
            try
            {
                await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
                _running.Release();
            }
            catch (OperationCanceledException) { }
        }

        private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
        {
            // Only the kinds the stores can hold. Images, people and folders
            // change constantly and none of it can move a rating.
            if (e.Item is Movie || e.Item is Series || e.Item is Season || e.Item is Episode)
                Schedule(Quiet);
        }

        private void Schedule(TimeSpan delay)
        {
            try { _timer?.Change(delay, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }

        private void Fire()
        {
            if (_stopping) return;
            _ = Task.Run(async () =>
            {
                // Checked again here: StopAsync can run between the timer
                // firing and this body starting, and a sweep that begins
                // after it would run through the host's teardown.
                if (_stopping) return;
                try { await RunAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "[StarTrack] Relink sweep failed."); }
            });
        }

        /// <summary>
        /// One sweep over every id the stores reference: record the identity of
        /// those the library knows, move those it no longer knows to the item
        /// that now carries their provider ids, and drop ledger records nothing
        /// references. Sweeps run one at a time.
        /// </summary>
        public async Task<RelinkSummary> RunAsync(CancellationToken cancellationToken = default)
        {
            await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var summary = new RelinkSummary();
                var ids = await ReferencedIdsAsync().ConfigureAwait(false);
                summary.Items = ids.Count;
                // Identities are written in one batch at the end: the first
                // sweep records every stored id, and a file write per id would
                // rewrite the ledger hundreds of times.
                var pending = new Dictionary<string, ItemIdentity>(StringComparer.OrdinalIgnoreCase);

                foreach (var id in ids)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Guid.TryParse(id, out var guid)) continue;

                    var item = Find(guid);
                    if (item != null)
                    {
                        var identity = Describe(item);
                        if (identity != null) pending[id] = identity;
                        continue;
                    }

                    var recorded = await _ledger.GetAsync(id).ConfigureAwait(false);
                    if (recorded == null || recorded.IsEmpty())
                    {
                        summary.Unrecorded++;
                        continue;
                    }

                    var target = Locate(recorded, guid, out var ambiguous);
                    if (target == null)
                    {
                        if (ambiguous)
                        {
                            summary.Ambiguous++;
                            _logger.LogInformation(
                                "[StarTrack] Not relinking \"{Name}\"{Year} from {Old}: more than one item carries its ids",
                                recorded.Name, recorded.Year.HasValue ? " (" + recorded.Year + ")" : string.Empty, id);
                        }
                        else summary.Unmatched++;
                        continue;
                    }

                    var newId = target.Id.ToString("N");
                    var moved = await MoveAsync(id, newId).ConfigureAwait(false);
                    await _ledger.RenameAsync(id, newId).ConfigureAwait(false);
                    var fresh = Describe(target);
                    if (fresh != null) pending[newId] = fresh;
                    summary.Relinked++;

                    _logger.LogInformation(
                        "[StarTrack] Relinked \"{Name}\"{Year}: {Old} -> {New} ({Ratings} ratings, {Diary} diary entries, {Interactions} watchlist/like/favourite entries, {Lists} list entries)",
                        recorded.Name, recorded.Year.HasValue ? " (" + recorded.Year + ")" : string.Empty,
                        id, newId, moved.Ratings, moved.Diary, moved.Interactions, moved.Lists);
                }

                summary.Recorded = await _ledger.RecordManyAsync(pending).ConfigureAwait(false);
                var referenced = new HashSet<string>(await ReferencedIdsAsync().ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
                summary.Pruned = await _ledger.PruneAsync(referenced).ConfigureAwait(false);

                if (summary.Relinked > 0 || summary.Recorded > 0 || summary.Ambiguous > 0)
                    _logger.LogInformation(
                        "[StarTrack] Relink sweep: {Items} stored ids, {Recorded} recorded, {Relinked} relinked, {Ambiguous} ambiguous, {Unmatched} unmatched, {Unrecorded} without a record, {Pruned} pruned",
                        summary.Items, summary.Recorded, summary.Relinked, summary.Ambiguous, summary.Unmatched, summary.Unrecorded, summary.Pruned);
                else
                    _logger.LogDebug(
                        "[StarTrack] Relink sweep: {Items} stored ids, nothing to do ({Unmatched} unmatched, {Unrecorded} without a record)",
                        summary.Items, summary.Unmatched, summary.Unrecorded);

                return summary;
            }
            finally { _running.Release(); }
        }

        private async Task<List<string>> ReferencedIdsAsync()
        {
            var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            all.UnionWith(await _ratings.GetAllItemIdsAsync().ConfigureAwait(false));
            all.UnionWith(await _diary.GetAllItemIdsAsync().ConfigureAwait(false));
            all.UnionWith(await _interactions.GetAllItemIdsAsync().ConfigureAwait(false));
            all.UnionWith(await _lists.GetAllItemIdsAsync().ConfigureAwait(false));
            return all.ToList();
        }

        private async Task<(int Ratings, int Diary, int Interactions, int Lists)> MoveAsync(string oldId, string newId)
        {
            var r = await _ratings.RenameItemAsync(oldId, newId).ConfigureAwait(false);
            var d = await _diary.RenameItemAsync(oldId, newId).ConfigureAwait(false);
            var i = await _interactions.RenameItemAsync(oldId, newId).ConfigureAwait(false);
            var l = await _lists.RenameItemAsync(oldId, newId).ConfigureAwait(false);
            return (r, d, i, l);
        }

        private BaseItem? Find(Guid id)
        {
            try { return _library.GetItemById(id); }
            catch { return null; }
        }

        /// <summary>The identity of a library item, with the series resolved for seasons and episodes.</summary>
        private ItemIdentity? Describe(BaseItem item)
        {
            BaseItem? series = item switch
            {
                Season s  when !s.SeriesId.Equals(Guid.Empty) => Find(s.SeriesId),
                Episode e when !e.SeriesId.Equals(Guid.Empty) => Find(e.SeriesId),
                _ => null
            };
            return ItemIdentity.FromItem(item, series);
        }

        /// <summary>
        /// The item that now carries a recorded identity, or null. Never the id
        /// being replaced, never an alternate version or an extra, and never a
        /// title match: an item without provider ids stays where it is, since a
        /// wrong move is worse than a miss. When more than one item fits,
        /// <paramref name="ambiguous"/> is set and nothing is returned, for the
        /// same reason.
        /// </summary>
        private BaseItem? Locate(ItemIdentity identity, Guid exclude, out bool ambiguous)
        {
            ambiguous = false;
            switch (identity.Kind)
            {
                case "movie":
                    return ByProviderIds(identity.Ids, BaseItemKind.Movie, exclude, out ambiguous);
                case "series":
                    return ByProviderIds(identity.Ids, BaseItemKind.Series, exclude, out ambiguous);
                case "season":
                {
                    if (!identity.SeasonNumber.HasValue || identity.SeriesIds == null) return null;
                    var series = ByProviderIds(identity.SeriesIds, BaseItemKind.Series, Guid.Empty, out ambiguous);
                    if (series == null) return null;
                    return Query(new InternalItemsQuery
                    {
                        ParentId         = series.Id,
                        IncludeItemTypes = new[] { BaseItemKind.Season },
                        IndexNumber      = identity.SeasonNumber
                    }, exclude, out ambiguous);
                }
                case "episode":
                {
                    var own = ByProviderIds(identity.Ids, BaseItemKind.Episode, exclude, out ambiguous);
                    if (own != null || ambiguous) return own;
                    if (!identity.SeasonNumber.HasValue || !identity.EpisodeNumber.HasValue || identity.SeriesIds == null) return null;
                    var series = ByProviderIds(identity.SeriesIds, BaseItemKind.Series, Guid.Empty, out ambiguous);
                    if (series == null) return null;
                    return Query(new InternalItemsQuery
                    {
                        AncestorIds       = new[] { series.Id },
                        IncludeItemTypes  = new[] { BaseItemKind.Episode },
                        ParentIndexNumber = identity.SeasonNumber,
                        IndexNumber       = identity.EpisodeNumber,
                        Recursive         = true
                    }, exclude, out ambiguous);
                }
                default:
                    return null;
            }
        }

        private BaseItem? ByProviderIds(Dictionary<string, string> ids, BaseItemKind kind, Guid exclude, out bool ambiguous)
        {
            ambiguous = false;
            if (ids.Count == 0) return null;
            return Query(new InternalItemsQuery
            {
                HasAnyProviderId = new Dictionary<string, string>(ids, StringComparer.OrdinalIgnoreCase),
                IncludeItemTypes = new[] { kind },
                Recursive        = true
            }, exclude, out ambiguous);
        }

        private BaseItem? Query(InternalItemsQuery query, Guid exclude, out bool ambiguous)
        {
            ambiguous = false;
            try
            {
                var hits = _library.GetItemList(query);
                return PickCandidate(hits, exclude, out ambiguous);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StarTrack] Relink lookup failed");
                return null;
            }
        }

        /// <summary>
        /// The one item a lookup may move entries to. Alternate versions of a
        /// film are Movie rows that carry the primary's provider ids; moving
        /// entries onto one would hide them behind the primary, so they are
        /// not candidates, and neither are extras, which belong to another
        /// item. The exception is an alternate whose primary is the vanished
        /// id itself: on Jellyfin 10.11 nothing clears that link when the
        /// primary is deleted, so that alternate is what the client now shows.
        /// Two remaining candidates (two copies of a film in different
        /// libraries, say) make the lookup ambiguous and nothing is returned.
        /// </summary>
        internal static BaseItem? PickCandidate(IReadOnlyList<BaseItem>? hits, Guid exclude, out bool ambiguous)
        {
            ambiguous = false;
            if (hits == null) return null;
            BaseItem? found = null;
            foreach (var h in hits)
            {
                if (h == null || h.Id.Equals(exclude)) continue;
                if (!h.OwnerId.Equals(Guid.Empty)) continue;
                if (h is Video v)
                {
                    var primary = PrimaryVersionOf(v);
                    if (primary.HasValue && !primary.Value.Equals(exclude)) continue;
                }
                if (found != null && !found.Id.Equals(h.Id))
                {
                    ambiguous = true;
                    return null;
                }
                found = h;
            }
            return found;
        }

        /// <summary>
        /// The primary this video is an alternate of, or null when it is a
        /// primary itself. PrimaryVersionId is a string on Jellyfin 10.11 and
        /// a Guid? on 12; boxing it reads both from one source file.
        /// </summary>
        private static Guid? PrimaryVersionOf(Video v)
        {
            object? primary = v.PrimaryVersionId;
            return primary switch
            {
                Guid g when !g.Equals(Guid.Empty)                 => g,
                string t when Guid.TryParse(t, out var parsed)
                           && !parsed.Equals(Guid.Empty)          => parsed,
                _                                                 => null
            };
        }

        public void Dispose()
        {
            // The semaphore is left to the collector: a sweep queued just
            // before shutdown may still hold it, and SemaphoreSlim owns
            // nothing that needs an explicit release.
            _timer?.Dispose();
        }
    }
}
