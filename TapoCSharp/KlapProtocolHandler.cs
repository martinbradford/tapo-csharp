using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TapoCSharp;

/// <summary>
/// KLAP protocol implementation for newer Tapo devices.
/// </summary>
internal class KlapProtocolHandler : IProtocolHandler
{
    private readonly HttpClient _httpClient;
    private string? _sessionCookie;
    private string? _url;
    private KlapCipher? _cipher;

    public KlapProtocolHandler(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Performs the KLAP authentication handshake and login.
    /// </summary>
    public async Task LoginAsync(string url, string username, string password)
    {
        _url = url;
        
        // Generate local seed (16 random bytes)
        var localSeed = new byte[16];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(localSeed);
        }

        // Calculate auth_hash = SHA256(SHA1(username) + SHA1(password))
        var usernameHash = KlapCipher.Sha1(Encoding.UTF8.GetBytes(username));
        var passwordHash = KlapCipher.Sha1(Encoding.UTF8.GetBytes(password));
        var authHash = KlapCipher.Sha256(CombineArrays(usernameHash, passwordHash));

        // Perform handshake1: Send local_seed, receive remote_seed + server_hash
        var (remoteSeed, serverHash) = await Handshake1Async(url, localSeed, authHash);

        // Verify server_hash = SHA256(local_seed + remote_seed + auth_hash)
        var expectedServerHash = KlapCipher.Sha256(CombineArrays(localSeed, remoteSeed, authHash));
        if (!serverHash.SequenceEqual(expectedServerHash))
        {
            throw new InvalidOperationException("Server hash verification failed");
        }

        // Perform handshake2: Send client_hash = SHA256(remote_seed + local_seed + auth_hash)
        var clientHash = KlapCipher.Sha256(CombineArrays(remoteSeed, localSeed, authHash));
        await Handshake2Async(url, clientHash);

        // Initialize cipher with derived keys
        var localHash = CombineArrays(localSeed, remoteSeed, authHash);
        var (_, initialSequence) = KlapCipher.DeriveIV(localHash);
        _cipher = new KlapCipher(localSeed, remoteSeed, authHash, initialSequence);
    }

    /// <summary>
    /// Handshake1: Send local seed, receive remote seed and server hash.
    /// </summary>
    private async Task<(byte[] remoteSeed, byte[] serverHash)> Handshake1Async(string baseUrl, byte[] localSeed, byte[] authHash)
    {
        var handshakeUrl = baseUrl + "/handshake1";
        var content = new ByteArrayContent(localSeed);
        
        var request = new HttpRequestMessage(HttpMethod.Post, handshakeUrl)
        {
            Content = content
        };
        
        // Remove default content type headers and add Accept header to match Rust version
        request.Content.Headers.ContentType = null;
        request.Headers.Add("Accept", "*/*");
        
        // Remove User-Agent if HttpClient added one
        request.Headers.Remove("User-Agent");
        
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        
        // Store session cookie
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var sessionCookie = cookies.FirstOrDefault(c => c.StartsWith("TP_SESSIONID"));
            if (sessionCookie != null)
            {
                _sessionCookie = sessionCookie.Split(';')[0];
            }
        }

        var responseBytes = await response.Content.ReadAsByteArrayAsync();
        
        // Response should be 48 bytes: remote_seed (16) + server_hash (32)
        if (responseBytes.Length != 48)
        {
            throw new InvalidOperationException($"Handshake1 response length invalid: {responseBytes.Length}, expected 48");
        }

        var remoteSeed = new byte[16];
        var serverHash = new byte[32];
        
        Array.Copy(responseBytes, 0, remoteSeed, 0, 16);
        Array.Copy(responseBytes, 16, serverHash, 0, 32);

