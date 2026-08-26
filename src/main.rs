use std::{collections::HashMap, sync::Arc};

use poise::serenity_prelude as serenity;
use tokio::sync::Mutex;
use tracing::info;

mod commands;
mod web;

use commands::{
    fun::{balls, hello},
    jellyfin::{crown, now_playing, setchannel, setup, suggest, top},
    utility::{get_week, ping, status},
};

const USER_MAP_PATH: &str = "user_map.json";
fn load_user_map() -> HashMap<u64, String> {
    std::fs::read_to_string(USER_MAP_PATH)
        .ok()
        .and_then(|s| serde_json::from_str(&s).ok())
        .unwrap_or_default()
}

pub fn save_user_map(user_map: &HashMap<u64, String>) {
    if let Ok(json) = serde_json::to_string(user_map) {
        let _ = std::fs::write(USER_MAP_PATH, json);
    }
}

fn load_channel() -> Option<u64> {
    std::fs::read_to_string("jellyfin_channel_id.txt")
        .ok()
        .and_then(|s| s.trim().parse::<u64>().ok())
}

pub fn save_channel(channel_id: u64) {
    let _ = std::fs::write("jellyfin_channel_id.txt", channel_id.to_string());
}

/// Shows help for a command, or lists all commands.
#[poise::command(slash_command, track_edits)]
async fn help(
    ctx: Context<'_>,
    #[description = "Command you need help about"] command: Option<String>,
) -> Result<(), Error> {
    let config = poise::builtins::HelpConfiguration {
        ..Default::default()
    };

    poise::builtins::help(ctx, command.as_deref(), config).await?;
    Ok(())
}

async fn pre_command(ctx: Context<'_>) {
    info!(
        "{} running command: {}",
        ctx.author().name,
        ctx.command().qualified_name
    )
}

async fn post_command(ctx: Context<'_>) {
    info!(
        "{} ran command: {}",
        ctx.author().name,
        ctx.command().qualified_name
    )
}

pub struct Data {
    pub user_map: Mutex<HashMap<u64, String>>,
    pub channel: Arc<Mutex<Option<serenity::ChannelId>>>,
}

pub type Error = Box<dyn std::error::Error + Send + Sync>;
pub type Context<'a> = poise::Context<'a, Data, Error>;

#[tokio::main]
async fn main() {
    dotenvy::dotenv().expect(".env file not found");
    tracing_subscriber::fmt::init();

    let secret = std::env::var("PLUGIN_SECRET").expect("missing PLUGIN_SECRET");
    let jellyfin_url = std::env::var("JELLYFIN_URL").expect("missing JELLYFIN_URL");
    let api_key = std::env::var("JELLYFIN_API_KEY").expect("missing JELLYFIN_API_KEY");

    let token = std::env::var("BOT_TOKEN").expect("missing DISCORD_TOKEN");
    let intents = serenity::GatewayIntents::non_privileged();
    let channel = Arc::new(Mutex::new(load_channel().map(serenity::ChannelId::new)));

    tokio::spawn(web::serve(
        serenity::Http::new(&token.clone()),
        Arc::clone(&channel),
        secret,
        jellyfin_url,
        api_key,
    ));

    let framework = poise::Framework::builder()
        .options(poise::FrameworkOptions {
            pre_command: |ctx| Box::pin(pre_command(ctx)),
            post_command: |ctx| Box::pin(post_command(ctx)),
            commands: vec![
                help(),
                hello(),
                balls(),
                ping(),
                status(),
                get_week(),
                setup(),
                setchannel(),
                top(),
                now_playing(),
                crown(),
                suggest(),
            ],
            ..Default::default()
        })
        .setup(|ctx, _ready, framework| {
            Box::pin(async move {
                poise::builtins::register_globally(ctx, &framework.options().commands).await?;
                Ok(Data {
                    user_map: Mutex::new(load_user_map()),
                    channel,
                })
            })
        })
        .build();

    let client = serenity::ClientBuilder::new(token, intents)
        .framework(framework)
        .await;

    client
        .expect("Can't construct client")
        .start()
        .await
        .expect("Can't start.");
}
