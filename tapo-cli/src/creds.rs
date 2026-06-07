//! TP-Link account credentials, stored in the system keyring (GNOME keyring via
//! the freedesktop Secret Service). These double as the per-device local KLAP
//! password, so on/off/status all need them — discovery does not.

use anyhow::{anyhow, Context, Result};
use keyring::Entry;
use serde::{Deserialize, Serialize};

const SERVICE: &str = "tapo-cli";
const ACCOUNT: &str = "tplink-account";

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Credentials {
    pub username: String,
    pub password: String,
}

fn entry() -> Result<Entry> {
    Entry::new(SERVICE, ACCOUNT).context("opening keyring entry")
}

pub fn save(creds: &Credentials) -> Result<()> {
    let blob = serde_json::to_string(creds)?;
    entry()?.set_password(&blob).context("storing credentials in keyring")?;
    Ok(())
}

pub fn load() -> Result<Credentials> {
    let blob = match entry()?.get_password() {
        Ok(b) => b,
        Err(keyring::Error::NoEntry) => {
            return Err(anyhow!("not logged in — run `tapo login` first"))
        }
        Err(e) => return Err(anyhow!("reading keyring: {e}")),
    };
    serde_json::from_str(&blob).context("parsing stored credentials")
}

pub fn delete() -> Result<()> {
    match entry()?.delete_credential() {
        Ok(()) | Err(keyring::Error::NoEntry) => Ok(()),
        Err(e) => Err(anyhow!("removing credentials from keyring: {e}")),
    }
}
