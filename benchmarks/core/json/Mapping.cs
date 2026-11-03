// Hand-written typed mapping for the baseline schemas (baselines/dotnet/Typed.cs): what the C2.5 generator will
// emit (`__json_read_T` over JsonIterator, `__json_write_T` over JsonWriter). The Utf8JsonWriter twins write the same
// tokens, so json-writer compares the two writers on identical call sequences.
using System.Text.Json;
using Alice.Core.Json;
using CoreBaselines;

namespace AliceJsonBench;

static class Read
{
    static string? Str(ref JsonIterator it) => it.TryReadNull() ? null : it.ReadString();
    static ulong? U64N(ref JsonIterator it) => it.TryReadNull() ? null : it.ReadUInt64();
    static bool? BoolN(ref JsonIterator it) => it.TryReadNull() ? null : it.ReadBoolean();
    static int? I32N(ref JsonIterator it) => it.TryReadNull() ? null : JsonMap.ReadInt32(ref it);
    static uint U32(ref JsonIterator it) => JsonMap.ReadUInt32(ref it);
    static ulong U64(ref JsonIterator it) => it.ReadUInt64();
    static string StrNN(ref JsonIterator it) => Str(ref it)!;

    public static Twitter Twitter(ref JsonIterator it)
    {
        var t = new Twitter();
        it.ReadStartObject();
        while (it.NextProperty(out var n))
        {
            if (n.SequenceEqual("statuses"u8)) t.Statuses = JsonMap.ReadList(ref it, Status);
            else if (n.SequenceEqual("search_metadata"u8)) t.SearchMetadata = SearchMetadata(ref it);
            else it.Skip();
        }
        return t;
    }

    static SearchMetadata SearchMetadata(ref JsonIterator it)
    {
        var m = new SearchMetadata();
        it.ReadStartObject();
        while (it.NextProperty(out var n))
        {
            switch (n.Length)
            {
                case 5 when n.SequenceEqual("query"u8): m.Query = StrNN(ref it); break;
                case 5 when n.SequenceEqual("count"u8): m.Count = U32(ref it); break;
                case 6 when n.SequenceEqual("max_id"u8): m.MaxId = it.ReadUInt64(); break;
                case 8 when n.SequenceEqual("since_id"u8): m.SinceId = it.ReadUInt64(); break;
                case 10 when n.SequenceEqual("max_id_str"u8): m.MaxIdStr = StrNN(ref it); break;
                case 11 when n.SequenceEqual("refresh_url"u8): m.RefreshUrl = StrNN(ref it); break;
                case 12 when n.SequenceEqual("completed_in"u8): m.CompletedIn = it.ReadDouble(); break;
                case 12 when n.SequenceEqual("next_results"u8): m.NextResults = StrNN(ref it); break;
                case 12 when n.SequenceEqual("since_id_str"u8): m.SinceIdStr = StrNN(ref it); break;
                default: it.Skip(); break;
            }
        }
        return m;
    }

    static Status Status(ref JsonIterator it)
    {
        var s = new Status();
        it.ReadStartObject();
        while (it.NextProperty(out var n))
        {
            switch (n.Length)
            {
                case 2 when n.SequenceEqual("id"u8): s.Id = it.ReadUInt64(); break;
                case 4 when n.SequenceEqual("text"u8): s.Text = StrNN(ref it); break;
                case 4 when n.SequenceEqual("user"u8): s.User = User(ref it); break;
                case 4 when n.SequenceEqual("lang"u8): s.Lang = StrNN(ref it); break;
                case 6 when n.SequenceEqual("id_str"u8): s.IdStr = StrNN(ref it); break;
                case 6 when n.SequenceEqual("source"u8): s.Source = StrNN(ref it); break;
                case 8 when n.SequenceEqual("metadata"u8): s.Metadata = Metadata(ref it); break;
                case 8 when n.SequenceEqual("entities"u8): s.Entities = Entities(ref it); break;
                case 9 when n.SequenceEqual("truncated"u8): s.Truncated = it.ReadBoolean(); break;
                case 9 when n.SequenceEqual("favorited"u8): s.Favorited = it.ReadBoolean(); break;
                case 9 when n.SequenceEqual("retweeted"u8): s.Retweeted = it.ReadBoolean(); break;
                case 10 when n.SequenceEqual("created_at"u8): s.CreatedAt = StrNN(ref it); break;
                case 13 when n.SequenceEqual("retweet_count"u8): s.RetweetCount = U32(ref it); break;
                case 14 when n.SequenceEqual("favorite_count"u8): s.FavoriteCount = U32(ref it); break;
                case 16 when n.SequenceEqual("retweeted_status"u8): s.RetweetedStatus = it.TryReadNull() ? null : Status(ref it); break;
                case 18 when n.SequenceEqual("possibly_sensitive"u8): s.PossiblySensitive = BoolN(ref it); break;
                case 19 when n.SequenceEqual("in_reply_to_user_id"u8): s.InReplyToUserId = U64N(ref it); break;
                case 21 when n.SequenceEqual("in_reply_to_status_id"u8): s.InReplyToStatusId = U64N(ref it); break;
                case 23 when n.SequenceEqual("in_reply_to_screen_name"u8): s.InReplyToScreenName = Str(ref it); break;
                default: it.Skip(); break;
            }
        }
        return s;
    }

