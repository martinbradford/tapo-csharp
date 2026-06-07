# Tapo

Control TP-Link Tapo smart plugs from Linux — a small Rust CLI plus a GNOME
**Quick Settings** tile.

- **No manual IP entry, no network scanning.** Devices are found with a single
  UDP broadcast (the Tapo discovery protocol on port 20002).
- **Corruption-proof.** Credentials live in the system keyring; the device list
  is a disposable cache that is rebuilt by `tapo discover` and written
  atomically. (The old C# version corrupted its JSON store under concurrent
  writes — that whole class of bug is gone.)
- **Quick Settings dropdown.** A GNOME tile that expands to a list of your plugs,
  each with a checkmark when it's on. Toggle one, or switch them all at once.

> **Why does it still need my TP-Link login if everything is local?**
> Tapo's *local* KLAP protocol authenticates you with your TP-Link account
> email/password — the account login doubles as the device password. Discovery
> is unauthenticated; turning plugs on/off is not. Nothing is sent to the cloud.

## Layout

| Path | What |
|------|------|
| `tapo-cli/` | Rust CLI (`tapo`) — discovery + control via the [`tapo`](https://crates.io/crates/tapo) crate |
| `gnome-extension/tapo@martinalderson.com/` | GNOME Shell quick-settings extension (GJS) |

## Build & install the CLI

Requires a recent Rust toolchain (edition 2021+; the `tapo` crate needs Rust ≥ 1.88).

```bash
cd tapo-cli
cargo build --release
install -Dm755 target/release/tapo ~/.local/bin/tapo   # ensure ~/.local/bin is on PATH
```

## First run

```bash
tapo login          # prompts for your TP-Link email + password; stored in the keyring
tapo discover       # broadcast-scan the LAN and cache the device list
tapo status         # live on/off state of every cached device
```

## Usage

```
tapo login [--username EMAIL --password PASS]   # store credentials + initial scan
tapo logout                                     # forget credentials
tapo discover [--timeout 5] [--target ADDR]     # rescan, refresh the cache
tapo list                                       # cached devices (no network)
tapo status                                     # live on/off state (parallel)
tapo on  <selector>                             # turn a device on
tapo off <selector>                             # turn a device off
tapo toggle <selector>                          # toggle a device
tapo all on | off                               # switch every controllable device
```

`<selector>` matches a device by nickname (case-insensitive), device id, MAC, or
IP. Add `--json` to any command for machine-readable output (this is what the
GNOME extension consumes). If a plug's IP changed (DHCP), `on/off/toggle`
auto-rescans once and retries.

## Install the GNOME extension

```bash
ln -sfn "$PWD/gnome-extension/tapo@martinalderson.com" \
   ~/.local/share/gnome-shell/extensions/tapo@martinalderson.com
gnome-extensions enable tapo@martinalderson.com
```

Log out and back in (Wayland can't hot-reload the shell), then open Quick
Settings: the **Tapo** tile appears. Click the arrow to expand the dropdown of
plugs, click a row to toggle it, click the tile body to switch all, and use
**Rescan network** after adding new plugs.

For development you can iterate without logging out using a nested shell:

```bash
dbus-run-session -- gnome-shell --nested --wayland
```

The extension shells out to the `tapo` binary (found on `PATH` or in
`~/.local/bin`) asynchronously, so it never blocks the shell — and it does no
crypto itself (GJS has no AES/RSA).

## Supported devices

Discovery lists every Tapo device on the network. On/off control currently
targets plugs (P100/P105 and the energy-monitoring P110/P115). Bulbs, light
strips, and power strips are discovered but not yet switchable from this tool.
