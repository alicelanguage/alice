// Typed schemas shared field for field with baselines/rust/src/typed.rs (partial schemas: unknown fields skipped).
#pragma warning disable CS8618
using System.Text.Json.Serialization;

namespace CoreBaselines;

public sealed class Twitter { public List<Status> Statuses { get; set; } public SearchMetadata SearchMetadata { get; set; } }
public sealed class SearchMetadata
{
    public double CompletedIn { get; set; } public ulong MaxId { get; set; } public string MaxIdStr { get; set; }
    public string NextResults { get; set; } public string Query { get; set; } public string RefreshUrl { get; set; }
    public uint Count { get; set; } public ulong SinceId { get; set; } public string SinceIdStr { get; set; }
}
public sealed class Status
{
    public Metadata Metadata { get; set; } public string CreatedAt { get; set; } public ulong Id { get; set; }
    public string IdStr { get; set; } public string Text { get; set; } public string Source { get; set; }
    public bool Truncated { get; set; } public ulong? InReplyToStatusId { get; set; } public ulong? InReplyToUserId { get; set; }
    public string? InReplyToScreenName { get; set; } public User User { get; set; } public uint RetweetCount { get; set; }
    public uint FavoriteCount { get; set; } public Entities Entities { get; set; } public bool Favorited { get; set; }
    public bool Retweeted { get; set; } public string Lang { get; set; } public bool? PossiblySensitive { get; set; }
    public Status? RetweetedStatus { get; set; }
}
public sealed class Metadata { public string ResultType { get; set; } public string IsoLanguageCode { get; set; } }
public sealed class User
{
    public ulong Id { get; set; } public string IdStr { get; set; } public string Name { get; set; } public string ScreenName { get; set; }
    public string Location { get; set; } public string Description { get; set; } public bool Protected { get; set; }
    public ulong FollowersCount { get; set; } public ulong FriendsCount { get; set; } public ulong ListedCount { get; set; }
    public string CreatedAt { get; set; } public ulong FavouritesCount { get; set; } public int? UtcOffset { get; set; }
    public string? TimeZone { get; set; } public bool GeoEnabled { get; set; } public bool Verified { get; set; }
    public ulong StatusesCount { get; set; } public string Lang { get; set; } public string ProfileImageUrl { get; set; }
}
public sealed class Entities { public List<Hashtag> Hashtags { get; set; } public List<Url> Urls { get; set; } public List<Mention> UserMentions { get; set; } }
public sealed class Hashtag { public string Text { get; set; } public List<uint> Indices { get; set; } }
public sealed class Url { [JsonPropertyName("url")] public string Link { get; set; } public string ExpandedUrl { get; set; } public string DisplayUrl { get; set; } public List<uint> Indices { get; set; } }
public sealed class Mention { public string ScreenName { get; set; } public string Name { get; set; } public ulong Id { get; set; } public string IdStr { get; set; } public List<uint> Indices { get; set; } }

public sealed class Canada { public string Type { get; set; } public List<Feature> Features { get; set; } }
public sealed class Feature { public string Type { get; set; } public Props Properties { get; set; } public Geometry Geometry { get; set; } }
public sealed class Props { public string Name { get; set; } }
public sealed class Geometry { public string Type { get; set; } public List<List<double[]>> Coordinates { get; set; } }

public sealed class Citm
{
    public Dictionary<string, string> AreaNames { get; set; } public Dictionary<string, string> AudienceSubCategoryNames { get; set; }
    public Dictionary<string, string> BlockNames { get; set; } public Dictionary<string, Event> Events { get; set; }
    public List<Performance> Performances { get; set; } public Dictionary<string, string> SeatCategoryNames { get; set; }
    public Dictionary<string, string> SubTopicNames { get; set; } public Dictionary<string, string> SubjectNames { get; set; }
    public Dictionary<string, string> TopicNames { get; set; } public Dictionary<string, List<ulong>> TopicSubTopics { get; set; }
    public Dictionary<string, string> VenueNames { get; set; }
}
public sealed class Event
{
    public string? Description { get; set; } public ulong Id { get; set; } public string? Logo { get; set; } public string Name { get; set; }
    public List<ulong> SubTopicIds { get; set; } public string? SubjectCode { get; set; } public string? Subtitle { get; set; }
    public List<ulong> TopicIds { get; set; }
}
public sealed class Performance
{
    public ulong EventId { get; set; } public ulong Id { get; set; } public string? Logo { get; set; } public string? Name { get; set; }
    public List<Price> Prices { get; set; } public List<SeatCategory> SeatCategories { get; set; } public string? SeatMapImage { get; set; }
    public ulong Start { get; set; } public string VenueCode { get; set; }
}
public sealed class Price { public ulong Amount { get; set; } public ulong AudienceSubCategoryId { get; set; } public ulong SeatCategoryId { get; set; } }
public sealed class SeatCategory { public List<Area> Areas { get; set; } public ulong SeatCategoryId { get; set; } }
public sealed class Area { public ulong AreaId { get; set; } public List<ulong> BlockIds { get; set; } }

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(Twitter))]
internal sealed partial class SnakeCtx : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Canada))]
[JsonSerializable(typeof(Citm))]
internal sealed partial class CamelCtx : JsonSerializerContext;
