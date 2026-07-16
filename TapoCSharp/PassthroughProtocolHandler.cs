using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TapoCSharp;

/// <summary>
/// Passthrough protocol implementation for older Tapo devices.
/// This uses RSA key exchange and AES encryption.
/// </summary>
internal class PassthroughProtocolHandler : IProtocolHandler
{
    private readonly HttpClient _httpClient;
    private RSA? _rsa;
    private string? _sessionCookie;
    private TpLinkCipher? _cipher;
    private string? _token;
    private string? _url;

    public PassthroughProtocolHandler(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task LoginAsync(string url, string username, string password)
    {
        _url = url;
        
        // Generate RSA key pair
        _rsa = RSA.Create(1024);
        var publicKeyPem = _rsa.ExportRSAPublicKeyPem();
        var publicKey = publicKeyPem
            .Replace("-----BEGIN RSA PUBLIC KEY-----", "")
            .Replace("-----END RSA PUBLIC KEY-----", "")
            .Replace("\n", "")
            .Replace("\r", "");

        // Perform handshake
        await HandshakeAsync(url, publicKey);

        // Login with credentials
        await LoginWithCredentialsAsync(username, password);
    }

    private async Task HandshakeAsync(string url, string publicKey)
    {
        var request = new
        {
            method = "handshake",
            @params = new
            {
                key = publicKey,
                requestTimeMils = 0
            }
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content);
        var responseText = await response.Content.ReadAsStringAsync();
        var responseObj = JsonNode.Parse(responseText);

        var errorCode = responseObj?["error_code"]?.GetValue<int>() ?? 0;
        if (errorCode != 0)
        {
            throw new TapoException(errorCode, $"Handshake failed with error {errorCode}");
        }

        // Get encrypted key and session cookie
        var encryptedKey = responseObj?["result"]?["key"]?.GetValue<string>()
            ?? throw new InvalidOperationException("No encrypted key in handshake response");

        // Extract session cookie
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var sessionCookie = cookies.FirstOrDefault(c => c.StartsWith("TP_SESSIONID"));
            if (sessionCookie != null)
            {
                _sessionCookie = sessionCookie.Split(';')[0];
            }
        }

        if (_sessionCookie == null)
        {
            throw new InvalidOperationException("No session cookie received");
        }

        // Decrypt the key to get AES key and IV
        _cipher = DecodeHandshakeKey(encryptedKey);
    }

    private TpLinkCipher DecodeHandshakeKey(string encryptedKey)
    {
        if (_rsa == null)
            throw new InvalidOperationException("RSA not initialized");

        var encryptedBytes = Convert.FromBase64String(encryptedKey);
        var decryptedBytes = _rsa.Decrypt(encryptedBytes, RSAEncryptionPadding.Pkcs1);

        if (decryptedBytes.Length < 32)
            throw new InvalidOperationException("Decrypted key too short");

        var key = new byte[16];
        var iv = new byte[16];

        Array.Copy(decryptedBytes, 0, key, 0, 16);
        Array.Copy(decryptedBytes, 16, iv, 0, 16);

        return new TpLinkCipher(key, iv);
    }