    static Metadata Metadata(ref JsonIterator it)
    {
        var m = new Metadata();
        it.ReadStartObject();
        while (it.NextProperty(out var n))
        {
            if (n.SequenceEqual("result_type"u8)) m.ResultType = StrNN(ref it);
            else if (n.SequenceEqual("iso_language_code"u8)) m.IsoLanguageCode = StrNN(ref it);
            else it.Skip();
        }
        return m;
    }

    static User User(ref JsonIterator it)
    {
        var u = new User();
        it.ReadStartObject();
        while (it.NextProperty(out var n))
        {
            switch (n.Length)
            {
                case 2 when n.SequenceEqual("id"u8): u.Id = it.ReadUInt64(); break;
                case 4 when n.SequenceEqual("name"u8): u.Name = StrNN(ref it); break;
                case 4 when n.SequenceEqual("lang"u8): u.Lang = StrNN(ref it); break;
                case 6 when n.SequenceEqual("id_str"u8): u.IdStr = StrNN(ref it); break;
                case 8 when n.SequenceEqual("location"u8): u.Location = StrNN(ref it); break;
                case 8 when n.SequenceEqual("verified"u8): u.Verified = it.ReadBoolean(); break;
                case 9 when n.SequenceEqual("protected"u8): u.Protected = it.ReadBoolean(); break;
                case 9 when n.SequenceEqual("time_zone"u8): u.TimeZone = Str(ref it); break;
                case 10 when n.SequenceEqual("created_at"u8): u.CreatedAt = StrNN(ref it); break;
                case 10 when n.SequenceEqual("utc_offset"u8): u.UtcOffset = I32N(ref it); break;
                case 11 when n.SequenceEqual("screen_name"u8): u.ScreenName = StrNN(ref it); break;
                case 11 when n.SequenceEqual("description"u8): u.Description = StrNN(ref it); break;
                case 11 when n.SequenceEqual("geo_enabled"u8): u.GeoEnabled = it.ReadBoolean(); break;
                case 13 when n.SequenceEqual("friends_count"u8): u.FriendsCount = it.ReadUInt64(); break;
                case 12 when n.SequenceEqual("listed_count"u8): u.ListedCount = it.ReadUInt64(); break;
                case 15 when n.SequenceEqual("followers_count"u8): u.FollowersCount = it.ReadUInt64(); break;
                case 14 when n.SequenceEqual("statuses_count"u8): u.StatusesCount = it.ReadUInt64(); break;
                case 16 when n.SequenceEqual("favourites_count"u8): u.FavouritesCount = it.ReadUInt64(); break;
                case 17 when n.SequenceEqual("profile_image_url"u8): u.ProfileImageUrl = StrNN(ref it); break;
                default: it.Skip(); break;
            }
        }
        return u;
    }

    static Entities Entities(ref JsonIterator it)
    {
        var e = new Entities();
        it.ReadStartObject();
        while (it.NextProperty(out var n))
        {
            if (n.SequenceEqual("hashtags"u8)) e.Hashtags = JsonMap.ReadList(ref it, Hashtag);
            else if (n.SequenceEqual("urls"u8)) e.Urls = JsonMap.ReadList(ref it, Url);
            else if (n.SequenceEqual("user_mentions"u8)) e.UserMentions = JsonMap.ReadList(ref it, Mention);
            else it.Skip();
        }
        return e;
    }

    static Hashtag Hashtag(ref JsonIterator it)
    {
        var h = new Hashtag();
        it.ReadStartObject();
        while (it.NextProperty(out var n))
        {
            if (n.SequenceEqual("text"u8)) h.Text = StrNN(ref it);
            else if (n.SequenceEqual("indices"u8)) h.Indices = JsonMap.ReadList(ref it, U32);
            else it.Skip();
        }
        return h;
    }

    static Url Url(ref JsonIterator it)
    {
        var u = new Url();
        it.ReadStartObject();
        while (it.NextProperty(out var n))
        {
            if (n.SequenceEqual("url"u8)) u.Link = StrNN(ref it);
            else if (n.SequenceEqual("expanded_url"u8)) u.ExpandedUrl = StrNN(ref it);
            else if (n.SequenceEqual("display_url"u8)) u.DisplayUrl = StrNN(ref it);
            else if (n.SequenceEqual("indices"u8)) u.Indices = JsonMap.ReadList(ref it, U32);
            else it.Skip();
        }
        return u;
    }

