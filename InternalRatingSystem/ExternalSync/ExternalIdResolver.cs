using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.InternalRating.ExternalSync
{
    /// <summary>
    /// Abstraction over the ILibraryManager-dependent resolver methods so that
    /// <c>RatingGatherer</c> (and tests) can substitute a fake without needing
    /// a real Jellyfin library.
    /// </summary>
    public interface IExternalIdResolver
    {
        /// <summary>
        /// Given a StarTrack item-id GUID string, looks up the library item and
        /// builds an <see cref="ExternalRating"/> populated with provider IDs.
        /// Returns <c>null</c> if the item is not found.
        /// </summary>
        ExternalRating? ResolveExternalIds(string itemId, double stars, DateTime ratedAt);

        /// <summary>
        /// Tries to locate a library item that matches the external rating.
        /// Returns the Jellyfin item GUID as a "N"-format string, or <c>null</c>.
        /// </summary>
        string? FindItemId(ExternalRating r);
    }

    /// <summary>
    /// Resolves Jellyfin item IDs to external provider IDs (IMDb, TMDb, TVDb)
    /// and vice-versa, bridging the gap between StarTrack's internal GUIDs and
    /// the identifiers used by external rating services (Trakt, Simkl, etc.).
    ///
    /// Design: pure mapping helpers (<see cref="MapToExternalRating"/> and
    /// <see cref="NormalizeTitle"/>) are <c>internal static</c> so they can be
    /// unit-tested without any ILibraryManager fake. The ILibraryManager glue
    /// (<see cref="ResolveExternalIds"/> and <see cref="FindItemId"/>) is thin
    /// and intentionally left uncovered by unit tests.
    /// </summary>
    public sealed class ExternalIdResolver : IExternalIdResolver
    {
        private readonly ILibraryManager _libraryManager;

        public ExternalIdResolver(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        // ============================================================= //
        // PUBLIC GLUE — thin ILibraryManager wrappers (not unit-tested)
        // ============================================================= //

        /// <summary>
        /// Given a StarTrack item-id string (a Jellyfin GUID serialised via
        /// <c>.ToString("N")</c>), looks up the library item and builds an
        /// <see cref="ExternalRating"/> populated with the item's provider IDs.
        /// Returns <c>null</c> if the GUID is unparseable or the item is not
        /// found in the library.
        /// </summary>
        public ExternalRating? ResolveExternalIds(string itemId, double stars, DateTime ratedAt)
        {
            if (!Guid.TryParse(itemId, out var guid))
                return null;

            BaseItem? item;
            try { item = _libraryManager.GetItemById(guid); }
            catch { item = null; }

            if (item == null)
                return null;

            var mediaType = item switch
            {
                Movie   => "movie",
                Series  => "show",
                Episode => "episode",
                _       => "movie"
            };

            return MapToExternalRating(
                item.ProviderIds,
                item.Name ?? string.Empty,
                item.ProductionYear,
                mediaType,
                stars,
                ratedAt);
        }

        /// <summary>
        /// Tries to locate a library item that matches the external rating <paramref name="r"/>.
        /// Strategy (in order):
        ///   1. Provider-ID query via <see cref="ILibraryManager.GetItemList"/> with
        ///      <c>HasAnyProviderId</c> — fastest and most accurate when the Jellyfin DB
        ///      has been enriched with IMDb/TMDb/TVDb metadata.
        ///   2. Normalised title + year scan across all items of the matching type —
        ///      fallback for libraries whose items lack provider-ID metadata.
        /// Returns <c>item.Id.ToString("N")</c> or <c>null</c> if no match is found.
        ///
        /// NOTE: This method is ILibraryManager glue and is intentionally not covered
        /// by unit tests. The pure helpers it relies on (MapToExternalRating,
        /// NormalizeTitle) are fully tested.
        /// </summary>
        public string? FindItemId(ExternalRating r)
        {
            var types = r.MediaType switch
            {
                "show"    => new[] { BaseItemKind.Series },
                "episode" => new[] { BaseItemKind.Episode },
                _         => new[] { BaseItemKind.Movie }
            };

            // ---- Strategy 1: provider-ID query ----
            // Build a Dictionary<string,string> of external IDs from the
            // ExternalRating and ask Jellyfin to find an item that matches.
            // InternalItemsQuery.HasAnyProviderId is Dictionary<string,string>
            // where keys are provider names ("Imdb", "Tmdb", "Tvdb") and values
            // are the ID strings. Jellyfin returns items where ANY of the
            // supplied provider IDs match.
            var providerDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(r.Imdb))
                providerDict["Imdb"] = r.Imdb;
            if (r.Tmdb.HasValue)
                providerDict["Tmdb"] = r.Tmdb.Value.ToString(CultureInfo.InvariantCulture);
            if (r.Tvdb.HasValue)
                providerDict["Tvdb"] = r.Tvdb.Value.ToString(CultureInfo.InvariantCulture);

            if (providerDict.Count > 0)
            {
                try
                {
                    var query = new InternalItemsQuery
                    {
                        HasAnyProviderId = providerDict,
                        IncludeItemTypes = types,
                        Recursive        = true,
                        Limit            = 1
                    };
                    var results = _libraryManager.GetItemList(query);
                    var hit = results?.FirstOrDefault();
                    if (hit != null)
                        return hit.Id.ToString("N");
                }
                catch (Exception)
                {
                    // best-effort: GetItemList can throw on unsupported query shapes in some
                    // Jellyfin builds — fall through to the title scan.
                }
            }

            // ---- Strategy 2: normalised title + year scan ----
            // Pull all items of the correct type and do an in-memory normalized
            // title comparison with optional year check. Mirrors the approach used
            // by LetterboxdSyncService.BuildMovieLookup / MovieLookup.Find.
            try
            {
                var scanQuery = new InternalItemsQuery
                {
                    IncludeItemTypes = types,
                    Recursive        = true
                };
                var all = _libraryManager.GetItemList(scanQuery) ?? (IReadOnlyList<BaseItem>)Array.Empty<BaseItem>();
                var normTarget = NormalizeTitle(r.Title);
                if (string.IsNullOrEmpty(normTarget))
                    return null;

                BaseItem? bestMatch = null;
                foreach (var item in all)
                {
                    if (item == null || string.IsNullOrEmpty(item.Name))
                        continue;
                    if (!string.Equals(NormalizeTitle(item.Name), normTarget, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Prefer year match; accept any match as a fallback.
                    if (r.Year.HasValue && item.ProductionYear.HasValue && item.ProductionYear == r.Year)
                        return item.Id.ToString("N");

                    bestMatch ??= item;
                }

                return bestMatch?.Id.ToString("N");
            }
            catch (Exception)
            {
                // best-effort
                return null;
            }
        }

        // ============================================================= //
        // PURE HELPERS — internal static, unit-tested
        // ============================================================= //

        /// <summary>
        /// Maps a Jellyfin item's provider-ID dictionary and metadata fields to
        /// a neutral <see cref="ExternalRating"/> record.
        ///
        /// Provider-ID key conventions in Jellyfin (PascalCase):
        ///   "Imdb"  → string value like "tt0111161" (kept as-is)
        ///   "Tmdb"  → string, parsed to <c>int?</c>
        ///   "Tvdb"  → string, parsed to <c>int?</c>
        /// </summary>
        internal static ExternalRating MapToExternalRating(
            IReadOnlyDictionary<string, string> providerIds,
            string name,
            int? year,
            string mediaType,
            double stars,
            DateTime ratedAt)
        {
            providerIds.TryGetValue("Imdb", out var imdb);
            if (string.IsNullOrWhiteSpace(imdb)) imdb = null;

            int? tmdb = null;
            if (providerIds.TryGetValue("Tmdb", out var tmdbStr) &&
                int.TryParse(tmdbStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbParsed))
            {
                tmdb = tmdbParsed;
            }

            int? tvdb = null;
            if (providerIds.TryGetValue("Tvdb", out var tvdbStr) &&
                int.TryParse(tvdbStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tvdbParsed))
            {
                tvdb = tvdbParsed;
            }

            return new ExternalRating(imdb, tmdb, tvdb, name, year, mediaType, stars, ratedAt);
        }

        /// <summary>
        /// Aggressive title normalisation for fuzzy library matching. Applies:
        ///   1. Unicode NFD decompose + strip combining marks (diacritics).
        ///   2. All non-letter/digit characters → space.
        ///   3. Lowercase invariant.
        ///   4. Strip leading articles ("the", "a", "an").
        ///   5. Strip trailing articles (after punctuation removal).
        ///   6. Collapse whitespace.
        ///
        /// // DRY-debt: shared with LetterboxdSyncService (private static NormalizeTitle).
        /// If that method changes, update this one too.
        /// </summary>
        internal static string NormalizeTitle(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            // 1. Decompose + strip diacritics
            var decomposed = s.Normalize(NormalizationForm.FormKD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(ch);
            }

            // 2. Punctuation → space; letters/digits → lowercase
            var chars = new StringBuilder(sb.Length);
            foreach (var ch in sb.ToString())
            {
                if (char.IsLetterOrDigit(ch))
                    chars.Append(char.ToLowerInvariant(ch));
                else
                    chars.Append(' ');
            }

            // 3. Collapse whitespace
            var parts = chars.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // 4. Strip leading article
            if (parts.Length > 1 && (parts[0] == "the" || parts[0] == "a" || parts[0] == "an"))
                parts = parts.Skip(1).ToArray();

            // 5. Strip trailing article (comma already removed in step 2, so the
            //    last token is the bare article for titles like "Matrix, The")
            if (parts.Length > 1)
            {
                var last = parts[parts.Length - 1];
                if (last == "the" || last == "a" || last == "an")
                    parts = parts.Take(parts.Length - 1).ToArray();
            }

            return string.Join(' ', parts);
        }
    }
}