    private async Task LoginWithCredentialsAsync(string username, string password)
    {
        if (_cipher == null || _sessionCookie == null || _url == null)
            throw new InvalidOperationException("Handshake must be completed first");

        // Encode credentials
        var encodedPassword = Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
        var emailHash = ComputeSha1Hash(username);
        var encodedEmail = Convert.ToBase64String(Encoding.UTF8.GetBytes(emailHash));

        var loginRequest = new
        {
            method = "login_device",
            @params = new
            {
                password = encodedPassword,
                username = encodedEmail
            },
            requestTimeMils = 0
        };

        var encryptedPayload = _cipher.Encrypt(JsonSerializer.Serialize(loginRequest));

        var secureRequest = new
        {
            method = "securePassthrough",
            @params = new
            {
                request = encryptedPayload
            }
        };

        var json = JsonSerializer.Serialize(secureRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = content
        };
        httpRequest.Headers.Add("Cookie", _sessionCookie);

        var response = await _httpClient.SendAsync(httpRequest);
        var responseText = await response.Content.ReadAsStringAsync();
        var responseObj = JsonNode.Parse(responseText);

        var encryptedResponse = responseObj?["result"]?["response"]?.GetValue<string>()
            ?? throw new InvalidOperationException("No encrypted response");

        var decryptedResponse = _cipher.Decrypt(encryptedResponse);
        var decryptedObj = JsonNode.Parse(decryptedResponse);

        var errorCode = decryptedObj?["error_code"]?.GetValue<int>() ?? 0;
        if (errorCode != 0)
        {
            throw new TapoException(errorCode, $"Login failed with error {errorCode}");
        }

        _token = decryptedObj?["result"]?["token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("No token in login response");
    }

    public async Task SetDeviceInfoAsync(object deviceInfo)
    {
        await ExecuteSecureCommandAsync(new
        {
            method = "set_device_info",
            @params = deviceInfo,
            requestTimeMils = 0,
            terminalUUID = Guid.NewGuid().ToString()
        });
    }

    public async Task<JsonNode> GetDeviceInfoAsync()
    {
        var result = await ExecuteSecureCommandAsync(new
        {
            method = "get_device_info",
            requestTimeMils = 0
        });

        return result ?? throw new InvalidOperationException("No device info received");
    }

    public async Task<JsonNode?> ExecuteMethodAsync(string method, object parameters)
    {
        return await ExecuteSecureCommandAsync(new
        {
            method,
            @params = parameters,
            requestTimeMils = 0,
            terminalUUID = Guid.NewGuid().ToString()
        });
    }

    private async Task<JsonNode?> ExecuteSecureCommandAsync(object command)
    {
        if (_cipher == null || _token == null || _sessionCookie == null || _url == null)
            throw new InvalidOperationException("Must complete login first");

        var url = $"{_url}?token={_token}";
        var encryptedPayload = _cipher.Encrypt(JsonSerializer.Serialize(command));

        var secureRequest = new
        {
            method = "securePassthrough",
            @params = new
            {
                request = encryptedPayload
            }
        };

        var json = JsonSerializer.Serialize(secureRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };
        httpRequest.Headers.Add("Cookie", _sessionCookie);

        var response = await _httpClient.SendAsync(httpRequest);
        var responseText = await response.Content.ReadAsStringAsync();
        var responseObj = JsonNode.Parse(responseText);

        var encryptedResponse = responseObj?["result"]?["response"]?.GetValue<string>()
            ?? throw new InvalidOperationException("No encrypted response");

        var decryptedResponse = _cipher.Decrypt(encryptedResponse);
        var decryptedObj = JsonNode.Parse(decryptedResponse);

        var errorCode = decryptedObj?["error_code"]?.GetValue<int>() ?? 0;
        if (errorCode != 0)
        {
            throw new TapoException(errorCode, GetErrorMessage(errorCode));
        }

        return decryptedObj?["result"];
    }

    private static string ComputeSha1Hash(string input)
    {
        using var sha1 = SHA1.Create();
        var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(input));
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }

    private static string GetErrorMessage(int errorCode)
    {
        return errorCode switch
        {
            0 => "Success",
            -1010 => "Invalid Public Key Length",
            -1012 => "Invalid terminalUUID",
            -1501 => "Invalid Request or Credentials",
            1002 => "Incorrect Request",
            -1003 => "JSON formatting error",
            _ => $"Unknown error code: {errorCode}"
        };
    }
}

/// <summary>
/// TP-Link cipher for AES encryption/decryption.
/// </summary>
internal class TpLinkCipher
{
    private readonly byte[] _key;
    private readonly byte[] _iv;

    public TpLinkCipher(byte[] key, byte[] iv)
    {
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _iv = iv ?? throw new ArgumentNullException(nameof(iv));
    }

    public string Encrypt(string data)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = _iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(data);
        var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        return Convert.ToBase64String(encryptedBytes).Replace("\r\n", "");
    }

    public string Decrypt(string data)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = _iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var encryptedBytes = Convert.FromBase64String(data);
        var decryptedBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);

        return Encoding.UTF8.GetString(decryptedBytes);
    }
}