    static Mention Mention(ref JsonIterator it)
    {
        var m = new Mention();
        it.ReadStartObject();
        while (it.NextProperty(out var n))
        {
            if (n.SequenceEqual("screen_name"u8)) m.ScreenName = StrNN(ref it);
            else if (n.SequenceEqual("name"u8)) m.Name = StrNN(ref it);
            else if (n.SequenceEqual("id"u8)) m.Id = it.ReadUInt64();
            else if (n.SequenceEqual("id_str"u8)) m.IdStr = StrNN(ref it);
            else if (n.SequenceEqual("indices"u8)) m.Indices = JsonMap.ReadList(ref it, U32);
            else it.Skip();
        }
        return m;
    }

    public static Canada Canada(ref JsonIterator it)
    {
        var c = new Canada();
        it.ReadStartObject();
        while (it.NextProperty(out var n))
        {
            if (n.SequenceEqual("type"u8)) c.Type = StrNN(ref it);
            else if (n.SequenceEqual("features"u8)) c.Features = JsonMap.ReadList(ref it, Feature);
            else it.Skip();
        }
        return c;
    }

    static Feature Feature(ref JsonIterator it)
    {
        var f = new Feature();
        it.ReadStartObject();
        while (it.NextProperty(out var n))
        {
            if (n.SequenceEqual("type"u8)) f.Type = StrNN(ref it);
            else if (n.SequenceEqual("properties"u8))
            {
                var p = new Props();
                it.ReadStartObject();
                while (it.NextProperty(out var k)) { if (k.SequenceEqual("name"u8)) p.Name = StrNN(ref it); else it.Skip(); }
                f.Properties = p;
            }
            else if (n.SequenceEqual("geometry"u8))
            {
                var g = new Geometry();
                it.ReadStartObject();
                while (it.NextProperty(out var k))
                {
                    if (k.SequenceEqual("type"u8)) g.Type = StrNN(ref it);
                    else if (k.SequenceEqual("coordinates"u8)) g.Coordinates = JsonMap.ReadList(ref it, Ring);
                    else it.Skip();
                }
                f.Geometry = g;
            }
            else it.Skip();
        }
        return f;
    }

    static List<double[]> Ring(ref JsonIterator it) => JsonMap.ReadList(ref it, Pair);

    static double[] Pair(ref JsonIterator it) => JsonMap.ReadArray(ref it, JsonMap.ReadDouble);

    public static Citm Citm(ref JsonIterator it)
    {
        var c = new Citm();
        it.ReadStartObject();
        while (it.NextProperty(out var n))
        {
            if (n.SequenceEqual("areaNames"u8)) c.AreaNames = JsonMap.ReadHash(ref it, StrNN);
            else if (n.SequenceEqual("audienceSubCategoryNames"u8)) c.AudienceSubCategoryNames = JsonMap.ReadHash(ref it, StrNN);
            else if (n.SequenceEqual("blockNames"u8)) c.BlockNames = JsonMap.ReadHash(ref it, StrNN);
            else if (n.SequenceEqual("events"u8)) c.Events = JsonMap.ReadHash(ref it, Event);
            else if (n.SequenceEqual("performances"u8)) c.Performances = JsonMap.ReadList(ref it, Performance);
            else if (n.SequenceEqual("seatCategoryNames"u8)) c.SeatCategoryNames = JsonMap.ReadHash(ref it, StrNN);
            else if (n.SequenceEqual("subTopicNames"u8)) c.SubTopicNames = JsonMap.ReadHash(ref it, StrNN);
            else if (n.SequenceEqual("subjectNames"u8)) c.SubjectNames = JsonMap.ReadHash(ref it, StrNN);
            else if (n.SequenceEqual("topicNames"u8)) c.TopicNames = JsonMap.ReadHash(ref it, StrNN);
            else if (n.SequenceEqual("topicSubTopics"u8)) c.TopicSubTopics = JsonMap.ReadHash(ref it, U64List);
            else if (n.SequenceEqual("venueNames"u8)) c.VenueNames = JsonMap.ReadHash(ref it, StrNN);
            else it.Skip();
        }
        return c;
    }

    static List<ulong> U64List(ref JsonIterator it) => JsonMap.ReadList(ref it, U64);

    static Event Event(ref JsonIterator it)
    {
        var e = new Event();
        it.ReadStartObject();
        while (it.NextProperty(out var n))
        {
            if (n.SequenceEqual("description"u8)) e.Description = Str(ref it);
            else if (n.SequenceEqual("id"u8)) e.Id = it.ReadUInt64();
            else if (n.SequenceEqual("logo"u8)) e.Logo = Str(ref it);
            else if (n.SequenceEqual("name"u8)) e.Name = StrNN(ref it);
            else if (n.SequenceEqual("subTopicIds"u8)) e.SubTopicIds = U64List(ref it);
            else if (n.SequenceEqual("subjectCode"u8)) e.SubjectCode = Str(ref it);
            else if (n.SequenceEqual("subtitle"u8)) e.Subtitle = Str(ref it);
            else if (n.SequenceEqual("topicIds"u8)) e.TopicIds = U64List(ref it);
            else it.Skip();
        }
        return e;
    }

