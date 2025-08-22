use crate::{
    Context, Error,
    serenity::{Mentionable, User},
};
use poise::{
    ChoiceParameter, command,
    serenity_prelude::{EditMember, GuildId, Member, Permissions},
};
use rand::{Rng, seq::SliceRandom, thread_rng};

use tracing::info;

#[derive(ChoiceParameter, Debug)]
enum Balls {
    #[name = "Balls a single person"]
    Single,

    #[name = "Balls multiple people"]
    Multiple,

    #[name = "Balls everyone"]
    All,
}

// Replies with hi!
#[command(slash_command, category = "Fun")]
pub async fn hello(ctx: Context<'_>) -> Result<(), Error> {
    ctx.reply("Hi!").await?;
    Ok(())
}

async fn get_users(ctx: &Context<'_>, guild_id: &GuildId) -> Result<Vec<User>, Error> {
    let users = guild_id.members(ctx.http(), None, None).await?;
    let mut users: Vec<User> = users.iter().map(|user| user.user.clone()).collect();
    let guild = ctx.guild().expect("Failed to get guild");
    let owner_id = guild.owner_id;

    users.retain(|user| user.id != ctx.framework().bot_id && user.id != owner_id);
    Ok(users)
}

async fn get_channel_permissions(ctx: &Context<'_>) -> Result<Permissions, Error> {
    let guild = ctx.partial_guild().await.expect("Failed to get guild");
    let guild_channels = guild
        .channels(&ctx.http())
        .await
        .expect("Failed to get channel");
    let current_guild_channel = guild_channels
        .get(&ctx.channel_id())
        .expect("Failed to get channel");

    let bot_member = guild.member(&ctx.http(), ctx.framework().bot_id).await?;

    let perms = guild.user_permissions_in(current_guild_channel, &bot_member);
    Ok(perms)
}

// Balls people!!!
#[command(
    slash_command,
    category = "Fun",
    required_permissions = "MANAGE_NICKNAMES",
    ephemeral
)]
pub async fn balls(
    ctx: Context<'_>,
    balls_choice: Option<Balls>,
    specific: Option<Member>,
) -> Result<(), Error> {
    let bot_permissions = get_channel_permissions(&ctx).await?;
    if !bot_permissions.manage_nicknames() {
        return Err("I don't have permission to change nicknames in this server!".into());
    }

    let names_for_balls = [
        "tokhme".to_string(),
        "balls".to_string(),
        "rocks".to_string(),
        "nuts".to_string(),
        "testicles".to_string(),
        "family jewels".to_string(),
        "bollocks".to_string(),
        "ballocks".to_string(),
        "cullions".to_string(),
        "jewels".to_string(),
        "orbs".to_string(),
    ];

    let guild_id = ctx.guild_id().expect("Can't get guild_id");
    let users = get_users(&ctx, &guild_id).await?;
    let length = users.len();

    match &balls_choice {
        Some(choice) => match choice {
            Balls::Single => {
                ctx.reply("Ballsing a single person").await?;
            }
            Balls::Multiple => {
                ctx.reply("Ballsing multiple people").await?;
            }
            Balls::All => {
                ctx.reply("Ballsing everyone").await?;
            }
        },
        None => {
            if specific.is_some() {
                ctx.reply(format!(
                    "Ballsing: {}",
                    specific
                        .as_ref()
                        .expect("Failed to get member")
                        .mention()
                        .to_string()
                ))
                .await?;
            } else {
                ctx.reply("Ballsing a random person").await?;
            }
        }
    }

    // Defer the response to avoid timeout
    ctx.defer_ephemeral().await?;

    let who_to_balls = match balls_choice {
        Some(balls_choices) => match balls_choices {
            Balls::Single => pick_random(1, users).await?,
            Balls::Multiple => {
                let amount = thread_rng().gen_range(3..length);
                pick_random(amount.try_into()?, users).await?
            }
            Balls::All => users,
        },
        None => {
            if specific.is_some() {
                vec![specific.unwrap().user]
            } else {
                pick_random(1, users).await?
            }
        }
    };

    for user in who_to_balls.clone() {
        let mut member = guild_id.member(&ctx.serenity_context(), user.id).await?;

        let random_nickname = {
            let mut rng = thread_rng();
            names_for_balls
                .choose(&mut rng)
                .expect("Failed to pick random name")
        };
        let edit_builder = EditMember::new().nickname(random_nickname);
        info!("Editing nickname for {} to {}", user.name, random_nickname);
        member.edit(&ctx.http(), edit_builder).await?;
    }

    let mention_users: String = who_to_balls
        .iter()
        .map(|user| format!("- {}", user.mention().to_string()))
        .collect::<Vec<String>>()
        .join("\n");
    let reply = format!("Successfully ballsed:\n {}", mention_users);
    ctx.reply(reply).await?;
    Ok(())
}

async fn pick_random(amount: usize, users: Vec<User>) -> Result<Vec<User>, Error> {
    let users = if amount > 1 {
        let users: Vec<User> = {
            let mut rng = thread_rng();
            let pick: Vec<&User> = users.choose_multiple(&mut rng, amount).collect();
            let derefd: Vec<User> = pick.into_iter().cloned().collect();
            derefd
        };
        users
    } else {
        let user = {
            let mut rng = thread_rng();
            users.choose(&mut rng)
        };
        let users: Vec<User> = vec![user.unwrap().clone()];
        users
    };
    Ok(users)
}
