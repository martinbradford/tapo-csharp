# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

TapoCSharp is a C# library and CLI tool for controlling TP-Link Tapo smart devices: single plugs (P100/P105/P110), bulbs (L510), and multi-socket power strips (P300/P304), whose sockets are controlled individually as child devices. It contains both KLAP and Passthrough protocol implementations.

## Build & Development Commands

```bash
# Build the entire solution
dotnet build

# Run the CLI tool in development
dotnet run --project TapoCSharp.Cli -- --help

# Run the example project (requires environment variables)
TAPO_USERNAME="user@email.com" TAPO_PASSWORD="password" IP_ADDRESS="192.168.0.250" dotnet run --project TapoCSharp.Example

# Build release CLI for Linux x64
dotnet publish TapoCSharp.Cli -c Release --self-contained -p:PublishSingleFile=true -r linux-x64

# Build for other platforms
dotnet publish TapoCSharp.Cli -c Release --self-contained -p:PublishSingleFile=true -r win-x64
dotnet publish TapoCSharp.Cli -c Release --self-contained -p:PublishSingleFile=true -r osx-x64
dotnet publish TapoCSharp.Cli -c Release --self-contained -p:PublishSingleFile=true -r osx-arm64
dotnet publish TapoCSharp.Cli -c Release --self-contained -p:PublishSingleFile=true -r linux-arm64
```

## Solution Structure

The solution contains three main projects:

- **TapoCSharp** - Core library containing protocol implementations
- **TapoCSharp.Cli** - CLI application using Spectre.Console
- **TapoCSharp.Example** - Simple console example

## Core Library Architecture

### Protocol Layer
- **TapoProtocol.cs** - Protocol orchestrator; also defines `IProtocolHandler`, the seam every protocol implements (LoginAsync, SetDeviceInfoAsync, GetDeviceInfoAsync, ExecuteMethodAsync)
- **KlapProtocolHandler.cs** - Modern KLAP protocol (AES encryption) for newer devices
- **PassthroughProtocolHandler.cs** - Legacy RSA-based protocol for older firmware
- **KlapCipher.cs** - Cryptographic utilities for KLAP protocol

### Device Layer  
- **ApiClient.cs** - Main entry point, creates authenticated device handlers
- **P100PlugHandler.cs** - Single-plug methods (OnAsync, OffAsync, GetDeviceInfoAsync)
- **PowerStripHandler.cs** - P300/P304 strips: enumerates sockets and exposes each as a `PowerStripSocket` with its own OnAsync/OffAsync/GetDeviceInfoAsync

## CLI Architecture

The CLI uses Spectre.Console.Cli with the following structure:

### Commands
- `tapo auth` - Configure authentication credentials
- `tapo devices ls` - List configured devices  
- `tapo devices add <ip> --name <name>` - Add device
- `tapo devices rm <name>` - Remove device
- `tapo devices scan [subnet] [--timeout <ms>]` - Find devices on the network; the subnet defaults to the local network
- `tapo on <device> [--socket <name|position>]` - Turn a device, or one power strip socket, on
- `tapo off <device> [--socket <name|position>]` - Turn a device, or one power strip socket, off
- `tapo status [device] [--socket <name|position>]` - Get device status; omit the device for all configured devices

`--socket` accepts a socket nickname or its position on the strip; a value that parses as a number is treated as a position.

### CLI Components
- **Commands/** - Command implementations using Spectre.Console.Cli
- **Services/** - ConfigService (manages ~/.tapo/ config), DeviceService (device operations)
- **Models/** - AuthConfig, DeviceConfig for configuration data
- **Settings/** - Command-line argument settings classes

## Key Design Patterns

### Protocol Selection
`TapoProtocol.DiscoverProtocolAsync` currently returns KLAP unconditionally — the component negotiation that would fall back to Passthrough is written but commented out. `PassthroughProtocolHandler` is therefore complete but never selected at runtime; keep it working when changing `IProtocolHandler`, but do not assume a device is reaching it.

Devices must have "Third-Party Compatibility" enabled in the Tapo app, or KLAP login fails at the handshake with a server hash verification error. That error means the credentials or the auth hash do not match — it is not a network fault.

### Child Devices (Power Strips)
A P300/P304 does not switch at the device level: each socket is a child device with its own `device_id`. `PowerStripHandler` enumerates them via `get_child_device_list` (paging on the `sum` field), and `PowerStripSocket` addresses each one with:

```
control_child { device_id, requestData: multipleRequest { requests: [ <the real request> ] } }
```

The socket's own `error_code` is nested at `responseData.result.responses[0]` and must be unwrapped and checked — the outer response reports success even when the child call failed. Two details worth knowing: socket positions are **1-based**, and a strip has no nickname of its own (only its sockets do), so `devices add` will prompt for a name.

Wire formats follow the Rust `tapo` crate (mihai-dinculescu/tapo), which is the de facto reference. Verify any new envelope against real hardware — TP-Link does not document these, and they vary by firmware.

### Configuration Management
CLI stores credentials in `~/.tapo/auth.json` and the device list in `~/.tapo/devices.json`. The password is stored **in plaintext**; `AuthConfig.IsEncrypted` exists but nothing sets or honours it. File modes are restricted on Linux/macOS only — no equivalent is applied on Windows. Never echo the stored password when debugging; read it into an environment variable instead.

### Error Handling
Both library and CLI use structured error handling with meaningful exceptions for authentication failures, network issues, and protocol errors.

## Environment Variables

For testing and examples:
- `TAPO_USERNAME` - Tapo account email
- `TAPO_PASSWORD` - Tapo account password  
- `IP_ADDRESS` - Device IP address

## Dependencies

Core library uses minimal dependencies:
- System.Text.Json for JSON handling
- Built-in System.Security.Cryptography for encryption

CLI additionally uses:
- Spectre.Console for rich terminal UI
- Spectre.Console.Cli for command-line parsing