    static Performance Performance(ref JsonIterator it)
    {
        var p = new Performance();
        it.ReadStartObject();
        while (it.NextProperty(out var n))
        {
            if (n.SequenceEqual("eventId"u8)) p.EventId = it.ReadUInt64();
            else if (n.SequenceEqual("id"u8)) p.Id = it.ReadUInt64();
            else if (n.SequenceEqual("logo"u8)) p.Logo = Str(ref it);
            else if (n.SequenceEqual("name"u8)) p.Name = Str(ref it);
            else if (n.SequenceEqual("prices"u8)) p.Prices = JsonMap.ReadList(ref it, Price);
            else if (n.SequenceEqual("seatCategories"u8)) p.SeatCategories = JsonMap.ReadList(ref it, SeatCategory);
            else if (n.SequenceEqual("seatMapImage"u8)) p.SeatMapImage = Str(ref it);
            else if (n.SequenceEqual("start"u8)) p.Start = it.ReadUInt64();
            else if (n.SequenceEqual("venueCode"u8)) p.VenueCode = StrNN(ref it);
            else it.Skip();
        }
        return p;
    }

    static Price Price(ref JsonIterator it)
    {
        var p = new Price();
        it.ReadStartObject();
        while (it.NextProperty(out var n))
        {
            if (n.SequenceEqual("amount"u8)) p.Amount = it.ReadUInt64();
            else if (n.SequenceEqual("audienceSubCategoryId"u8)) p.AudienceSubCategoryId = it.ReadUInt64();
            else if (n.SequenceEqual("seatCategoryId"u8)) p.SeatCategoryId = it.ReadUInt64();
            else it.Skip();
        }
        return p;
    }

    static SeatCategory SeatCategory(ref JsonIterator it)
    {
        var s = new SeatCategory();
        it.ReadStartObject();
        while (it.NextProperty(out var n))
        {
            if (n.SequenceEqual("areas"u8)) s.Areas = JsonMap.ReadList(ref it, Area);
            else if (n.SequenceEqual("seatCategoryId"u8)) s.SeatCategoryId = it.ReadUInt64();
            else it.Skip();
        }
        return s;
    }

    static Area Area(ref JsonIterator it)
    {
        var a = new Area();
        it.ReadStartObject();
        while (it.NextProperty(out var n))
        {
            if (n.SequenceEqual("areaId"u8)) a.AreaId = it.ReadUInt64();
            else if (n.SequenceEqual("blockIds"u8)) a.BlockIds = U64List(ref it);
            else it.Skip();
        }
        return a;
    }
}

/// <summary>Writers over Alice's JsonWriter.</summary>
static class WriteA
{
    static void S(ref JsonWriter w, ReadOnlySpan<byte> name, string? v) { w.WritePropertyName(name); if (v is null) w.WriteNull(); else w.WriteString(v.AsSpan()); }
    static void U(ref JsonWriter w, ReadOnlySpan<byte> name, ulong v) { w.WritePropertyName(name); w.WriteNumber(v); }
    static void UN(ref JsonWriter w, ReadOnlySpan<byte> name, ulong? v) { w.WritePropertyName(name); if (v is { } x) w.WriteNumber(x); else w.WriteNull(); }
    static void B(ref JsonWriter w, ReadOnlySpan<byte> name, bool v) { w.WritePropertyName(name); w.WriteBoolean(v); }
    static void Indices(ref JsonWriter w, List<uint> l) { w.WritePropertyName("indices"u8); w.WriteStartArray(); foreach (var i in l) w.WriteNumber((ulong)i); w.WriteEndArray(); }
    static void U64s(ref JsonWriter w, ReadOnlySpan<byte> name, List<ulong> l) { w.WritePropertyName(name); w.WriteStartArray(); foreach (var i in l) w.WriteNumber(i); w.WriteEndArray(); }

