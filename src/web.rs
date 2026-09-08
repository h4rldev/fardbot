use axum::{
    Router,
    extract::{Json, State, rejection::JsonRejection},
    http::{HeaderMap, StatusCode},
    response::{IntoResponse, Response},
    routing::post,
};
use poise::serenity_prelude::{ChannelId, CreateEmbed, CreateMessage, Http};
use serde::Deserialize;
use std::sync::{Arc, LazyLock};
use tokio::sync::Mutex;
use tracing::{error, info, warn};

static CLIENT: LazyLock<reqwest::Client> = LazyLock::new(reqwest::Client::new);

struct WebState {
    http: Http,
    channel: Arc<Mutex<Option<ChannelId>>>,
    secret: String,
    jellyfin_url: String,
    api_key: String,
}

#[derive(Deserialize)]
struct JellyfinEvent {
    kind: String,
    artist: Option<String>,
    track: Option<String>,
    album: Option<String>,
    #[serde(rename = "itemId")]
    item_id: Option<String>,
}

async fn jellyfin_event(
    State(state): State<Arc<WebState>>,
    headers: HeaderMap,
    body: Result<Json<serde_json::Value>, JsonRejection>,
) -> Response {
    let secret = headers.get("x-h4ip-secret").and_then(|s| s.to_str().ok());
    if secret != Some(&state.secret) {
        warn!("jellyfin event rejected: bad secret");
        return (StatusCode::UNAUTHORIZED, "Invalid secret").into_response();
    }

    let Json(value) = match body {
        Ok(body) => body,
        Err(_) => return (StatusCode::BAD_REQUEST, "Malformed body").into_response(),
    };

    let events: Vec<JellyfinEvent> = match value {
        serde_json::Value::Array(items) => match serde_json::from_value(serde_json::Value::Array(items)) {
            Ok(events) => events,
            Err(_) => return (StatusCode::BAD_REQUEST, "Malformed event array").into_response(),
        },
        value @ serde_json::Value::Object(_) => match serde_json::from_value::<JellyfinEvent>(value) {
            Ok(event) => vec![event],
            Err(_) => return (StatusCode::BAD_REQUEST, "Malformed event").into_response(),
        },
        _ => return (StatusCode::BAD_REQUEST, "Body must be an event or array of events").into_response(),
    };

    info!("received {} jellyfin event(s)", events.len());

    let Some(channel) = *state.channel.lock().await else {
        return StatusCode::NO_CONTENT.into_response();
    };

    for event in events {
        if let Err(e) = broadcast_event(&state, channel, event).await {
            error!("failed to broadcast an event: {e:?}");
        }
    }

    StatusCode::NO_CONTENT.into_response()
}

async fn broadcast_event(
    state: &WebState,
    channel: ChannelId,
    event: JellyfinEvent,
) -> Result<(), crate::Error> {
    let (title, description) = match event.kind.as_str() {
        "artist_added" => ("New artist", event.artist.unwrap_or_default()),
        "track_added" => {
            let track = event.track.unwrap_or_default();
            let artist = event.artist.unwrap_or_default();
            let album = event.album.unwrap_or_default();
            let desc = if album.is_empty() {
                format!("**{track}** by {artist}")
            } else {
                format!("**{track}** by {artist}\n{album}")
            };

            ("New track", desc)
        }
        _ => {
            warn!("unknown jellyfin event kind: {}", event.kind);
            return Ok(());
        }
    };

    let mut embed = CreateEmbed::new().title(title).description(description);
    if let Some(item_id) = event.item_id {
        let url = format!(
            "{}/Items/{}/Images/Primary?maxWidth=200&ApiKey={}",
            state.jellyfin_url, item_id, state.api_key
        );
        if image_available(&url).await {
            embed = embed.thumbnail(url);
        }
    }

    channel.send_message(&state.http, CreateMessage::new().embed(embed)).await?;
    info!("broadcast sent to channel {channel}");
    Ok(())
}

async fn image_available(url: &str) -> bool {
    CLIENT
        .head(url)
        .send()
        .await
        .map(|r| r.status().is_success())
        .unwrap_or(false)
}

pub async fn serve(
    http: Http,
    channel: Arc<Mutex<Option<ChannelId>>>,
    secret: String,
    jellyfin_url: String,
    api_key: String,
) {
    let app = Router::new()
        .route("/jellyfin/event", post(jellyfin_event))
        .with_state(Arc::new(WebState {
            http,
            channel,
            secret,
            jellyfin_url,
            api_key,
        }));

    let port = std::env::var("BOT_PORT")
        .unwrap_or_else(|_| "8080".to_string())
        .parse::<u16>()
        .expect("BOT_PORT must be a valid port number");

    let listener = tokio::net::TcpListener::bind(("0.0.0.0", port))
        .await
        .expect("Failed to bind to port");

    axum::serve(listener, app)
        .await
        .expect("Server failed to start");
}
