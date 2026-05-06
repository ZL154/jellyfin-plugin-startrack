using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.InternalRating.Models
{
    /// <summary>
    /// A user-curated list of films that other users on the server can also
    /// contribute to (if the list is collaborative).
    /// </summary>
    public sealed class UserList
    {
        [JsonPropertyName("id")]           public string Id           { get; set; } = string.Empty; // server-unique GUID
        [JsonPropertyName("ownerId")]      public string OwnerId      { get; set; } = string.Empty;
        [JsonPropertyName("ownerName")]    public string OwnerName    { get; set; } = string.Empty;
        [JsonPropertyName("name")]         public string Name         { get; set; } = string.Empty;
        [JsonPropertyName("description")]  public string? Description { get; set; }
        [JsonPropertyName("collaborative")] public bool Collaborative { get; set; } = true;
        // v1.5.12: opt-in private list. Defaults to false so existing lists
        // remain publicly visible. Private lists are visible only to the
        // owner via GetAllLists / GetList.
        [JsonPropertyName("isPrivate")]    public bool IsPrivate      { get; set; }
        [JsonPropertyName("createdAt")]    public DateTime CreatedAt  { get; set; }
        [JsonPropertyName("items")]        public List<ListItem> Items { get; set; } = new();
    }

    public sealed class ListItem
    {
        [JsonPropertyName("itemId")]   public string   ItemId   { get; set; } = string.Empty;
        [JsonPropertyName("addedBy")]  public string   AddedBy  { get; set; } = string.Empty;
        [JsonPropertyName("addedByName")] public string AddedByName { get; set; } = string.Empty;
        [JsonPropertyName("addedAt")]  public DateTime AddedAt  { get; set; }
    }

    /// <summary>Top-level store — list of lists.</summary>
    public sealed class ListsStore
    {
        [JsonPropertyName("lists")] public List<UserList> Lists { get; set; } = new();
    }

    // ========================= API DTOs ============================ //

    public sealed class CreateListRequest
    {
        [MaxLength(120)]
        public string? Name          { get; set; }
        [MaxLength(2000)]
        public string? Description   { get; set; }
        public bool?   Collaborative { get; set; }
        public bool?   IsPrivate     { get; set; }
    }

    public sealed class AddListItemRequest
    {
        public string? ItemId { get; set; }
    }
}
