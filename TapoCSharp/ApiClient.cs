using System.Text.Json;

namespace TapoCSharp;

/// <summary>
/// Tapo API Client for controlling TP-Link Tapo devices.
/// </summary>
public class ApiClient
{
    private readonly string _username;
    private readonly string _password;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Creates a new instance of ApiClient.
    /// </summary>
    /// <param name="username">Tapo username (email)</param>
    /// <param name="password">Tapo password</param>
    /// <param name="timeout">Connection timeout (default: 30 seconds)</param>
    public ApiClient(string username, string password, TimeSpan? timeout = null)
    {
        _username = username ?? throw new ArgumentNullException(nameof(username));
        _password = password ?? throw new ArgumentNullException(nameof(password));
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        
        var handler = new HttpClientHandler()
        {
            UseCookies = false, // Disable automatic cookie handling

            // Tapo devices speak plain HTTP on /app and never redirect. Following
            // redirects only matters when probing a non-Tapo device that bounces
            // HTTP to HTTPS, where it costs a pointless TLS handshake against a
            // device that was never going to answer. Fail fast instead.
            AllowAutoRedirect = false
        };
        
        _httpClient = new HttpClient(handler)
        { 
            Timeout = _timeout 
        };
    }

    /// <summary>
    /// Creates a P100 plug handler for the specified IP address.
    /// </summary>
    /// <param name="ipAddress">Device IP address</param>
    /// <returns>Authenticated P100PlugHandler</returns>
    public async Task<P100PlugHandler> P100Async(string ipAddress)
    {
        var handler = new P100PlugHandler(_username, _password, ipAddress, _httpClient);
        await handler.LoginAsync();
        return handler;
    }

    /// <summary>
    /// Creates a P300 power strip handler for the specified IP address.
    /// </summary>
    /// <param name="ipAddress">Device IP address</param>
    /// <returns>Authenticated PowerStripHandler</returns>
    public async Task<PowerStripHandler> P300Async(string ipAddress)
    {
        return await PowerStripAsync(ipAddress);
    }

    /// <summary>
    /// Creates a P304 power strip handler for the specified IP address.
    /// </summary>
    /// <param name="ipAddress">Device IP address</param>
    /// <returns>Authenticated PowerStripHandler</returns>
    public async Task<PowerStripHandler> P304Async(string ipAddress)
    {
        return await PowerStripAsync(ipAddress);
    }

    private async Task<PowerStripHandler> PowerStripAsync(string ipAddress)
    {
        var handler = new PowerStripHandler(_username, _password, ipAddress, _httpClient);
        await handler.LoginAsync();
        return handler;
    }

    /// <summary>
    /// Releases resources used by the ApiClient.
    /// </summary>
    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}