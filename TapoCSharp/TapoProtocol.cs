using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Security.Cryptography;

namespace TapoCSharp;

/// <summary>
/// Handles the Tapo protocol communication.
/// This is a simplified implementation that will auto-discover the correct protocol.
/// </summary>
internal class TapoProtocol
{
    private readonly HttpClient _httpClient;
    private IProtocolHandler? _protocolHandler;
    
    public TapoProtocol(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }
    
    /// <summary>
    /// Logs in to the device and discovers the correct protocol.
    /// </summary>
    public async Task LoginAsync(string url, string username, string password)
    {
        // Try to discover which protocol to use
        _protocolHandler = await DiscoverProtocolAsync(url);
        
        // Login using the discovered protocol
        await _protocolHandler.LoginAsync(url, username, password);
    }
    
    /// <summary>
    /// Discovers whether to use Passthrough or KLAP protocol.
    /// </summary>
    private async Task<IProtocolHandler> DiscoverProtocolAsync(string url)
    {
        // Force KLAP since we know from Rust debug that it works
        return new KlapProtocolHandler(_httpClient);
        
        /*
        // Test component negotiation to see if Passthrough is supported
        var testRequest = new
        {
            method = "component_nego", 
            @params = new { }
        };
        
        var json = JsonSerializer.Serialize(testRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        
        try
        {
            var response = await _httpClient.PostAsync(url, content);
            var responseText = await response.Content.ReadAsStringAsync();
            var responseJson = JsonNode.Parse(responseText);
            
            var errorCode = responseJson?["error_code"]?.GetValue<int>() ?? 0;
            
            // If error code is 1003, Passthrough is NOT supported, use KLAP
            if (errorCode == 1003)
            {
                return new KlapProtocolHandler(_httpClient);
            }
            else
            {
                return new PassthroughProtocolHandler(_httpClient);
            }
        }
        catch
        {
            // Fall through to KLAP
        }
        
        return new KlapProtocolHandler(_httpClient);
        */
    }
    
    /// <summary>
    /// Sets device information.
    /// </summary>
    public async Task SetDeviceInfoAsync(object deviceInfo)
    {
        if (_protocolHandler == null)
            throw new InvalidOperationException("Must call LoginAsync first");
            
        await _protocolHandler.SetDeviceInfoAsync(deviceInfo);
    }
    
    /// <summary>
    /// Gets device information.
    /// </summary>
    public async Task<JsonNode> GetDeviceInfoAsync()
    {
        if (_protocolHandler == null)
            throw new InvalidOperationException("Must call LoginAsync first");

        return await _protocolHandler.GetDeviceInfoAsync();
    }

    /// <summary>
    /// Executes an arbitrary device method and returns its result.
    /// </summary>
    public async Task<JsonNode?> ExecuteMethodAsync(string method, object parameters)
    {
        if (_protocolHandler == null)
            throw new InvalidOperationException("Must call LoginAsync first");

        return await _protocolHandler.ExecuteMethodAsync(method, parameters);
    }
}

/// <summary>
/// Interface for different protocol handlers.
/// </summary>
internal interface IProtocolHandler
{
    Task LoginAsync(string url, string username, string password);
    Task SetDeviceInfoAsync(object deviceInfo);
    Task<JsonNode> GetDeviceInfoAsync();
    Task<JsonNode?> ExecuteMethodAsync(string method, object parameters);
}


