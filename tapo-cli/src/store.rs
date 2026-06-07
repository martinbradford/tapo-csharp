//! Disposable, corruption-resistant device cache.
//!
//! The cache lives at `~/.cache/tapo/devices.json` and is fully regenerable by
//! `tapo discover`. Design rules that keep it from corrupting (the original bug):
//!   * Only `discover` rewrites the whole file; `on/off/toggle` only ever do a
//!     targeted IP update. `status` never writes.
//!   * Every write is atomic: write a temp file in the same dir, then rename.
//!   * Reads are tolerant: a missing or malformed file yields an empty list and a
//!     hint, never a crash.

use std::fs;
use std::path::PathBuf;

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Device {
    pub device_id: String,
    pub nickname: String,
    pub ip: String,
    pub mac: String,
    pub model: String,
    /// One of: plug, plug_energy, light, color_light, rgb_light_strip,
    /// rgbic_light_strip, power_strip, power_strip_energy, hub, camera, other.
    #[serde(rename = "type")]
    pub kind: String,
    /// Last known on/off state from discovery (live state comes from `status`).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub device_on: Option<bool>,
}

impl Device {
    /// True for device kinds this CLI can currently switch on/off.
    pub fn controllable(&self) -> bool {
        matches!(self.kind.as_str(), "plug" | "plug_energy")
    }
}

pub fn cache_path() -> Result<PathBuf> {
    let dir = dirs::cache_dir().context("could not determine cache directory")?;
    Ok(dir.join("tapo").join("devices.json"))
}

/// Tolerant read: never fails on a missing or corrupt file.
pub fn load() -> Vec<Device> {
    let path = match cache_path() {
        Ok(p) => p,
        Err(_) => return Vec::new(),
    };
    let raw = match fs::read_to_string(&path) {
        Ok(s) => s,
        Err(_) => return Vec::new(), // no cache yet
    };
    match serde_json::from_str::<Vec<Device>>(&raw) {
        Ok(devices) => devices,
        Err(_) => {
            eprintln!(
                "warning: device cache at {} was unreadable; run `tapo discover` to rebuild it",
                path.display()
            );
            Vec::new()
        }
    }
}

/// Atomic write: temp file in the same directory, then rename over the target.
pub fn save(devices: &[Device]) -> Result<()> {
    let path = cache_path()?;
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)
            .with_context(|| format!("creating cache dir {}", parent.display()))?;
    }
    let json = serde_json::to_string_pretty(devices)?;
    let tmp = path.with_extension(format!("json.tmp.{}", std::process::id()));
    fs::write(&tmp, json).with_context(|| format!("writing {}", tmp.display()))?;
    fs::rename(&tmp, &path).with_context(|| format!("renaming into {}", path.display()))?;
    Ok(())
}

/// Resolve a user-supplied selector against the cache: matches device_id, MAC
/// (colons/case-insensitive), nickname (case-insensitive), or IP.
pub fn resolve<'a>(devices: &'a [Device], selector: &str) -> Option<&'a Device> {
    let want = selector.trim();
    let want_lower = want.to_lowercase();
    let want_mac = normalize_mac(want);
    devices.iter().find(|d| {
        d.device_id == want
            || d.ip == want
            || d.nickname.to_lowercase() == want_lower
            || normalize_mac(&d.mac) == want_mac
    })
}

fn normalize_mac(mac: &str) -> String {
    mac.chars()
        .filter(|c| c.is_ascii_alphanumeric())
        .collect::<String>()
        .to_lowercase()
}
