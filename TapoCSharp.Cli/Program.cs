using Spectre.Console.Cli;
using TapoCSharp.Cli.Commands;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("tapo");
    config.SetApplicationVersion("1.0.0");
    
    // Set up commands
    config.AddCommand<AuthCommand>("auth");
    
    config.AddBranch("devices", devices =>
    {
        devices.SetDescription("Manage Tapo devices");
        devices.SetDefaultCommand<DevicesCommand>();
        devices.AddCommand<ListDevicesCommand>("ls");
        devices.AddCommand<AddDeviceCommand>("add");
        devices.AddCommand<RemoveDeviceCommand>("rm");
        devices.AddCommand<ScanDevicesCommand>("scan");
    });
    
    config.AddCommand<OnCommand>("on");
    config.AddCommand<OffCommand>("off");
    config.AddCommand<StatusCommand>("status");
});

return await app.RunAsync(args);
