use crate::{Context, Error};
use poise::{
    ChoiceParameter, CreateReply,
    serenity_prelude::{
        Channel, ChannelId, ComponentInteractionCollector, CreateActionRow, CreateButton,
        CreateEmbed,
    },
};
use serde::{Deserialize, de::DeserializeOwned};
use std::{sync::LazyLock, time::Duration};
use tracing::info;

static CLIENT: LazyLock<reqwest::Client> = LazyLock::new(|| {
    reqwest::Client::builder()
        .timeout(Duration::from_secs(30))
        .build()
        .expect("Failed to build reqwest client")
});

static JELLYFIN_URL: LazyLock<String> =
    LazyLock::new(|| std::env::var("JELLYFIN_URL").expect("missing JELLYFIN_URL"));
static JELLYFIN_API_KEY: LazyLock<String> =
    LazyLock::new(|| std::env::var("JELLYFIN_API_KEY").expect("missing JELLYFIN_API_KEY"));

async fn jf_get<T: DeserializeOwned>(path: &str, params: &[(&str, &str)]) -> Result<T, Error> {
    let response = CLIENT
        .get(format!(
            "{}/{}",
            JELLYFIN_URL.as_str().trim_end_matches('/'),
            path.trim_start_matches('/')
        ))
        .query(params)
        .header(
            "Authorization",
            format!("MediaBrowser Token=\"{}\"", JELLYFIN_API_KEY.as_str()),
        )
        .send()
        .await?;
    info!(
        "jellyfin GET {path}?{} -> {}",
        params
            .iter()
            .map(|(k, v)| format!("{k}={v}"))
            .collect::<Vec<_>>()
            .join("&"),
        response.status()
    );

    let body = response.text().await?;
    match serde_json::from_str::<T>(&body) {
        Ok(value) => Ok(value),
        Err(e) => {
            info!("failed to decode jellyfin response for {path}: {e}; body: {body}");
            Err(e.into())
        }
    }
}

async fn jf_post(path: &str, body: &serde_json::Value) -> Result<(), Error> {
    let response = CLIENT
        .post(format!(
            "{}/{}",
            JELLYFIN_URL.as_str().trim_end_matches('/'),
            path.trim_start_matches('/')
        ))
        .header(
            "Authorization",
            format!("MediaBrowser Token=\"{}\"", JELLYFIN_API_KEY.as_str()),
        )
        .json(body)
        .send()
        .await?;

    let status = response.status();
    info!("jellyfin POST {path} -> {}", status);

    if !status.is_success() {
        return Err(format!("jellyfin POST {path} returned {status}").into());
    }

    Ok(())
}

#[derive(Deserialize)]
struct CrownEntry {
    #[serde(rename = "User", alias = "user")]
    user: String,
    #[serde(rename = "Count", alias = "count")]
    count: u32,
}

#[derive(Deserialize)]
struct TopEntry {
    #[serde(rename = "ItemName", alias = "itemName")]
    item_name: String,
    #[serde(rename = "Count", alias = "count")]
    count: u32,
}

#[derive(Deserialize)]
struct ItemsResponse {
    #[serde(rename = "Items")]
    items: Vec<MediaItem>,
}

#[derive(Deserialize)]
struct MediaItem {
    #[serde(rename = "Artists", default)]
    artists: Vec<String>,
    #[serde(rename = "AlbumArtist", default)]
    album_artist: Option<String>,
}

#[derive(Deserialize)]
struct JFUser {
    #[serde(rename = "Id")]
    id: String,
}

#[derive(Deserialize)]
struct Session {
    #[serde(rename = "UserId")]
    user_id: Option<String>,
    #[serde(rename = "NowPlayingItem")]
    now_playing: Option<NowPlaying>,
}

#[derive(Deserialize, Clone)]
struct NowPlaying {
    #[serde(rename = "Name")]
    name: String,
    #[serde(rename = "Id")]
    id: String,
    #[serde(rename = "Artists")]
    artists: Option<Vec<String>>,
    #[serde(rename = "AlbumArtist")]
    album_artist: Option<String>,
    #[serde(rename = "Album")]
    album: Option<String>,
    #[serde(rename = "Type")]
    item_type: String,
}

#[derive(Deserialize)]
struct SearchHints {
    #[serde(rename = "SearchHints")]
    hints: Vec<SearchHint>,
}

#[derive(Deserialize)]
struct SearchHint {
    #[serde(rename = "Name")]
    name: String,
    #[serde(rename = "Type")]
    item_type: String,
    #[serde(rename = "ItemId")]
    item_id: String,
    #[serde(rename = "AlbumArtist")]
    album_artist: Option<String>,
    #[serde(rename = "Album")]
    album: Option<String>,
}

