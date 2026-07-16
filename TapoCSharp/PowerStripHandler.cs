using System.Text;
using System.Text.Json.Nodes;

namespace TapoCSharp;

/// <summary>
/// Handler for multi-socket power strips such as the P300 and P304M.
/// The strip itself is addressed directly, while each socket is a child device
/// reached through the control_child method.
/// </summary>
public class PowerStripHandler
{
    private readonly string _username;
    private readonly string _password;
    private readonly string _ipAddress;
    private readonly TapoProtocol _protocol;

    internal PowerStripHandler(string username, string password, string ipAddress, HttpClient httpClient)
    {
        _username = username;
        _password = password;
        _ipAddress = ipAddress;
        _protocol = new TapoProtocol(httpClient);
    }

    /// <summary>
    /// Authenticates with the device.
    /// </summary>
    internal async Task LoginAsync()
    {
        await _protocol.LoginAsync($"http://{_ipAddress}/app", _username, _password);
    }

    /// <summary>
    /// Gets information about the power strip itself.
    /// </summary>
    /// <returns>Device information as JSON</returns>
    public async Task<JsonNode> GetDeviceInfoAsync()
    {
        return await _protocol.GetDeviceInfoAsync();
    }

    /// <summary>
    /// Gets the sockets attached to this power strip, ordered by position.
    /// </summary>
    public async Task<IReadOnlyList<PowerStripSocket>> GetChildDeviceListAsync()
    {
        var sockets = new List<PowerStripSocket>();
        var startIndex = 0;

        while (true)
        {
            var result = await _protocol.ExecuteMethodAsync("get_child_device_list", new { start_index = startIndex })
                ?? throw new InvalidOperationException("No result returned for get_child_device_list");

            var children = result["child_device_list"]?.AsArray();
            if (children == null || children.Count == 0)
            {
                break;
            }

            foreach (var child in children)
            {
                if (child != null)
                {
                    sockets.Add(new PowerStripSocket(child, _protocol));
                }
            }

            // "sum" is the total number of children; keep paging until we have them all.
            var total = result["sum"]?.GetValue<int>() ?? sockets.Count;
            if (sockets.Count >= total)
            {
                break;
            }

            startIndex = sockets.Count;
        }

        return sockets.OrderBy(s => s.Position).ToList();
    }

    /// <summary>
    /// Gets the socket at the given position on the strip. Positions are 1-based,
    /// matching the numbering printed on the device.
    /// </summary>
    public async Task<PowerStripSocket> GetSocketAsync(int position)
    {
        var sockets = await GetChildDeviceListAsync();
        return sockets.FirstOrDefault(s => s.Position == position)
            ?? throw new InvalidOperationException(
                $"No socket at position {position}. Available positions: {string.Join(", ", sockets.Select(s => s.Position))}");
    }

    /// <summary>
    /// Gets the socket with the given nickname, as set in the Tapo app (case-insensitive).
    /// </summary>
    public async Task<PowerStripSocket> GetSocketAsync(string nickname)
    {
        var sockets = await GetChildDeviceListAsync();
        return sockets.FirstOrDefault(s => string.Equals(s.Nickname, nickname, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No socket named '{nickname}'. Available sockets: {string.Join(", ", sockets.Select(s => s.Nickname))}");
    }

    /// <summary>
    /// Refreshes the authentication session.
    /// </summary>
    public async Task RefreshSessionAsync()
    {
        await LoginAsync();
    }
}

/// <summary>
/// A single socket on a power strip. Commands are addressed to the socket's
/// device_id and forwarded by the strip.
/// </summary>
public class PowerStripSocket
{
    private readonly TapoProtocol _protocol;

    internal PowerStripSocket(JsonNode raw, TapoProtocol protocol)
    {
        _protocol = protocol;
        Raw = raw;
        DeviceId = raw["device_id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Child device has no device_id");
        Position = raw["position"]?.GetValue<int>() ?? -1;
        Nickname = DecodeNickname(raw["nickname"]?.GetValue<string>());
        DeviceOn = raw["device_on"]?.GetValue<bool>() ?? false;
    }

    /// <summary>
    /// The child device identifier used to address this socket.
    /// </summary>
    public string DeviceId { get; }

    /// <summary>
    /// Position of this socket on the strip, 1-based to match the device's own numbering.
    /// </summary>
    public int Position { get; }

    /// <summary>
    /// The socket name set in the Tapo app.
    /// </summary>
    public string Nickname { get; }

    /// <summary>
    /// Whether the socket was on when this instance was retrieved.
    /// </summary>
    public bool DeviceOn { get; }

    /// <summary>
    /// The full child device entry as returned by the strip.
    /// </summary>
    public JsonNode Raw { get; }

    /// <summary>
    /// Turns this socket on.
    /// </summary>
    public async Task OnAsync()
    {
        await SetDeviceInfoAsync(new { device_on = true });
    }

    /// <summary>
    /// Turns this socket off.
    /// </summary>
    public async Task OffAsync()
    {
        await SetDeviceInfoAsync(new { device_on = false });
    }

    /// <summary>
    /// Gets current information for this socket.
    /// </summary>
    /// <returns>Socket information as JSON</returns>
    public async Task<JsonNode> GetDeviceInfoAsync()
    {
        var result = await ControlChildAsync(new { method = "get_device_info", @params = new { } });
        return result ?? throw new InvalidOperationException($"No device info returned for socket '{Nickname}'");
    }

    private async Task SetDeviceInfoAsync(object deviceInfo)
    {
        await ControlChildAsync(new { method = "set_device_info", @params = deviceInfo });
    }

    /// <summary>
    /// Wraps a request in a control_child call addressed to this socket and
    /// unwraps the socket's own response out of the strip's reply.
    /// </summary>
    private async Task<JsonNode?> ControlChildAsync(object childRequest)
    {
        var result = await _protocol.ExecuteMethodAsync("control_child", new
        {
            device_id = DeviceId,
            requestData = new
            {
                method = "multipleRequest",
                @params = new { requests = new[] { childRequest } }
            }
        });

        var responseData = result?["responseData"]
            ?? throw new InvalidOperationException($"control_child returned no responseData for socket '{Nickname}'");

        // The strip echoes the multipleRequest envelope back; the socket's own
        // response - including its own error_code - is inside.
        var childResponse = responseData["result"]?["responses"]?.AsArray()?.FirstOrDefault() ?? responseData;

        var errorCode = childResponse?["error_code"]?.GetValue<int>() ?? 0;
        if (errorCode != 0)
        {
            throw new TapoException(errorCode, $"Socket '{Nickname}' returned error code {errorCode}");
        }

        return childResponse?["result"];
    }

    private static string DecodeNickname(string? base64Nickname)
    {
        if (string.IsNullOrEmpty(base64Nickname))
        {
            return "Unnamed Socket";
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64Nickname));
        }
        catch (FormatException)
        {
            return base64Nickname;
        }
    }
}
