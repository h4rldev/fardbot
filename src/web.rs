use axum::{
    Router,
    extract::{Json, State, rejection::JsonRejection},
    http::{HeaderMap, StatusCode},
    response::{IntoResponse, Response},
    routing::post,
};
use poise::serenity_prelude::{ChannelId, CreateEmbed, CreateMessage, Http};
use serde::Deserialize;
use std::sync::Arc;
use tokio::sync::Mutex;
use tracing::{error, info, warn};

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
    body: Result<Json<JellyfinEvent>, JsonRejection>,
) -> Response {
    let secret = headers.get("x-h4ip-secret").and_then(|s| s.to_str().ok());
    if secret != Some(&state.secret) {
        warn!("jellyfin event rejected: bad secret");
        return (StatusCode::UNAUTHORIZED, "Invalid secret").into_response();
    }

    let Some(channel) = *state.channel.lock().await else {
        return StatusCode::NO_CONTENT.into_response();
    };

    let Json(event) = match body {
        Ok(body) => body,
        Err(_) => return (StatusCode::BAD_REQUEST, "Malformed body").into_response(),
    };

    info!(
        "received jellyfin event: kind={}, artist={:?}, track={:?}, album={:?}, item_id={:?}",
        event.kind, event.artist, event.track, event.album, event.item_id
    );

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
        _ => return (StatusCode::BAD_REQUEST, "Invalid event kind").into_response(),
    };

    let mut embed = CreateEmbed::new().title(title).description(description);
    if let Some(item_id) = event.item_id {
        let url = format!(
            "{}/Items/{}/Images/Primary?maxWidth=200&ApiKey={}",
            state.jellyfin_url, item_id, state.api_key
        );
        embed = embed.thumbnail(url);
    }

    match channel
        .send_message(&state.http, CreateMessage::new().embed(embed))
        .await
    {
        Ok(_) => {
            info!("broadcast sent to channel {channel}");
            StatusCode::NO_CONTENT.into_response()
        }
        Err(e) => {
            error!("Failed to broadcast event: {:?}", e);
            (
                StatusCode::INTERNAL_SERVER_ERROR,
                "Failed to broadcast event",
            )
                .into_response()
        }
    }
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
