using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using TapoCSharp.Cli.Services;
using TapoCSharp.Cli.Settings;

namespace TapoCSharp.Cli.Commands;

[Description("Turn a device off")]
public class OffCommand : AsyncCommand<SocketCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SocketCommandSettings settings)
    {
        try
        {
            var configService = new ConfigService();
            var deviceService = new DeviceService(configService);

            var target = string.IsNullOrWhiteSpace(settings.Socket)
                ? $"Device '{settings.Device}'"
                : $"Socket '{settings.Socket}' on '{settings.Device}'";

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Turning off {settings.Device}...", async ctx =>
                {
                    if (string.IsNullOrWhiteSpace(settings.Socket))
                    {
                        var device = await deviceService.ConnectToDeviceAsync(settings.Device);
                        if (device != null)
                            await device.OffAsync();
                    }
                    else
                    {
                        var socket = await deviceService.ConnectToSocketAsync(settings.Device, settings.Socket);
                        await socket.OffAsync();
                    }
                });

            AnsiConsole.MarkupLine($"[green]✓[/] {target} turned off successfully!");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Error: {ex.Message}[/]");
            return 1;
        }
    }
}