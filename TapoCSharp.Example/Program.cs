using TapoCSharp;

// Get credentials from environment variables
var username = Environment.GetEnvironmentVariable("TAPO_USERNAME")
    ?? throw new InvalidOperationException("TAPO_USERNAME environment variable not set");
var password = Environment.GetEnvironmentVariable("TAPO_PASSWORD")
    ?? throw new InvalidOperationException("TAPO_PASSWORD environment variable not set");
var ipAddress = Environment.GetEnvironmentVariable("IP_ADDRESS") ?? "192.168.4.27";

Console.WriteLine($"Connecting to Tapo power strip at {ipAddress}...");

try
{
    var client = new ApiClient(username, password);
    var strip = await client.P304Async(ipAddress);

    Console.WriteLine("Connected! Getting strip info...");
    var stripInfo = await strip.GetDeviceInfoAsync();
    Console.WriteLine($"Model: {stripInfo?["model"]}");

    Console.WriteLine("\nEnumerating child devices...");
    var sockets = await strip.GetChildDeviceListAsync();
    Console.WriteLine($"Found {sockets.Count} socket(s):");

    foreach (var socket in sockets)
    {
        Console.WriteLine($"  [{socket.Position}] {socket.Nickname,-20} on={socket.DeviceOn,-5} id={socket.DeviceId}");
    }

    // Individual sockets are controlled through the strip via control_child, e.g.:
    //
    //     var socket = await strip.GetSocketAsync("Monitors");   // or GetSocketAsync(3)
    //     await socket.OnAsync();
    //     await socket.OffAsync();
    //
    // Left commented out so running this example does not switch anything on or off.

    Console.WriteLine("\nExample completed successfully!");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine($"Stack trace: {ex}");
}
