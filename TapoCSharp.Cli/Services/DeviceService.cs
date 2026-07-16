using System.Text;
using TapoCSharp;
using TapoCSharp.Cli.Models;

namespace TapoCSharp.Cli.Services;

public class DeviceService
{
    private readonly ConfigService _configService;

    public DeviceService(ConfigService configService)
    {
        _configService = configService;
    }

    public async Task<P100PlugHandler?> ConnectToDeviceAsync(string ipOrName)
    {
        var (client, ipAddress, device) = await ResolveTargetAsync(ipOrName);

        try
        {
            var plugHandler = await client.P100Async(ipAddress);
            await MarkSeenAsync(device);
            return plugHandler;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to connect to device at {ipAddress}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Connects to a power strip and resolves one of its sockets, addressed either
    /// by name or by position on the strip.
    /// </summary>
    public async Task<PowerStripSocket> ConnectToSocketAsync(string ipOrName, string socket)
    {
        var (client, ipAddress, device) = await ResolveTargetAsync(ipOrName);

        PowerStripHandler strip;
        try
        {
            strip = await client.P304Async(ipAddress);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to connect to device at {ipAddress}: {ex.Message}", ex);
        }

        // Socket lookup failures carry their own message listing what is available,
        // so they are deliberately not wrapped as connection errors.
        var resolved = int.TryParse(socket, out var position)
            ? await strip.GetSocketAsync(position)
            : await strip.GetSocketAsync(socket);

        await MarkSeenAsync(device);
        return resolved;
    }

    /// <summary>
    /// Resolves a device name or IP to an authenticated client and its address.
    /// </summary>
    private async Task<(ApiClient client, string ipAddress, DeviceConfig? device)> ResolveTargetAsync(string ipOrName)
    {
        var auth = await _configService.LoadAuthConfigAsync();
        if (auth == null)
        {
            throw new InvalidOperationException("Authentication not configured. Run 'tapo auth' first.");
        }

        var device = await _configService.FindDeviceAsync(ipOrName);
        var ipAddress = device?.IpAddress ?? ipOrName;

        // Validate IP address format
        if (!System.Net.IPAddress.TryParse(ipAddress, out _))
        {
            throw new ArgumentException($"Invalid IP address: {ipAddress}");
        }

        return (new ApiClient(auth.Username, auth.Password), ipAddress, device);
    }

    private async Task MarkSeenAsync(DeviceConfig? device)
    {
        if (device != null)
        {
            device.LastSeen = DateTime.UtcNow;
            await _configService.AddDeviceAsync(device);
        }
    }

    public async Task<(bool success, string? model, string? nickname, string? error)> TestDeviceConnectionAsync(string ipAddress)
    {
        try
        {
            var auth = await _configService.LoadAuthConfigAsync();
            if (auth == null)
            {
                return (false, null, null, "Authentication not configured");
            }

            var client = new ApiClient(auth.Username, auth.Password);
            var device = await client.P100Async(ipAddress);
            var deviceInfo = await device.GetDeviceInfoAsync();

            var model = deviceInfo?["model"]?.ToString();
            var nickname = DecodeNickname(deviceInfo?["nickname"]?.ToString());
            return (true, model, nickname, null);
        }
        catch (Exception ex)
        {
            return (false, null, null, ex.Message);
        }
    }

    public async Task<DeviceConfig[]> GetAllDevicesAsync()
    {
        var deviceList = await _configService.LoadDevicesAsync();
        return deviceList.Devices.ToArray();
    }

    /// <summary>
    /// Decodes the Base64 encoded nickname from device info
    /// </summary>
    public static string DecodeNickname(string? base64Nickname)
    {
        if (string.IsNullOrEmpty(base64Nickname))
            return "Unnamed Device";

        try
        {
            var bytes = Convert.FromBase64String(base64Nickname);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return base64Nickname; // Return as-is if not valid Base64
        }
    }
}