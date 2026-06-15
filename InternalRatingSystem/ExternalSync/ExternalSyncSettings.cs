using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.InternalRating.ExternalSync
{
    public enum SyncDirection { Off = 0, ExportOnly = 1, ImportOnly = 2, TwoWay = 3 }

    public sealed class ProviderConnection
    {
        [JsonPropertyName("direction")]     public SyncDirection Direction { get; set; } = SyncDirection.Off;
        [JsonPropertyName("accessToken")]   public string? AccessToken { get; set; }
        [JsonPropertyName("refreshToken")]  public string? RefreshToken { get; set; }
        [JsonPropertyName("tokenExpiresAt")]public DateTime? TokenExpiresAt { get; set; }
        [JsonPropertyName("baseUrl")]       public string? BaseUrl { get; set; }
        [JsonPropertyName("apiToken")]      public string? ApiToken { get; set; }
        [JsonPropertyName("lastSyncedAt")]  public DateTime? LastSyncedAt { get; set; }
        [JsonPropertyName("lastPushed")]    public int LastPushed { get; set; }
        [JsonPropertyName("lastPulled")]    public int LastPulled { get; set; }
        [JsonPropertyName("lastError")]     public string? LastError { get; set; }
    }

    public sealed class ExternalSyncUserSettings
    {
        [JsonPropertyName("providers")]
        public Dictionary<string, ProviderConnection> Providers { get; set; } = new();
    }

    public sealed class ExternalSyncStore
    {
        [JsonPropertyName("users")]
        public Dictionary<string, ExternalSyncUserSettings> Users { get; set; } = new();
    }

    public sealed class SyncResult
    {
        [JsonPropertyName("pushed")]  public int Pushed { get; set; }
        [JsonPropertyName("pulled")]  public int Pulled { get; set; }
        [JsonPropertyName("skipped")] public int Skipped { get; set; }
        [JsonPropertyName("error")]   public string? Error { get; set; }
    }

    public sealed record ExternalRating(
        string? Imdb, int? Tmdb, int? Tvdb,
        string Title, int? Year, string MediaType,
        double Stars, DateTime RatedAt);
}
