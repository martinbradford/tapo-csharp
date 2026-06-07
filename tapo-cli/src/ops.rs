//! Device discovery and control, built on the `tapo` crate.

use anyhow::{anyhow, bail, Result};
use tapo::{ApiClient, DiscoveryResult, StreamExt};

use crate::creds::Credentials;
use crate::store::{self, Device};

/// What to do to a device's relay.
#[derive(Clone, Copy)]
pub enum Action {
    On,
    Off,
    Toggle,
}

/// Broadcast-discover every Tapo device on the LAN. Authenticates with the given
/// credentials (the crate logs into each device during discovery), so wrong
/// credentials surface here.
pub async fn discover(creds: &Credentials, target: String, timeout_s: u64) -> Result<Vec<Device>> {
    let client = ApiClient::new(creds.username.clone(), creds.password.clone());
    let mut stream = client.discover_devices(target, timeout_s).await?;

    let mut devices = Vec::new();
    while let Some(result) = stream.next().await {
        let r = match result {
            Ok(r) => r,
            Err(e) => {
                eprintln!("warning: skipped a device during discovery: {e}");
                continue;
            }
        };

        let device_id = r.device_id().to_string();
        let nickname = r.nickname().to_string();
        let model = r.model().to_string();
        let ip = r.ip().to_string();

        // Pull MAC + on/off state for the plug variants we control; other kinds
        // are listed but not yet controllable.
        let (kind, mac, device_on) = match &r {
            DiscoveryResult::Plug { device_info, .. } => (
                "plug",
                device_info.mac.clone(),
                Some(device_info.device_on),
            ),
            DiscoveryResult::PlugEnergyMonitoring { device_info, .. } => (
                "plug_energy",
                device_info.mac.clone(),
                Some(device_info.device_on),
            ),
            other => (variant_kind(other), String::new(), None),
        };

        devices.push(Device {
            device_id,
            nickname,
            ip,
            mac,
            model,
            kind: kind.to_string(),
            device_on,
        });
    }

    devices.sort_by(|a, b| a.nickname.to_lowercase().cmp(&b.nickname.to_lowercase()));
    Ok(devices)
}

fn variant_kind(r: &DiscoveryResult) -> &'static str {
    match r {
        DiscoveryResult::Plug { .. } => "plug",
        DiscoveryResult::PlugEnergyMonitoring { .. } => "plug_energy",
        DiscoveryResult::Light { .. } => "light",
        DiscoveryResult::ColorLight { .. } => "color_light",
        DiscoveryResult::RgbLightStrip { .. } => "rgb_light_strip",
        DiscoveryResult::RgbicLightStrip { .. } => "rgbic_light_strip",
        DiscoveryResult::PowerStrip { .. } => "power_strip",
        DiscoveryResult::PowerStripEnergyMonitoring { .. } => "power_strip_energy",
        DiscoveryResult::Hub { .. } => "hub",
        DiscoveryResult::CameraPtz { .. } => "camera",
        DiscoveryResult::Other { .. } => "other",
    }
}

/// Read the live on/off state of a single device. Returns None if unreachable.
pub async fn read_state(creds: &Credentials, device: &Device) -> Option<bool> {
    let on = match device.kind.as_str() {
        "plug" => {
            let h = ApiClient::new(creds.username.clone(), creds.password.clone())
                .p100(device.ip.clone())
                .await
                .ok()?;
            h.get_device_info().await.ok()?.device_on
        }
        "plug_energy" => {
            let h = ApiClient::new(creds.username.clone(), creds.password.clone())
                .p110(device.ip.clone())
                .await
                .ok()?;
            h.get_device_info().await.ok()?.device_on
        }
        _ => return None,
    };
    Some(on)
}

/// Apply an action to a device by connecting to its current IP. Returns the
/// resulting on/off state.
pub async fn apply(creds: &Credentials, device: &Device, action: Action) -> Result<bool> {
    match device.kind.as_str() {
        "plug" => {
            let h = ApiClient::new(creds.username.clone(), creds.password.clone())
                .p100(device.ip.clone())
                .await?;
            let target = match action {
                Action::On => true,
                Action::Off => false,
                Action::Toggle => !h.get_device_info().await?.device_on,
            };
            if target {
                h.on().await?;
            } else {
                h.off().await?;
            }
            Ok(target)
        }
        "plug_energy" => {
            let h = ApiClient::new(creds.username.clone(), creds.password.clone())
                .p110(device.ip.clone())
                .await?;
            let target = match action {
                Action::On => true,
                Action::Off => false,
                Action::Toggle => !h.get_device_info().await?.device_on,
            };
            if target {
                h.on().await?;
            } else {
                h.off().await?;
            }
            Ok(target)
        }
        other => bail!("control of '{other}' devices is not supported yet"),
    }
}

/// Apply an action, and if the device can't be reached (e.g. DHCP changed its
/// IP) re-discover once, refresh the cached IP, and retry.
pub async fn apply_with_retry(
    creds: &Credentials,
    device: &Device,
    action: Action,
    target: String,
    timeout_s: u64,
) -> Result<bool> {
    match apply(creds, device, action).await {
        Ok(state) => Ok(state),
        // Only a reachability failure is worth a rescan; an unsupported device
        // type will just fail again.
        Err(first_err) if !device.controllable() => Err(first_err),
        Err(first_err) => {
            let refreshed = discover(creds, target, timeout_s).await?;
            store::save(&refreshed)?;
            let updated = refreshed
                .iter()
                .find(|d| d.device_id == device.device_id)
                .ok_or_else(|| {
                    anyhow!("'{}' is unreachable and was not found on re-scan ({first_err})", device.nickname)
                })?;
            apply(creds, updated, action).await
        }
    }
}

/// A reasonable default broadcast target: the /24 broadcast of the primary local
/// IPv4, falling back to the global broadcast address.
pub fn default_target() -> String {
    use std::net::{IpAddr, UdpSocket};
    if let Ok(sock) = UdpSocket::bind("0.0.0.0:0") {
        if sock.connect("8.8.8.8:80").is_ok() {
            if let Ok(local) = sock.local_addr() {
                if let IpAddr::V4(v4) = local.ip() {
                    let o = v4.octets();
                    return format!("{}.{}.{}.255", o[0], o[1], o[2]);
                }
            }
        }
    }
    "255.255.255.255".to_string()
}
