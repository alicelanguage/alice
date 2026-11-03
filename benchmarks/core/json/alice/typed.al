# The baseline schemas (../baselines/dotnet/Typed.cs) written in Alice: `alice emit` turns them into records plus
# the compiler's __AliceJson readers/writers (C2.5), which the bench runs as alice/typed-gen. Names carry an `Al`
# prefix so they do not shadow the CoreBaselines classes in the same assembly.

record AlMetadata(result_type : String, iso_language_code : String)
record AlHashtag(text : String, indices : Array(UInt32))
record AlUrl(url : String, expanded_url : String, display_url : String, indices : Array(UInt32))
record AlMention(screen_name : String, name : String, id : UInt64, id_str : String, indices : Array(UInt32))
record AlEntities(hashtags : Array(AlHashtag), urls : Array(AlUrl), user_mentions : Array(AlMention))
record AlUser(id : UInt64, id_str : String, name : String, screen_name : String, location : String,
  description : String, protected : Bool, followers_count : UInt64, friends_count : UInt64, listed_count : UInt64,
  created_at : String, favourites_count : UInt64, utc_offset : Int32?, time_zone : String?, geo_enabled : Bool,
  verified : Bool, statuses_count : UInt64, lang : String, profile_image_url : String)
record AlStatus(metadata : AlMetadata, created_at : String, id : UInt64, id_str : String, text : String,
  source : String, truncated : Bool, in_reply_to_status_id : UInt64?, in_reply_to_user_id : UInt64?,
  in_reply_to_screen_name : String?, user : AlUser, retweet_count : UInt32, favorite_count : UInt32,
  entities : AlEntities, favorited : Bool, retweeted : Bool, lang : String, possibly_sensitive : Bool?,
  retweeted_status : AlStatus?)
record AlSearchMetadata(completed_in : Float64, max_id : UInt64, max_id_str : String, next_results : String,
  query : String, refresh_url : String, count : UInt32, since_id : UInt64, since_id_str : String)
record AlTwitter(statuses : Array(AlStatus), search_metadata : AlSearchMetadata)

record AlProps(name : String)
record AlGeometry(type : String, coordinates : Array(Array(Array(Float64))))
record AlFeature(type : String, properties : AlProps, geometry : AlGeometry)
record AlCanada(type : String, features : Array(AlFeature))

record AlEvent(description : String?, id : UInt64, logo : String?, name : String, subTopicIds : Array(UInt64),
  subjectCode : String?, subtitle : String?, topicIds : Array(UInt64))
record AlPrice(amount : UInt64, audienceSubCategoryId : UInt64, seatCategoryId : UInt64)
record AlArea(areaId : UInt64, blockIds : Array(UInt64))
record AlSeatCategory(areas : Array(AlArea), seatCategoryId : UInt64)
record AlPerformance(eventId : UInt64, id : UInt64, logo : String?, name : String?, prices : Array(AlPrice),
  seatCategories : Array(AlSeatCategory), seatMapImage : String?, start : UInt64, venueCode : String)
record AlCitm(areaNames : Hash(String, String), audienceSubCategoryNames : Hash(String, String),
  blockNames : Hash(String, String), events : Hash(String, AlEvent), performances : Array(AlPerformance),
  seatCategoryNames : Hash(String, String), subTopicNames : Hash(String, String), subjectNames : Hash(String, String),
  topicNames : Hash(String, String), topicSubTopics : Hash(String, Array(UInt64)), venueNames : Hash(String, String))

module AlJson
  def self.twitter(s : Array(UInt8)) : AlTwitter = JSON.parse(AlTwitter, s)
  def self.canada(s : Array(UInt8)) : AlCanada = JSON.parse(AlCanada, s)
  def self.citm(s : Array(UInt8)) : AlCitm = JSON.parse(AlCitm, s)
  def self.write(t : AlTwitter) : String = to_json(t)
  def self.write(c : AlCanada) : String = to_json(c)
  def self.write(c : AlCitm) : String = to_json(c)
end
