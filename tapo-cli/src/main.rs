//! `tapo` — discover and control TP-Link Tapo devices on the local network.
//!
//! Credentials live in the system keyring; the device list is a disposable cache
//! rebuilt by `tapo discover`. See the module docs in `store.rs` for why the
//! cache cannot corrupt the way the old C# store did.

mod creds;
mod ops;
mod store;

use std::io::Write;

use anyhow::{Context, Result};
use clap::{Parser, Subcommand};
use serde::Serialize;

use creds::Credentials;
use ops::Action;
use store::Device;

#[derive(Parser)]
#[command(name = "tapo", version, about = "Control TP-Link Tapo devices on your LAN")]
struct Cli {
    /// Emit machine-readable JSON instead of human text.
    #[arg(long, global = true)]
    json: bool,

    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    /// Store TP-Link account credentials and run an initial scan.
    Login {
        #[arg(long)]
        username: Option<String>,
        #[arg(long)]
        password: Option<String>,
    },
    /// Forget stored credentials.
    Logout,
    /// Broadcast-scan the LAN and refresh the device cache.
    Discover {
        /// Seconds to listen for replies.
        #[arg(long, default_value_t = 5)]
        timeout: u64,
        /// Broadcast target (defaults to the local /24 broadcast address).
        #[arg(long)]
        target: Option<String>,
    },
    /// Show cached devices without touching the network.
    List,
    /// Query each cached device's live on/off state (in parallel).
    Status,
    /// Turn a device on.
    On { selector: String },
    /// Turn a device off.
    Off { selector: String },
    /// Toggle a device.
    Toggle { selector: String },
    /// Turn every controllable device on or off.
    All {
        #[command(subcommand)]
        action: AllAction,
    },
}

#[derive(Subcommand)]
enum AllAction {
    On,
    Off,
}

#[tokio::main]
async fn main() {
    let cli = Cli::parse();
    if let Err(e) = run(cli).await {
        eprintln!("error: {e:#}");
        std::process::exit(1);
    }
}

async fn run(cli: Cli) -> Result<()> {
    let json = cli.json;
    match cli.command {
        Command::Login { username, password } => login(username, password, json).await,
        Command::Logout => {
            creds::delete()?;
            report_msg("Credentials removed.", json, "ok");
            Ok(())
        }
        Command::Discover { timeout, target } => {
            let creds = creds::load()?;
            let target = target.unwrap_or_else(ops::default_target);
            let devices = ops::discover(&creds, target, timeout).await?;
            store::save(&devices)?;
            print_devices(&devices, json);
            Ok(())
        }
        Command::List => {
            print_devices(&store::load(), json);
            Ok(())
        }
        Command::Status => status(json).await,
        Command::On { selector } => switch(&selector, Action::On, json).await,
        Command::Off { selector } => switch(&selector, Action::Off, json).await,
        Command::Toggle { selector } => switch(&selector, Action::Toggle, json).await,
        Command::All { action } => {
            let a = match action {
                AllAction::On => Action::On,
                AllAction::Off => Action::Off,
            };
            switch_all(a, json).await
        }
    }
}

async fn login(username: Option<String>, password: Option<String>, json: bool) -> Result<()> {
    let username = match username {
        Some(u) => u,
        None => prompt_line("TP-Link account email: ")?,
    };
    let password = match password {
        Some(p) => p,
        None => rpassword::prompt_password("TP-Link account password: ")
            .context("reading password")?,
    };
    let creds = Credentials { username, password };
    creds::save(&creds)?;

    // Populate the cache; this also surfaces wrong credentials (discovery
    // authenticates against each device).
    let devices = ops::discover(&creds, ops::default_target(), 5).await?;
    store::save(&devices)?;

    if json {
        print_json(&serde_json::json!({
            "status": "ok",
            "username": creds.username,
            "devices_found": devices.len(),
        }));
    } else {
        println!("Credentials saved. Found {} device(s).", devices.len());
        if devices.is_empty() {
            println!("(No devices replied — make sure your plugs are powered and on this network.)");
        } else {
            print_devices(&devices, false);
        }
    }
    Ok(())
}