    public static void Twitter(ref JsonWriter w, Twitter t)
    {
        w.WriteStartObject();
        w.WritePropertyName("statuses"u8);
        w.WriteStartArray();
        foreach (var s in t.Statuses) Status(ref w, s);
        w.WriteEndArray();
        var m = t.SearchMetadata;
        w.WritePropertyName("search_metadata"u8);
        w.WriteStartObject();
        w.WritePropertyName("completed_in"u8); w.WriteNumber(m.CompletedIn);
        U(ref w, "max_id"u8, m.MaxId); S(ref w, "max_id_str"u8, m.MaxIdStr); S(ref w, "next_results"u8, m.NextResults);
        S(ref w, "query"u8, m.Query); S(ref w, "refresh_url"u8, m.RefreshUrl); U(ref w, "count"u8, m.Count);
        U(ref w, "since_id"u8, m.SinceId); S(ref w, "since_id_str"u8, m.SinceIdStr);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    static void Status(ref JsonWriter w, Status s)
    {
        w.WriteStartObject();
        w.WritePropertyName("metadata"u8);
        w.WriteStartObject(); S(ref w, "result_type"u8, s.Metadata.ResultType); S(ref w, "iso_language_code"u8, s.Metadata.IsoLanguageCode); w.WriteEndObject();
        S(ref w, "created_at"u8, s.CreatedAt); U(ref w, "id"u8, s.Id); S(ref w, "id_str"u8, s.IdStr); S(ref w, "text"u8, s.Text);
        S(ref w, "source"u8, s.Source); B(ref w, "truncated"u8, s.Truncated); UN(ref w, "in_reply_to_status_id"u8, s.InReplyToStatusId);
        UN(ref w, "in_reply_to_user_id"u8, s.InReplyToUserId); S(ref w, "in_reply_to_screen_name"u8, s.InReplyToScreenName);
        w.WritePropertyName("user"u8); User(ref w, s.User);
        U(ref w, "retweet_count"u8, s.RetweetCount); U(ref w, "favorite_count"u8, s.FavoriteCount);
        w.WritePropertyName("entities"u8);
        w.WriteStartObject();
        w.WritePropertyName("hashtags"u8); w.WriteStartArray();
        foreach (var h in s.Entities.Hashtags) { w.WriteStartObject(); S(ref w, "text"u8, h.Text); Indices(ref w, h.Indices); w.WriteEndObject(); }
        w.WriteEndArray();
        w.WritePropertyName("urls"u8); w.WriteStartArray();
        foreach (var u in s.Entities.Urls) { w.WriteStartObject(); S(ref w, "url"u8, u.Link); S(ref w, "expanded_url"u8, u.ExpandedUrl); S(ref w, "display_url"u8, u.DisplayUrl); Indices(ref w, u.Indices); w.WriteEndObject(); }
        w.WriteEndArray();
        w.WritePropertyName("user_mentions"u8); w.WriteStartArray();
        foreach (var m in s.Entities.UserMentions) { w.WriteStartObject(); S(ref w, "screen_name"u8, m.ScreenName); S(ref w, "name"u8, m.Name); U(ref w, "id"u8, m.Id); S(ref w, "id_str"u8, m.IdStr); Indices(ref w, m.Indices); w.WriteEndObject(); }
        w.WriteEndArray();
        w.WriteEndObject();
        B(ref w, "favorited"u8, s.Favorited); B(ref w, "retweeted"u8, s.Retweeted); S(ref w, "lang"u8, s.Lang);
        w.WritePropertyName("possibly_sensitive"u8); if (s.PossiblySensitive is { } ps) w.WriteBoolean(ps); else w.WriteNull();
        w.WritePropertyName("retweeted_status"u8); if (s.RetweetedStatus is { } rs) Status(ref w, rs); else w.WriteNull();
        w.WriteEndObject();
    }

    static void User(ref JsonWriter w, User u)
    {
        w.WriteStartObject();
        U(ref w, "id"u8, u.Id); S(ref w, "id_str"u8, u.IdStr); S(ref w, "name"u8, u.Name); S(ref w, "screen_name"u8, u.ScreenName);
        S(ref w, "location"u8, u.Location); S(ref w, "description"u8, u.Description); B(ref w, "protected"u8, u.Protected);
        U(ref w, "followers_count"u8, u.FollowersCount); U(ref w, "friends_count"u8, u.FriendsCount); U(ref w, "listed_count"u8, u.ListedCount);
        S(ref w, "created_at"u8, u.CreatedAt); U(ref w, "favourites_count"u8, u.FavouritesCount);
        w.WritePropertyName("utc_offset"u8); if (u.UtcOffset is { } o) w.WriteNumber(o); else w.WriteNull();
        S(ref w, "time_zone"u8, u.TimeZone); B(ref w, "geo_enabled"u8, u.GeoEnabled); B(ref w, "verified"u8, u.Verified);
        U(ref w, "statuses_count"u8, u.StatusesCount); S(ref w, "lang"u8, u.Lang); S(ref w, "profile_image_url"u8, u.ProfileImageUrl);
        w.WriteEndObject();
    }

    public static void Canada(ref JsonWriter w, Canada c)
    {
        w.WriteStartObject();
        S(ref w, "type"u8, c.Type);
        w.WritePropertyName("features"u8);
        w.WriteStartArray();
        foreach (var f in c.Features)
        {
            w.WriteStartObject();
            S(ref w, "type"u8, f.Type);
            w.WritePropertyName("properties"u8); w.WriteStartObject(); S(ref w, "name"u8, f.Properties.Name); w.WriteEndObject();
            w.WritePropertyName("geometry"u8);
            w.WriteStartObject();
            S(ref w, "type"u8, f.Geometry.Type);
            w.WritePropertyName("coordinates"u8);
            w.WriteStartArray();
            foreach (var ring in f.Geometry.Coordinates)
            {
                w.WriteStartArray();
                foreach (var p in ring) { w.WriteStartArray(); foreach (var d in p) w.WriteNumber(d); w.WriteEndArray(); }
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static void Names(ref JsonWriter w, ReadOnlySpan<byte> name, Dictionary<string, string> d)
    {
        w.WritePropertyName(name);
        w.WriteStartObject();
        foreach (var kv in d) { w.WritePropertyName(kv.Key.AsSpan()); w.WriteString(kv.Value.AsSpan()); }
        w.WriteEndObject();
    }

    public static void Citm(ref JsonWriter w, Citm c)
    {
        w.WriteStartObject();
        Names(ref w, "areaNames"u8, c.AreaNames); Names(ref w, "audienceSubCategoryNames"u8, c.AudienceSubCategoryNames);
        Names(ref w, "blockNames"u8, c.BlockNames);
        w.WritePropertyName("events"u8);
        w.WriteStartObject();
        foreach (var kv in c.Events)
        {
            var e = kv.Value;
            w.WritePropertyName(kv.Key.AsSpan());
            w.WriteStartObject();
            S(ref w, "description"u8, e.Description); U(ref w, "id"u8, e.Id); S(ref w, "logo"u8, e.Logo); S(ref w, "name"u8, e.Name);
            U64s(ref w, "subTopicIds"u8, e.SubTopicIds); S(ref w, "subjectCode"u8, e.SubjectCode); S(ref w, "subtitle"u8, e.Subtitle);
            U64s(ref w, "topicIds"u8, e.TopicIds);
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WritePropertyName("performances"u8);
        w.WriteStartArray();
        foreach (var p in c.Performances)
        {
            w.WriteStartObject();
            U(ref w, "eventId"u8, p.EventId); U(ref w, "id"u8, p.Id); S(ref w, "logo"u8, p.Logo); S(ref w, "name"u8, p.Name);
            w.WritePropertyName("prices"u8); w.WriteStartArray();
            foreach (var pr in p.Prices) { w.WriteStartObject(); U(ref w, "amount"u8, pr.Amount); U(ref w, "audienceSubCategoryId"u8, pr.AudienceSubCategoryId); U(ref w, "seatCategoryId"u8, pr.SeatCategoryId); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WritePropertyName("seatCategories"u8); w.WriteStartArray();
            foreach (var sc in p.SeatCategories)
            {
                w.WriteStartObject();
                w.WritePropertyName("areas"u8); w.WriteStartArray();
                foreach (var a in sc.Areas) { w.WriteStartObject(); U(ref w, "areaId"u8, a.AreaId); U64s(ref w, "blockIds"u8, a.BlockIds); w.WriteEndObject(); }
                w.WriteEndArray();
                U(ref w, "seatCategoryId"u8, sc.SeatCategoryId);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            S(ref w, "seatMapImage"u8, p.SeatMapImage); U(ref w, "start"u8, p.Start); S(ref w, "venueCode"u8, p.VenueCode);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        Names(ref w, "seatCategoryNames"u8, c.SeatCategoryNames); Names(ref w, "subTopicNames"u8, c.SubTopicNames);
        Names(ref w, "subjectNames"u8, c.SubjectNames); Names(ref w, "topicNames"u8, c.TopicNames);
        w.WritePropertyName("topicSubTopics"u8);
        w.WriteStartObject();
        foreach (var kv in c.TopicSubTopics) { w.WritePropertyName(kv.Key.AsSpan()); w.WriteStartArray(); foreach (var i in kv.Value) w.WriteNumber(i); w.WriteEndArray(); }
        w.WriteEndObject();
        Names(ref w, "venueNames"u8, c.VenueNames);
        w.WriteEndObject();
    }
}

/// <summary>The same writers over System.Text.Json's Utf8JsonWriter (identical call sequence).</summary>
static class WriteS
{
    static void S(Utf8JsonWriter w, ReadOnlySpan<byte> name, string? v) { w.WritePropertyName(name); if (v is null) w.WriteNullValue(); else w.WriteStringValue(v); }
    static void U(Utf8JsonWriter w, ReadOnlySpan<byte> name, ulong v) { w.WritePropertyName(name); w.WriteNumberValue(v); }
    static void UN(Utf8JsonWriter w, ReadOnlySpan<byte> name, ulong? v) { w.WritePropertyName(name); if (v is { } x) w.WriteNumberValue(x); else w.WriteNullValue(); }
    static void B(Utf8JsonWriter w, ReadOnlySpan<byte> name, bool v) { w.WritePropertyName(name); w.WriteBooleanValue(v); }
    static void Indices(Utf8JsonWriter w, List<uint> l) { w.WritePropertyName("indices"u8); w.WriteStartArray(); foreach (var i in l) w.WriteNumberValue((ulong)i); w.WriteEndArray(); }
    static void U64s(Utf8JsonWriter w, ReadOnlySpan<byte> name, List<ulong> l) { w.WritePropertyName(name); w.WriteStartArray(); foreach (var i in l) w.WriteNumberValue(i); w.WriteEndArray(); }

    public static void Twitter(Utf8JsonWriter w, Twitter t)
    {
        w.WriteStartObject();
        w.WritePropertyName("statuses"u8);
        w.WriteStartArray();
        foreach (var s in t.Statuses) Status(w, s);
        w.WriteEndArray();
        var m = t.SearchMetadata;
        w.WritePropertyName("search_metadata"u8);
        w.WriteStartObject();
        w.WritePropertyName("completed_in"u8); w.WriteNumberValue(m.CompletedIn);
        U(w, "max_id"u8, m.MaxId); S(w, "max_id_str"u8, m.MaxIdStr); S(w, "next_results"u8, m.NextResults);
        S(w, "query"u8, m.Query); S(w, "refresh_url"u8, m.RefreshUrl); U(w, "count"u8, m.Count);
        U(w, "since_id"u8, m.SinceId); S(w, "since_id_str"u8, m.SinceIdStr);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    static void Status(Utf8JsonWriter w, Status s)
    {
        w.WriteStartObject();
        w.WritePropertyName("metadata"u8);
        w.WriteStartObject(); S(w, "result_type"u8, s.Metadata.ResultType); S(w, "iso_language_code"u8, s.Metadata.IsoLanguageCode); w.WriteEndObject();
        S(w, "created_at"u8, s.CreatedAt); U(w, "id"u8, s.Id); S(w, "id_str"u8, s.IdStr); S(w, "text"u8, s.Text);
        S(w, "source"u8, s.Source); B(w, "truncated"u8, s.Truncated); UN(w, "in_reply_to_status_id"u8, s.InReplyToStatusId);
        UN(w, "in_reply_to_user_id"u8, s.InReplyToUserId); S(w, "in_reply_to_screen_name"u8, s.InReplyToScreenName);
        w.WritePropertyName("user"u8); User(w, s.User);
        U(w, "retweet_count"u8, s.RetweetCount); U(w, "favorite_count"u8, s.FavoriteCount);
        w.WritePropertyName("entities"u8);
        w.WriteStartObject();
        w.WritePropertyName("hashtags"u8); w.WriteStartArray();
        foreach (var h in s.Entities.Hashtags) { w.WriteStartObject(); S(w, "text"u8, h.Text); Indices(w, h.Indices); w.WriteEndObject(); }
        w.WriteEndArray();
        w.WritePropertyName("urls"u8); w.WriteStartArray();
        foreach (var u in s.Entities.Urls) { w.WriteStartObject(); S(w, "url"u8, u.Link); S(w, "expanded_url"u8, u.ExpandedUrl); S(w, "display_url"u8, u.DisplayUrl); Indices(w, u.Indices); w.WriteEndObject(); }
        w.WriteEndArray();
        w.WritePropertyName("user_mentions"u8); w.WriteStartArray();
        foreach (var m in s.Entities.UserMentions) { w.WriteStartObject(); S(w, "screen_name"u8, m.ScreenName); S(w, "name"u8, m.Name); U(w, "id"u8, m.Id); S(w, "id_str"u8, m.IdStr); Indices(w, m.Indices); w.WriteEndObject(); }
        w.WriteEndArray();
        w.WriteEndObject();
        B(w, "favorited"u8, s.Favorited); B(w, "retweeted"u8, s.Retweeted); S(w, "lang"u8, s.Lang);
        w.WritePropertyName("possibly_sensitive"u8); if (s.PossiblySensitive is { } ps) w.WriteBooleanValue(ps); else w.WriteNullValue();
        w.WritePropertyName("retweeted_status"u8); if (s.RetweetedStatus is { } rs) Status(w, rs); else w.WriteNullValue();
        w.WriteEndObject();
    }

    static void User(Utf8JsonWriter w, User u)
    {
        w.WriteStartObject();
        U(w, "id"u8, u.Id); S(w, "id_str"u8, u.IdStr); S(w, "name"u8, u.Name); S(w, "screen_name"u8, u.ScreenName);
        S(w, "location"u8, u.Location); S(w, "description"u8, u.Description); B(w, "protected"u8, u.Protected);
        U(w, "followers_count"u8, u.FollowersCount); U(w, "friends_count"u8, u.FriendsCount); U(w, "listed_count"u8, u.ListedCount);
        S(w, "created_at"u8, u.CreatedAt); U(w, "favourites_count"u8, u.FavouritesCount);
        w.WritePropertyName("utc_offset"u8); if (u.UtcOffset is { } o) w.WriteNumberValue(o); else w.WriteNullValue();
        S(w, "time_zone"u8, u.TimeZone); B(w, "geo_enabled"u8, u.GeoEnabled); B(w, "verified"u8, u.Verified);
        U(w, "statuses_count"u8, u.StatusesCount); S(w, "lang"u8, u.Lang); S(w, "profile_image_url"u8, u.ProfileImageUrl);
        w.WriteEndObject();
    }

    public static void Canada(Utf8JsonWriter w, Canada c)
    {
        w.WriteStartObject();
        S(w, "type"u8, c.Type);
        w.WritePropertyName("features"u8);
        w.WriteStartArray();
        foreach (var f in c.Features)
        {
            w.WriteStartObject();
            S(w, "type"u8, f.Type);
            w.WritePropertyName("properties"u8); w.WriteStartObject(); S(w, "name"u8, f.Properties.Name); w.WriteEndObject();
            w.WritePropertyName("geometry"u8);
            w.WriteStartObject();
            S(w, "type"u8, f.Geometry.Type);
            w.WritePropertyName("coordinates"u8);
            w.WriteStartArray();
            foreach (var ring in f.Geometry.Coordinates)
            {
                w.WriteStartArray();
                foreach (var p in ring) { w.WriteStartArray(); foreach (var d in p) w.WriteNumberValue(d); w.WriteEndArray(); }
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static void Names(Utf8JsonWriter w, ReadOnlySpan<byte> name, Dictionary<string, string> d)
    {
        w.WritePropertyName(name);
        w.WriteStartObject();
        foreach (var kv in d) { w.WritePropertyName(kv.Key); w.WriteStringValue(kv.Value); }
        w.WriteEndObject();
    }

    public static void Citm(Utf8JsonWriter w, Citm c)
    {
        w.WriteStartObject();
        Names(w, "areaNames"u8, c.AreaNames); Names(w, "audienceSubCategoryNames"u8, c.AudienceSubCategoryNames);
        Names(w, "blockNames"u8, c.BlockNames);
        w.WritePropertyName("events"u8);
        w.WriteStartObject();
        foreach (var kv in c.Events)
        {
            var e = kv.Value;
            w.WritePropertyName(kv.Key);
            w.WriteStartObject();
            S(w, "description"u8, e.Description); U(w, "id"u8, e.Id); S(w, "logo"u8, e.Logo); S(w, "name"u8, e.Name);
            U64s(w, "subTopicIds"u8, e.SubTopicIds); S(w, "subjectCode"u8, e.SubjectCode); S(w, "subtitle"u8, e.Subtitle);
            U64s(w, "topicIds"u8, e.TopicIds);
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WritePropertyName("performances"u8);
        w.WriteStartArray();
        foreach (var p in c.Performances)
        {
            w.WriteStartObject();
            U(w, "eventId"u8, p.EventId); U(w, "id"u8, p.Id); S(w, "logo"u8, p.Logo); S(w, "name"u8, p.Name);
            w.WritePropertyName("prices"u8); w.WriteStartArray();
            foreach (var pr in p.Prices) { w.WriteStartObject(); U(w, "amount"u8, pr.Amount); U(w, "audienceSubCategoryId"u8, pr.AudienceSubCategoryId); U(w, "seatCategoryId"u8, pr.SeatCategoryId); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WritePropertyName("seatCategories"u8); w.WriteStartArray();
            foreach (var sc in p.SeatCategories)
            {
                w.WriteStartObject();
                w.WritePropertyName("areas"u8); w.WriteStartArray();
                foreach (var a in sc.Areas) { w.WriteStartObject(); U(w, "areaId"u8, a.AreaId); U64s(w, "blockIds"u8, a.BlockIds); w.WriteEndObject(); }
                w.WriteEndArray();
                U(w, "seatCategoryId"u8, sc.SeatCategoryId);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            S(w, "seatMapImage"u8, p.SeatMapImage); U(w, "start"u8, p.Start); S(w, "venueCode"u8, p.VenueCode);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        Names(w, "seatCategoryNames"u8, c.SeatCategoryNames); Names(w, "subTopicNames"u8, c.SubTopicNames);
        Names(w, "subjectNames"u8, c.SubjectNames); Names(w, "topicNames"u8, c.TopicNames);
        w.WritePropertyName("topicSubTopics"u8);
        w.WriteStartObject();
        foreach (var kv in c.TopicSubTopics) { w.WritePropertyName(kv.Key); w.WriteStartArray(); foreach (var i in kv.Value) w.WriteNumberValue(i); w.WriteEndArray(); }
        w.WriteEndObject();
        Names(w, "venueNames"u8, c.VenueNames);
        w.WriteEndObject();
    }
}