#[derive(Deserialize, ChoiceParameter)]
enum ItemKind {
    #[name = "Artist"]
    Artist,
    #[name = "Album"]
    Album,
    #[name = "Track"]
    Track,
}

impl ItemKind {
    fn as_str(&self) -> &str {
        match self {
            ItemKind::Artist => "artist",
            ItemKind::Album => "album",
            ItemKind::Track => "track",
        }
    }
}

fn hint_to_kind(item_type: &str) -> Option<ItemKind> {
    match item_type {
        "MusicArtist" => Some(ItemKind::Artist),
        "MusicAlbum" => Some(ItemKind::Album),
        "Audio" => Some(ItemKind::Track),
        _ => None,
    }
}

fn search_type(kind: &ItemKind) -> &'static str {
    match kind {
        ItemKind::Artist => "MusicArtist",
        ItemKind::Album => "MusicAlbum",
        ItemKind::Track => "Audio",
    }
}

fn thumbnail_url(item_id: &str) -> String {
    format!(
        "{}/Items/{}/Images/Primary?maxWidth=200&ApiKey={}",
        JELLYFIN_URL.as_str(),
        item_id,
        JELLYFIN_API_KEY.as_str()
    )
}

fn linked_user(ctx: &Context<'_>) -> Option<String> {
    ctx.data()
        .user_map
        .lock()
        .unwrap()
        .get(&ctx.author().id.get())
        .cloned()
}

async fn current_audio(user_id: &str) -> Option<NowPlaying> {
    let sessions: Vec<Session> = jf_get("Sessions", &[("activeWithinSeconds", "120")])
        .await
        .ok()?;
    sessions
        .iter()
        .filter(|s| s.user_id.as_deref() == Some(user_id))
        .filter_map(|s| s.now_playing.as_ref())
        .find(|np| np.item_type == "Audio")
        .cloned()
}

async fn most_recent_artist(user_id: &str) -> Option<String> {
    if let Some(np) = current_audio(user_id).await
        && let Some(artist) = np
            .artists
            .as_ref()
            .and_then(|a| a.first().cloned())
            .or_else(|| np.album_artist.clone())
            .filter(|a| !a.is_empty())
    {
        return Some(artist);
    }

    let recent: ItemsResponse = jf_get(
        &format!("Users/{user_id}/Items"),
        &[
            ("SortBy", "DatePlayed"),
            ("SortOrder", "Descending"),
            ("Filters", "IsPlayed"),
            ("IncludeItemTypes", "Audio"),
            ("Limit", "1"),
        ],
    )
    .await
    .ok()?;
    recent
        .items
        .into_iter()
        .next()
        .and_then(|item| item.artists.into_iter().next().or(item.album_artist))
        .filter(|a| !a.is_empty())
}

async fn resolve_item_id(kind: &ItemKind, name: &str) -> Option<String> {
    let search: SearchHints = jf_get("/Search/Hints", &[("searchTerm", name), ("limit", "5")])
        .await
        .ok()?;
    search
        .hints
        .iter()
        .find(|h| h.item_type == search_type(kind))
        .or_else(|| {
            search
                .hints
                .iter()
                .find(|h| hint_to_kind(&h.item_type).is_some())
        })
        .map(|h| h.item_id.clone())
}

async fn reply_embed(
    ctx: &Context<'_>,
    title: &str,
    description: String,
    ephemeral: bool,
) -> Result<(), Error> {
    reply_embed_thumb(ctx, title, description, ephemeral, None).await
}

async fn reply_embed_thumb(
    ctx: &Context<'_>,
    title: &str,
    description: String,
    ephemeral: bool,
    thumbnail: Option<String>,
) -> Result<(), Error> {
    let mut embed = CreateEmbed::new().title(title).description(description);
    if let Some(url) = thumbnail {
        embed = embed.thumbnail(url);
    }
    ctx.send(CreateReply::default().embed(embed).ephemeral(ephemeral))
        .await?;
    Ok(())
}

