using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Spectre.Console;

namespace TapoCSharp.Cli.Services;

public class NetworkScanService
{
    private readonly DeviceService _deviceService;

    public NetworkScanService(DeviceService deviceService)
    {
        _deviceService = deviceService;
    }

    /// <summary>
    /// Default per-host connect timeout. Generous rather than tight: an interactive
    /// firewall may hold the first connections while it waits for the user to allow
    /// them, and anything shorter silently drops those hosts from the results.
    /// </summary>
    public const int DefaultPortTimeoutMs = 2000;

    /// <summary>
    /// Scans an entire /24 subnet for Tapo devices.
    /// </summary>
    /// <param name="subnetIp">Any address on the subnet to scan. When null, the local network is used.</param>
    /// <param name="progress">Receives scan progress messages.</param>
    /// <param name="portTimeoutMs">How long to wait for each host to accept a connection.</param>
    public async Task<List<(string ip, string model, string nickname)>> ScanSubnetAsync(
        string? subnetIp = null,
        IProgress<string>? progress = null,
        int portTimeoutMs = DefaultPortTimeoutMs)
    {
        var foundDevices = new List<(string ip, string model, string nickname)>();

        var target = subnetIp ?? GetLocalAddress()
            ?? throw new InvalidOperationException(
                "Could not determine the local network. Pass an address on the subnet to scan, e.g. 'tapo devices scan 192.168.1.1'.");

        if (!IPAddress.TryParse(target, out var ipAddress) || ipAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException($"Invalid IPv4 address: {target}");
        }

        var ipBytes = ipAddress.GetAddressBytes();
        var subnet = $"{ipBytes[0]}.{ipBytes[1]}.{ipBytes[2]}";

        progress?.Report($"Scanning entire subnet {subnet}.0/24 for Tapo devices...");
        
        // First pass: scan for hosts with port 80 open
        var hostsWithPort80 = new List<string>();
        var portScanTasks = new List<Task>();
        var portScanSemaphore = new SemaphoreSlim(50); // More aggressive for port scanning
        
        for (int hostNum = 1; hostNum <= 254; hostNum++)
        {
            int currentHost = hostNum; // Capture for closure
            portScanTasks.Add(Task.Run(async () =>
            {
                await portScanSemaphore.WaitAsync();
                try
                {
                    var testIp = $"{subnet}.{currentHost}";
                    progress?.Report($"Port scanning {testIp}:80...");
                    
                    if (await IsPortOpenAsync(testIp, 80, portTimeoutMs))
                    {
                        lock (hostsWithPort80)
                        {
                            hostsWithPort80.Add(testIp);
                        }
                        progress?.Report($"✓ Port 80 open on {testIp}");
                    }
                }
                catch
                {
                    // Ignore errors during scanning
                }
                finally
                {
                    portScanSemaphore.Release();
                }
            }));
        }
        
        await Task.WhenAll(portScanTasks);
        progress?.Report($"Found {hostsWithPort80.Count} hosts with port 80 open, testing for Tapo devices...");
        
        // Second pass: test Tapo connectivity on hosts with port 80 open
        var tapoTestTasks = new List<Task>();
        var tapoTestSemaphore = new SemaphoreSlim(5); // Conservative for actual Tapo connections
        
        foreach (var ip in hostsWithPort80)
        {
            tapoTestTasks.Add(Task.Run(async () =>
            {
                await tapoTestSemaphore.WaitAsync();
                try
                {
                    progress?.Report($"Testing Tapo connection to {ip}...");

                    var (success, model, nickname, _) = await _deviceService.TestDeviceConnectionAsync(ip);
                    if (success && !string.IsNullOrEmpty(model))
                    {
                        lock (foundDevices)
                        {
                            foundDevices.Add((ip, model, nickname ?? "Unnamed Device"));
                        }
                        progress?.Report($"✓ Found {model} at {ip}");
                    }
                }
                catch
                {
                    // Ignore errors during scanning
                }
                finally
                {
                    tapoTestSemaphore.Release();
                }
            }));
        }
        
        await Task.WhenAll(tapoTestTasks);
        
        return foundDevices.OrderBy(d => IPAddress.Parse(d.ip).GetAddressBytes()[3]).ToList();
    }

    /// <summary>
    /// Finds this machine's IPv4 address on the local network. Adapters without a
    /// gateway (WSL, Hyper-V, VPN bridges) are skipped, since their subnets hold no
    /// Tapo devices.
    /// </summary>
    private static string? GetLocalAddress()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            var properties = nic.GetIPProperties();

            var hasGateway = properties.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork &&
                !g.Address.Equals(IPAddress.Any));

            if (!hasGateway)
            {
                continue;
            }

            var address = properties.UnicastAddresses.FirstOrDefault(a =>
                a.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(a.Address));

            if (address != null)
            {
                return address.Address.ToString();
            }
        }

        return null;
    }

    private async Task<bool> IsPortOpenAsync(string ipAddress, int port, int timeoutMs)
    {
        try
        {
            using var tcpClient = new TcpClient();
            var connectTask = tcpClient.ConnectAsync(ipAddress, port);
            var timeoutTask = Task.Delay(timeoutMs);

            var completedTask = await Task.WhenAny(connectTask, timeoutTask);

            if (completedTask == connectTask && tcpClient.Connected)
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}