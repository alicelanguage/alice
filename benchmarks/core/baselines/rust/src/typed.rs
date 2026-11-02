//! Typed schemas shared field for field with baselines/dotnet/Typed.cs. Partial schemas: fields not listed are
//! skipped by both serializers, so the comparison is like for like.
use serde::Deserialize;
use std::collections::HashMap;

#[derive(Deserialize)]
pub struct Twitter {
    pub statuses: Vec<Status>,
    pub search_metadata: SearchMetadata,
}
#[derive(Deserialize)]
pub struct SearchMetadata {
    pub completed_in: f64,
    pub max_id: u64,
    pub max_id_str: String,
    pub next_results: String,
    pub query: String,
    pub refresh_url: String,
    pub count: u32,
    pub since_id: u64,
    pub since_id_str: String,
}
#[derive(Deserialize)]
pub struct Status {
    pub metadata: Metadata,
    pub created_at: String,
    pub id: u64,
    pub id_str: String,
    pub text: String,
    pub source: String,
    pub truncated: bool,
    pub in_reply_to_status_id: Option<u64>,
    pub in_reply_to_user_id: Option<u64>,
    pub in_reply_to_screen_name: Option<String>,
    pub user: User,
    pub retweet_count: u32,
    pub favorite_count: u32,
    pub entities: Entities,
    pub favorited: bool,
    pub retweeted: bool,
    pub lang: String,
    pub possibly_sensitive: Option<bool>,
    pub retweeted_status: Option<Box<Status>>,
}
#[derive(Deserialize)]
pub struct Metadata {
    pub result_type: String,
    pub iso_language_code: String,
}
#[derive(Deserialize)]
pub struct User {
    pub id: u64,
    pub id_str: String,
    pub name: String,
    pub screen_name: String,
    pub location: String,
    pub description: String,
    pub protected: bool,
    pub followers_count: u64,
    pub friends_count: u64,
    pub listed_count: u64,
    pub created_at: String,
    pub favourites_count: u64,
    pub utc_offset: Option<i32>,
    pub time_zone: Option<String>,
    pub geo_enabled: bool,
    pub verified: bool,
    pub statuses_count: u64,
    pub lang: String,
    pub profile_image_url: String,
}
#[derive(Deserialize)]
pub struct Entities {
    pub hashtags: Vec<Hashtag>,
    pub urls: Vec<Url>,
    pub user_mentions: Vec<Mention>,
}
#[derive(Deserialize)]
pub struct Hashtag {
    pub text: String,
    pub indices: Vec<u32>,
}
#[derive(Deserialize)]
pub struct Url {
    pub url: String,
    pub expanded_url: String,
    pub display_url: String,
    pub indices: Vec<u32>,
}
#[derive(Deserialize)]
pub struct Mention {
    pub screen_name: String,
    pub name: String,
    pub id: u64,
    pub id_str: String,
    pub indices: Vec<u32>,
}

#[derive(Deserialize)]
pub struct Canada {
    #[serde(rename = "type")]
    pub kind: String,
    pub features: Vec<Feature>,
}
#[derive(Deserialize)]
pub struct Feature {
    #[serde(rename = "type")]
    pub kind: String,
    pub properties: Props,
    pub geometry: Geometry,
}
#[derive(Deserialize)]
pub struct Props {
    pub name: String,
}
#[derive(Deserialize)]
pub struct Geometry {
    #[serde(rename = "type")]
    pub kind: String,
    pub coordinates: Vec<Vec<[f64; 2]>>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Citm {
    pub area_names: HashMap<String, String>,
    pub audience_sub_category_names: HashMap<String, String>,
    pub block_names: HashMap<String, String>,
    pub events: HashMap<String, Event>,
    pub performances: Vec<Performance>,
    pub seat_category_names: HashMap<String, String>,
    pub sub_topic_names: HashMap<String, String>,
    pub subject_names: HashMap<String, String>,
    pub topic_names: HashMap<String, String>,
    pub topic_sub_topics: HashMap<String, Vec<u64>>,
    pub venue_names: HashMap<String, String>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Event {
    pub description: Option<String>,
    pub id: u64,
    pub logo: Option<String>,
    pub name: String,
    pub sub_topic_ids: Vec<u64>,
    pub subject_code: Option<String>,
    pub subtitle: Option<String>,
    pub topic_ids: Vec<u64>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Performance {
    pub event_id: u64,
    pub id: u64,
    pub logo: Option<String>,
    pub name: Option<String>,
    pub prices: Vec<Price>,
    pub seat_categories: Vec<SeatCategory>,
    pub seat_map_image: Option<String>,
    pub start: u64,
    pub venue_code: String,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Price {
    pub amount: u64,
    pub audience_sub_category_id: u64,
    pub seat_category_id: u64,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SeatCategory {
    pub areas: Vec<Area>,
    pub seat_category_id: u64,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Area {
    pub area_id: u64,
    pub block_ids: Vec<u64>,
}