/// Shows who knows an item — the ranked listeners, top listener first.
#[poise::command(slash_command, category = "Jellyfin")]
pub async fn whoknows(
    ctx: Context<'_>,
    #[description = "Artist, album, or track name (default to what's playing)"] name: Option<
        String,
    >,
    #[description = "How many"] limit: Option<u32>,
) -> Result<(), Error> {
    ctx.defer().await?;

    let name = match name {
        Some(name) => name,
        None => {
            let Some(user_id) = linked_user(&ctx) else {
                return reply_embed(
                    &ctx,
                    "Not linked",
                    "Run `/setup` first to link your Jellyfin account.".into(),
                    true,
                )
                .await;
            };

            match most_recent_artist(&user_id).await {
                Some(artist) => artist,
                None => {
                    return reply_embed(
                        &ctx,
                        "Whoknows",
                        "You haven't listened to anything yet.".into(),
                        true,
                    )
                    .await;
                }
            }
        }
    };

    let search: SearchHints = jf_get(
        "Search/Hints",
        &[("searchTerm", name.as_str()), ("limit", "10")],
    )
    .await?;

    let hint = ["MusicArtist", "MusicAlbum", "Audio"]
        .iter()
        .find_map(|t| {
            search
                .hints
                .iter()
                .find(|h| h.item_type == *t && h.name.eq_ignore_ascii_case(&name))
        })
        .or_else(|| {
            search
                .hints
                .iter()
                .find(|h| h.name.eq_ignore_ascii_case(&name))
        })
        .or_else(|| {
            search
                .hints
                .iter()
                .find(|h| hint_to_kind(&h.item_type).is_some())
        });

    let Some(hint) = hint else {
        return reply_embed(
            &ctx,
            "Whoknows",
            format!("Couldn't find anything matching **{name}**."),
            true,
        )
        .await;
    };
    let Some(kind) = hint_to_kind(&hint.item_type) else {
        return reply_embed(
            &ctx,
            "Whoknows",
            format!("**{}** isn't an artist, album, or track.", hint.name),
            true,
        )
        .await;
    };

    let limit = limit.unwrap_or(10).to_string();
    let entries: Vec<CrownEntry> = jf_get(
        "h4ip/crown",
        &[
            ("kind", kind.as_str()),
            ("name", hint.name.as_str()),
            ("limit", limit.as_str()),
        ],
    )
    .await?;

    let mut description = format!("Plays of {}", hint.name);
    if let Some(a) = hint.album_artist.as_deref().filter(|a| !a.is_empty()) {
        description.push_str(&format!(" by {a}"));
    }
    if let Some(al) = hint.album.as_deref().filter(|a| !a.is_empty()) {
        description.push_str(&format!(" on {al}"));
    }

    if entries.is_empty() {
        description.push_str("\n\nNobody has played this yet.");
    } else {
        description.push_str(&format!(
            "\n\n{}",
            entries
                .iter()
                .enumerate()
                .map(|(i, e)| {
                    if i == 0 {
                        format!("{}. **{}** — {} plays 👑", i + 1, e.user, e.count)
                    } else {
                        format!("{}. **{}** — {} plays", i + 1, e.user, e.count)
                    }
                })
                .collect::<Vec<_>>()
                .join("\n")
        ));
    }

    let thumb = if hint.item_id.is_empty() {
        None
    } else {
        Some(thumbnail_url(&hint.item_id))
    };
    reply_embed_thumb(
        &ctx,
        &format!("Top listeners for {}", hint.name),
        description,
        false,
        thumb,
    )
    .await
}

/// Adds an artist to the suggestion queue.
#[poise::command(slash_command, category = "Jellyfin")]
pub async fn suggest(
    ctx: Context<'_>,
    #[description = "Artist to suggest"] artist: String,
    #[description = "Optional notes for the suggestion"] notes: Option<String>,
) -> Result<(), Error> {
    jf_post(
        "/h4ip/suggestions",
        &serde_json::json!({ "artist": artist, "notes": notes }),
    )
    .await?;

    let msg = if notes.is_some() {
        format!(
            "**{artist}** was added to the queue, with notes:\n`{}`",
            notes.unwrap()
        )
    } else {
        format!("**{artist}** was added to the queue.")
    };

    reply_embed(&ctx, "Suggestion added!", msg, true).await
}

/// Shows what you're currently playing on the Jellyfin server.
#[poise::command(slash_command, category = "Jellyfin")]
pub async fn now_playing(ctx: Context<'_>) -> Result<(), Error> {
    ctx.defer().await?;

    let Some(user_id) = linked_user(&ctx) else {
        return reply_embed(
            &ctx,
            "Not Linked",
            "Run `/setup` first to link your Jellyfin account.".into(),
            true,
        )
        .await;
    };

    let Some(np) = current_audio(&user_id).await else {
        return reply_embed(
            &ctx,
            "Now Playing",
            "You're not playing anything right now.".into(),
            false,
        )
        .await;
    };

    let artist = np.album_artist.as_deref().unwrap_or("unknown");
    let album = np.album.as_deref().unwrap_or("");
    let description = if album.is_empty() {
        format!("by **{artist}**")
    } else {
        format!("on **{album}**\nby **{artist}**")
    };
    let thumb = if np.id.is_empty() {
        None
    } else {
        Some(thumbnail_url(&np.id))
    };
    reply_embed_thumb(&ctx, &np.name, description, false, thumb).await
}

