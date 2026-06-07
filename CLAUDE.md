# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Tapo is a Linux tool for controlling TP-Link Tapo smart plugs, in two parts:

1. **`tapo-cli/`** — a Rust CLI (binary name `tapo`) that discovers and controls
   devices on the local network, built on the [`tapo`](https://crates.io/crates/tapo) crate.
2. **`gnome-extension/tapo@martinalderson.com/`** — a GNOME Shell **Quick
   Settings** extension (GJS) that shells out to the `tapo` binary.

> History: this was originally a C# library/CLI. It was rebuilt in Rust because
> the C# JSON data store corrupted under concurrent writes and devices had to be
> added by IP. The old C# code is in git history before the `rust-rebuild-gnome`
> work.

## Architecture & key decisions

- **Discovery, not scanning.** Devices are found via the Tapo UDP broadcast
  protocol (port 20002) using the crate's `ApiClient::discover_devices`. No
  manual IPs, no brute-force scan, and the cloud API is **not** used (it can't
  return local IPs or control modern Tapo plugs).
- **Credentials in the system keyring** (GNOME keyring via the freedesktop
  Secret Service), through the `keyring` crate — never plaintext on disk. The
  TP-Link account login is also the local KLAP device password, so on/off/status
  need it; discovery does not.
- **Disposable, corruption-proof cache.** The device list lives at
  `~/.cache/tapo/devices.json`. Rules that prevent the old corruption:
  only `discover` rewrites it; `status` never writes; every write is atomic
  (temp file + rename); reads tolerate a missing/corrupt file (→ empty + hint).
- **Extension does no crypto/network itself** (GJS lacks AES/RSA). It runs
  `tapo … --json` via `Gio.Subprocess` asynchronously and parses the JSON.

## CLI (`tapo-cli/`)

- `src/main.rs` — clap CLI, command dispatch, human + `--json` output.
- `src/store.rs` — `Device` model, atomic cache load/save, selector `resolve`.
- `src/creds.rs` — keyring get/set/delete of `Credentials`.
- `src/ops.rs` — discovery, on/off/toggle, parallel `status`, broadcast-target
  detection, and re-discover-on-failure retry.

Commands: `login`, `logout`, `discover`, `list`, `status`, `on/off/toggle <selector>`,
`all on|off`. Every command accepts a global `--json`.

### Build / run

```bash
cd tapo-cli
cargo build --release
install -Dm755 target/release/tapo ~/.local/bin/tapo
cargo run -- status          # dev run
```

The `tapo` crate requires Rust ≥ 1.88 and uses the tokio async runtime.

## GNOME extension

ESM/GJS for GNOME 45+ (developed on GNOME 50). `extension.js` registers a
`QuickSettings.SystemIndicator` containing a `QuickMenuToggle`:
- main toggle = all plugs on/off; per-plug rows with `Ornament.CHECK` for on;
  a "Rescan network" action; refreshes via `tapo status` on menu open.
- custom symbolic icon at `icons/tapo-symbolic.svg`.

### Install / iterate

```bash
ln -sfn "$PWD/gnome-extension/tapo@martinalderson.com" \
   ~/.local/share/gnome-shell/extensions/tapo@martinalderson.com
gnome-extensions enable tapo@martinalderson.com
# Wayland needs a logout/login to load a new extension; for dev iterate with:
dbus-run-session -- gnome-shell --nested --wayland
```

When changing the extension API, the authoritative reference for the installed
shell is the bundled `quickSettings.js`, extractable with:
`gresource extract /usr/lib64/gnome-shell/libshell-*.so /org/gnome/shell/ui/quickSettings.js`.

## Conventions

- The selector for control commands matches nickname / device_id / MAC / IP.
- Prefer letting errors surface (no silent fallbacks); the cache read is the one
  deliberate exception (tolerant by design).