async fn status(json: bool) -> Result<()> {
    let creds = creds::load()?;
    let devices = store::load();
    let creds_ref = &creds;

    let views = futures::future::join_all(devices.iter().map(|d| async move {
        let on = ops::read_state(creds_ref, d).await;
        StatusView {
            device_id: d.device_id.clone(),
            nickname: d.nickname.clone(),
            ip: d.ip.clone(),
            mac: d.mac.clone(),
            model: d.model.clone(),
            kind: d.kind.clone(),
            reachable: on.is_some(),
            device_on: on,
        }
    }))
    .await;

    if json {
        print_json(&views);
    } else if views.is_empty() {
        println!("No devices cached. Run `tapo discover`.");
    } else {
        for v in &views {
            let state = match (v.reachable, v.device_on) {
                (true, Some(true)) => "ON ",
                (true, Some(false)) => "OFF",
                _ => "—  ",
            };
            let offline = if v.reachable { "" } else { "  (offline)" };
            println!("{state}  {}  [{}]  {}{offline}", v.nickname, v.model, v.ip);
        }
    }
    Ok(())
}

async fn switch(selector: &str, action: Action, json: bool) -> Result<()> {
    let creds = creds::load()?;
    let devices = store::load();
    let device = store::resolve(&devices, selector)
        .cloned()
        .with_context(|| no_match_msg(selector, &devices))?;

    let state = ops::apply_with_retry(&creds, &device, action, ops::default_target(), 5).await?;
    emit_switch(&device, state, json);
    Ok(())
}

async fn switch_all(action: Action, json: bool) -> Result<()> {
    let creds = creds::load()?;
    let devices = store::load();
    let creds_ref = &creds;

    let results = futures::future::join_all(
        devices
            .iter()
            .filter(|d| d.controllable())
            .map(|d| async move {
                let r = ops::apply(creds_ref, d, action).await;
                (d, r)
            }),
    )
    .await;

    if json {
        let out: Vec<_> = results
            .iter()
            .map(|(d, r)| {
                serde_json::json!({
                    "device_id": d.device_id,
                    "nickname": d.nickname,
                    "device_on": r.as_ref().ok().copied(),
                    "error": r.as_ref().err().map(|e| e.to_string()),
                })
            })
            .collect();
        print_json(&out);
    } else {
        for (d, r) in &results {
            match r {
                Ok(state) => println!("{} → {}", d.nickname, if *state { "on" } else { "off" }),
                Err(e) => println!("{} → error: {e}", d.nickname),
            }
        }
    }
    Ok(())
}

// ---- output helpers ----

#[derive(Serialize)]
struct StatusView {
    device_id: String,
    nickname: String,
    ip: String,
    mac: String,
    model: String,
    #[serde(rename = "type")]
    kind: String,
    reachable: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    device_on: Option<bool>,
}

fn emit_switch(device: &Device, state: bool, json: bool) {
    if json {
        print_json(&serde_json::json!({
            "device_id": device.device_id,
            "nickname": device.nickname,
            "device_on": state,
        }));
    } else {
        println!("{} → {}", device.nickname, if state { "on" } else { "off" });
    }
}

fn print_devices(devices: &[Device], json: bool) {
    if json {
        print_json(&devices);
        return;
    }
    if devices.is_empty() {
        println!("No devices. Run `tapo discover`.");
        return;
    }
    for d in devices {
        let state = match d.device_on {
            Some(true) => "ON ",
            Some(false) => "OFF",
            None => "—  ",
        };
        println!("{state}  {}  [{}]  {}", d.nickname, d.model, d.ip);
    }
}

fn report_msg(text: &str, json: bool, status: &str) {
    if json {
        print_json(&serde_json::json!({ "status": status, "message": text }));
    } else {
        println!("{text}");
    }
}

fn print_json<T: Serialize>(value: &T) {
    match serde_json::to_string_pretty(value) {
        Ok(s) => println!("{s}"),
        Err(e) => eprintln!("error serializing JSON: {e}"),
    }
}

fn prompt_line(prompt: &str) -> Result<String> {
    print!("{prompt}");
    std::io::stdout().flush().ok();
    let mut line = String::new();
    std::io::stdin().read_line(&mut line).context("reading input")?;
    Ok(line.trim().to_string())
}

fn no_match_msg(selector: &str, devices: &[Device]) -> String {
    if devices.is_empty() {
        return format!("no device matches '{selector}', and the cache is empty — run `tapo discover`");
    }
    let names: Vec<_> = devices.iter().map(|d| d.nickname.as_str()).collect();
    format!("no device matches '{selector}'. Known: {}", names.join(", "))
}