#[derive(poise::Modal)]
#[name = "Link your Jellyfin account"]
struct SetupModal {
    #[name = "Jellyfin User ID"]
    #[placeholder = "Find it in Jellyfin → your profile → the Id under your name"]
    user_id: String,
}

/// Links your Discord account to your Jellyfin user ID.
#[poise::command(slash_command, category = "Jellyfin")]
pub async fn setup(ctx: Context<'_>) -> Result<(), Error> {
    let profile_url = &format!("{}/web/#/mypreferencesmenu", JELLYFIN_URL.as_str());

    ctx.send(
        poise::CreateReply::default()
            .content("Open your Jellyfin profile to find your user ID, then enter it below.")
            .components(vec![CreateActionRow::Buttons(vec![
                CreateButton::new_link(profile_url).label("Open Jellyfin profile"),
                CreateButton::new("open_setup_modal").label("Enter user ID"),
            ])])
            .ephemeral(true),
    )
    .await?;

    while let Some(mci) = ComponentInteractionCollector::new(ctx.serenity_context())
        .timeout(Duration::from_secs(120))
        .filter(|mci| mci.data.custom_id == "open_setup_modal")
        .await
    {
        let Some(modal) =
            poise::execute_modal_on_component_interaction::<SetupModal>(ctx, mci, None, None)
                .await?
        else {
            continue;
        };

        let users: Vec<JFUser> = jf_get("/Users", &[]).await?;
        if !users
            .iter()
            .any(|u| u.id.eq_ignore_ascii_case(&modal.user_id))
        {
            ctx.send(
                poise::CreateReply::default().embed(
                    CreateEmbed::new()
                        .title("Account link failed")
                        .description(format!(
                            "No Jellyfin user with ID **{}** was found.",
                            modal.user_id
                        )),
                ),
            )
            .await?;
            continue;
        }

        {
            let mut map = ctx.data().user_map.lock().unwrap();
            map.insert(ctx.author().id.get(), modal.user_id.to_lowercase());
            crate::save_user_map(&map);
        }

        ctx.send(
            poise::CreateReply::default().embed(
                CreateEmbed::new()
                    .title("Account linked")
                    .description(format!(
                        "Linked to Jellyfin user ID **{}**.\n[Open your Jellyfin profile]({profile_url})",
                        modal.user_id
                    )),
            ).ephemeral(true),
        )
        .await?;
        break;
    }
    Ok(())
}

/// Shows your top artists, albums, or tracks.
#[poise::command(slash_command, category = "Jellyfin")]
pub async fn top(
    ctx: Context<'_>,
    #[description = "Artist, album, or track"] kind: ItemKind,
) -> Result<(), Error> {
    let user_id = ctx
        .data()
        .user_map
        .lock()
        .unwrap()
        .get(&ctx.author().id.get())
        .cloned();
    let Some(user_id) = user_id else {
        return reply_embed(
            &ctx,
            "Not linked",
            "Run `/setup` first to link your Jellyfin account.".into(),
            true,
        )
        .await;
    };

    ctx.defer().await?;
    let entries: Vec<TopEntry> = jf_get(
        "h4ip/top",
        &[
            ("user", user_id.as_str()),
            ("kind", kind.as_str()),
            ("limit", "10"),
        ],
    )
    .await?;

    if entries.is_empty() {
        reply_embed(
            &ctx,
            &format!("Your top {}s", kind.as_str()),
            "No plays recorded yet.".into(),
            false,
        )
        .await
    } else {
        let lines: Vec<String> = entries
            .iter()
            .enumerate()
            .map(|(i, e)| format!("{}. **{}** — {} plays", i + 1, e.item_name, e.count))
            .collect();
        let thumb = resolve_item_id(&kind, &entries[0].item_name)
            .await
            .map(|id| thumbnail_url(&id));
        reply_embed_thumb(
            &ctx,
            &format!("Your top {}s", kind.as_str()),
            lines.join("\n"),
            false,
            thumb,
        )
        .await
    }
}

/// Sets the channel that Jellyfin broadcasts go to.
#[poise::command(
    slash_command,
    category = "Jellyfin",
    required_permissions = "MANAGE_CHANNELS"
)]
pub async fn setchannel(
    ctx: Context<'_>,
    #[description = "Channel to receive broadcasts (defaults to this channel)"] channel: Option<
        Channel,
    >,
) -> Result<(), Error> {
    let id = channel
        .map(|c| c.id().get())
        .unwrap_or_else(|| ctx.channel_id().get());
    *ctx.data().channel.lock().await = Some(ChannelId::new(id));

    crate::save_channel(id);

    reply_embed(
        &ctx,
        "Broadcast channel set",
        format!("Jellyfin broadcasts will go to <#{id}>."),
        true,
    )
    .await
}
