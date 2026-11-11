using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.InternalRating.Data
{
    /// <summary>
    /// What a stored Jellyfin item id refers to, in terms that survive the id
    /// itself changing. Jellyfin derives an item's id from its file path, so a
    /// Radarr or Sonarr upgrade that renames the file removes the item and
    /// re-adds it under a new id; this is what lets the plugin find the new one.
    ///
    /// Movies and series carry their own provider ids. A season is identified
    /// by its series' ids plus its index; an episode by its own ids when it
    /// has any, and by its series' ids plus season and episode index otherwise.
    /// </summary>
    public sealed class ItemIdentity
    {
        /// <summary>"movie", "series", "season" or "episode".</summary>
        [JsonPropertyName("kind")]   public string Kind { get; set; } = string.Empty;
        [JsonPropertyName("name")]   public string Name { get; set; } = string.Empty;
        [JsonPropertyName("year")]   public int? Year { get; set; }

        /// <summary>The item's own provider ids: Tmdb, Imdb, Tvdb.</summary>
        [JsonPropertyName("ids")]    public Dictionary<string, string> Ids { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The series' provider ids, for seasons and episodes.</summary>
        [JsonPropertyName("series")] public Dictionary<string, string>? SeriesIds { get; set; }
        [JsonPropertyName("season")] public int? SeasonNumber { get; set; }
        [JsonPropertyName("episode")] public int? EpisodeNumber { get; set; }

        /// <summary>Provider names worth recording. Anything else (collection ids, local ids) does not identify a title.</summary>
        internal static readonly string[] Providers = { "Tmdb", "Imdb", "Tvdb" };

        /// <summary>
        /// Match keys: two identities refer to the same title when they are of
        /// the same kind and share at least one key. Keys carry the kind so a
        /// film and a series with the same Imdb id can never match.
        /// </summary>
        public IEnumerable<string> Keys()
        {
            foreach (var kv in Ids)
                yield return Kind + ":" + kv.Key.ToLowerInvariant() + ":" + kv.Value.Trim().ToLowerInvariant();

            if (SeriesIds == null) yield break;

            string? suffix = Kind switch
            {
                "season"  when SeasonNumber.HasValue                    => "/s" + SeasonNumber.Value.ToString(CultureInfo.InvariantCulture),
                "episode" when SeasonNumber.HasValue && EpisodeNumber.HasValue => "/s" + SeasonNumber.Value.ToString(CultureInfo.InvariantCulture)
                                                                     + "e" + EpisodeNumber.Value.ToString(CultureInfo.InvariantCulture),
                _ => null
            };
            if (suffix == null) yield break;

            foreach (var kv in SeriesIds)
                yield return Kind + ":" + kv.Key.ToLowerInvariant() + ":" + kv.Value.Trim().ToLowerInvariant() + suffix;
        }

        /// <summary>True when nothing here could identify the title. Such items are never moved.</summary>
        public bool IsEmpty() => !Keys().Any();

        public bool SameAs(ItemIdentity other)
        {
            if (!string.Equals(Kind, other.Kind, StringComparison.Ordinal)) return false;
            var mine = new HashSet<string>(Keys(), StringComparer.Ordinal);
            return other.Keys().Any(mine.Contains);
        }

        /// <summary>
        /// Builds the identity of a library item. <paramref name="series"/> is
        /// the parent series for a season or an episode; the caller resolves it
        /// so this stays free of ILibraryManager and testable.
        /// </summary>
        public static ItemIdentity? FromItem(BaseItem item, BaseItem? series = null)
        {
            var kind = item switch
            {
                Movie   => "movie",
                Series  => "series",
                Season  => "season",
                Episode => "episode",
                _       => null
            };
            if (kind == null) return null;

            var id = new ItemIdentity
            {
                Kind = kind,
                Name = item.Name ?? string.Empty,
                Year = item.ProductionYear,
                Ids  = Pick(item.ProviderIds)
            };

            switch (item)
            {
                case Season s:
                    id.SeasonNumber = s.IndexNumber;
                    id.SeriesIds = series != null ? Pick(series.ProviderIds) : null;
                    break;
                case Episode e:
                    id.SeasonNumber = e.ParentIndexNumber;
                    id.EpisodeNumber = e.IndexNumber;
                    id.SeriesIds = series != null ? Pick(series.ProviderIds) : null;
                    break;
            }

            return id;
        }

        private static Dictionary<string, string> Pick(IReadOnlyDictionary<string, string>? providerIds)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (providerIds == null) return d;
            foreach (var p in Providers)
            {
                if (providerIds.TryGetValue(p, out var v) && !string.IsNullOrWhiteSpace(v))
                    d[p] = v.Trim();
            }
            return d;
        }
    }
}
