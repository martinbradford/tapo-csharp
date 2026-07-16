using System.ComponentModel;
using Spectre.Console.Cli;
using TapoCSharp.Cli.Services;

namespace TapoCSharp.Cli.Settings;

public class DeviceCommandSettings : CommandSettings
{
    [CommandArgument(0, "[device]")]
    [Description("Device IP address or name (omit for all devices)")]
    public string? Device { get; init; }
}

public class SocketCommandSettings : CommandSettings
{
    [CommandArgument(0, "<device>")]
    [Description("Device IP address or name")]
    public required string Device { get; init; }

    [CommandOption("-s|--socket")]
    [Description("Socket on a power strip, by name or position (e.g. --socket Monitors or --socket 3). A value that parses as a number is treated as a position.")]
    public string? Socket { get; init; }
}

public class StatusCommandSettings : DeviceCommandSettings
{
    [CommandOption("-s|--socket")]
    [Description("Show a single socket on a power strip, by name or position (e.g. --socket Monitors or --socket 3). A value that parses as a number is treated as a position.")]
    public string? Socket { get; init; }
}

public class ScanDevicesSettings : CommandSettings
{
    [CommandArgument(0, "[subnet]")]
    [Description("Any IP address on the subnet to scan (e.g. 192.168.4.1). Defaults to this machine's local network.")]
    public string? Subnet { get; init; }

    [CommandOption("-t|--timeout")]
    [Description("Milliseconds to wait for each host to respond. Raise this if a firewall prompts you to approve outgoing connections, so the scan waits rather than skipping the host.")]
    public int TimeoutMs { get; init; } = NetworkScanService.DefaultPortTimeoutMs;
}

public class AddDeviceSettings : CommandSettings
{
    [CommandArgument(0, "<ip>")]
    [Description("Device IP address")]
    public required string IpAddress { get; init; }
    
    [CommandOption("-n|--name")]
    [Description("Device name (optional)")]
    public string? Name { get; init; }
}

public class RemoveDeviceSettings : CommandSettings
{
    [CommandArgument(0, "<device>")]
    [Description("Device IP address or name to remove")]
    public required string Device { get; init; }
}