        return (remoteSeed, serverHash);
    }

    /// <summary>
    /// Handshake2: Send client hash to complete authentication.
    /// </summary>
    private async Task Handshake2Async(string baseUrl, byte[] clientHash)
    {
        var handshakeUrl = baseUrl + "/handshake2";
        var content = new ByteArrayContent(clientHash);
        
        var request = new HttpRequestMessage(HttpMethod.Post, handshakeUrl)
        {
            Content = content
        };
        
        // Remove default content type headers that HttpClient might add
        request.Content.Headers.ContentType = null;
        
        // Add Accept header to match Rust version
        request.Headers.Add("Accept", "*/*");
        
        // Remove User-Agent if HttpClient added one
        request.Headers.Remove("User-Agent");
        
        if (_sessionCookie != null)
        {
            request.Headers.Add("Cookie", _sessionCookie);
        }

        var response = await _httpClient.SendAsync(request);
        
        response.EnsureSuccessStatusCode();
        
        // Response should be empty or contain session info
        var responseBytes = await response.Content.ReadAsByteArrayAsync();
        // Some devices return additional data, but we just need the success status
    }

    /// <summary>
    /// Sets device information using encrypted request.
    /// </summary>
    public async Task SetDeviceInfoAsync(object deviceInfo)
    {
        if (_cipher == null)
            throw new InvalidOperationException("Must call LoginAsync first");

        var request = new
        {
            method = "set_device_info",
            @params = deviceInfo
        };

        await ExecuteRequestAsync<object>(request);
    }

    /// <summary>
    /// Gets device information using encrypted request.
    /// </summary>
    public async Task<JsonNode> GetDeviceInfoAsync()
    {
        if (_cipher == null)
            throw new InvalidOperationException("Must call LoginAsync first");

        var request = new
        {
            method = "get_device_info",
            @params = new { }
        };

        var response = await ExecuteRequestAsync<JsonNode>(request);
        return response ?? throw new InvalidOperationException("No response received");
    }

    /// <summary>
    /// Executes an arbitrary device method using an encrypted request.
    /// </summary>
    public async Task<JsonNode?> ExecuteMethodAsync(string method, object parameters)
    {
        if (_cipher == null)
            throw new InvalidOperationException("Must call LoginAsync first");

        var request = new
        {
            method,
            @params = parameters
        };

        return await ExecuteRequestAsync<JsonNode>(request);
    }

    /// <summary>
    /// Executes an encrypted request using the KLAP protocol.
    /// </summary>
    private async Task<T?> ExecuteRequestAsync<T>(object request)
    {
        if (_cipher == null || _url == null)
            throw new InvalidOperationException("Must call LoginAsync first");

        var requestJson = JsonSerializer.Serialize(request);
        var (encryptedPayload, sequence) = _cipher.Encrypt(requestJson);

        var requestUrl = _url + $"/request?seq={sequence}";
        var content = new ByteArrayContent(encryptedPayload);
        
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUrl)
        {
            Content = content
        };
        
        if (_sessionCookie != null)
        {
            httpRequest.Headers.Add("Cookie", _sessionCookie);
        }

        var response = await _httpClient.SendAsync(httpRequest);
        response.EnsureSuccessStatusCode();

        var responseBytes = await response.Content.ReadAsByteArrayAsync();
        
        // Response format: signature (32 bytes) + encrypted_data
        if (responseBytes.Length < 32)
        {
            throw new InvalidOperationException("Response too short");
        }

        var encryptedData = new byte[responseBytes.Length - 32];
        Array.Copy(responseBytes, 32, encryptedData, 0, encryptedData.Length);

        var decryptedJson = _cipher.Decrypt(sequence, encryptedData);
        
        var responseObj = JsonNode.Parse(decryptedJson);
        var errorCode = responseObj?["error_code"]?.GetValue<int>() ?? 0;
        
        if (errorCode != 0)
        {
            var errorMsg = GetErrorMessage(errorCode);
            throw new TapoException(errorCode, errorMsg);
        }

        var result = responseObj?["result"];
        if (typeof(T) == typeof(JsonNode))
        {
            return (T?)(object?)result;
        }
        else if (result != null)
        {
            return JsonSerializer.Deserialize<T>(result.ToString());
        }

        return default(T);
    }

    /// <summary>
    /// Gets error message for error code.
    /// </summary>
    private static string GetErrorMessage(int errorCode)
    {
        return errorCode switch
        {
            0 => "Success",
            -1002 => "Invalid Request",
            -1003 => "Malformed Request",
            -1008 => "Invalid Parameters",
            -1501 => "Invalid Credentials",
            9999 => "Session Timeout",
            _ => $"Unknown error code: {errorCode}"
        };
    }

    /// <summary>
    /// Combines multiple byte arrays into one.
    /// </summary>
    private static byte[] CombineArrays(params byte[][] arrays)
    {
        var totalLength = arrays.Sum(arr => arr.Length);
        var result = new byte[totalLength];
        var offset = 0;
        
        foreach (var array in arrays)
        {
            Array.Copy(array, 0, result, offset, array.Length);
            offset += array.Length;
        }
        
        return result;
    }
}

/// <summary>
/// Exception thrown when Tapo device returns an error.
/// </summary>
public class TapoException : Exception
{
    public int ErrorCode { get; }
    
    public TapoException(int errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}