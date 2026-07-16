using System.ComponentModel;
using System.Text.Json.Nodes;
using Spectre.Console;
using Spectre.Console.Cli;
using TapoCSharp.Cli.Models;
using TapoCSharp.Cli.Services;
using TapoCSharp.Cli.Settings;

namespace TapoCSharp.Cli.Commands;

[Description("Configure Tapo authentication credentials")]
public class AuthCommand : Command<GlobalSettings>
{
    public override int Execute(CommandContext context, GlobalSettings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(CommandContext context, GlobalSettings settings)
    {
        try
        {
            var configService = new ConfigService();
            var deviceService = new DeviceService(configService);

            AnsiConsole.Write(new FigletText("Tapo Auth").Centered().Color(Color.Blue));
            AnsiConsole.WriteLine();

            // Check if auth already exists
            var existingAuth = await configService.LoadAuthConfigAsync();
            if (existingAuth != null)
            {
                var overwrite = AnsiConsole.Confirm($"Authentication is already configured for [green]{existingAuth.Username}[/]. Overwrite?");
                if (!overwrite)
                {
                    AnsiConsole.MarkupLine("[yellow]Authentication configuration cancelled.[/]");
                    return 0;
                }
            }

            // Prompt for credentials
            var username = AnsiConsole.Ask<string>("Enter your Tapo [blue]username/email[/]:");
            var password = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter your Tapo [blue]password[/]:")
                    .Secret());

            // Save credentials immediately
            var authConfig = new AuthConfig
            {
                Username = username,
                Password = password
            };

            await configService.SaveAuthConfigAsync(authConfig);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[green]✓[/] Authentication credentials saved!");

            // Ask for an IP to test and scan network
            AnsiConsole.WriteLine();
            var knownIp = AnsiConsole.Ask<string>("Enter the IP address of [blue]any Tapo device[/] on your network:");
            
            // Test the known device first
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Testing credentials...", async ctx =>
                {
                    try
                    {
                        var (success, model, _, error) = await deviceService.TestDeviceConnectionAsync(knownIp);
                        if (!success)
                        {
                            throw new InvalidOperationException($"Failed to connect to device: {error}");
                        }
                        
                        AnsiConsole.MarkupLine($"[green]✓[/] Successfully connected to {model} at {knownIp}");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] Credential test failed: {ex.Message}");
                        throw;
                    }
                });

            // Scan network for other devices
            AnsiConsole.WriteLine();
            bool scanNetwork = AnsiConsole.Confirm("Scan network for other Tapo devices?", defaultValue: true);
            
            if (scanNetwork)
            {
                var networkScanService = new NetworkScanService(deviceService);
                var foundDevices = new List<(string ip, string model, string nickname)>();
                
                await AnsiConsole.Progress()
                    .StartAsync(async ctx =>
                    {
                        var task = ctx.AddTask("Scanning network...", maxValue: 254);
                        var progress = new Progress<string>(msg =>
                        {
                            task.Description = msg;
                            task.Increment(1);
                        });
                        
                        foundDevices = await networkScanService.ScanSubnetAsync(knownIp, progress);
                        task.Value = task.MaxValue;
                    });

                AnsiConsole.WriteLine();
                if (foundDevices.Any())
                {
                    AnsiConsole.MarkupLine($"[green]Found {foundDevices.Count} Tapo device(s):[/]");
                    
                    var table = new Table();
                    table.AddColumn("IP Address");
                    table.AddColumn("Model");
                    table.AddColumn("Name");

                    foreach (var device in foundDevices)
                    {
                        table.AddRow(device.ip, device.model, Markup.Escape(device.nickname));
                    }

                    AnsiConsole.Write(table);

                    bool addDevices = AnsiConsole.Confirm("Add devices to configuration?", defaultValue: true);
                    if (addDevices)
                    {
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine("[blue]Adding devices with their actual names...[/]");

                        foreach (var device in foundDevices)
                        {
                            // The scan already reported each device's own nickname; fall back to a
                            // generated name only when the device has none set.
                            var deviceName = device.nickname != "Unnamed Device"
                                ? device.nickname
                                : $"{device.model.Replace("Tapo ", "")} {device.ip.Split('.').Last()}";

                            await configService.AddDeviceAsync(new DeviceConfig
                            {
                                Name = deviceName,
                                IpAddress = device.ip,
                                Model = device.model
                            });
                        }

                        AnsiConsole.MarkupLine($"[green]✓[/] Added {foundDevices.Count} device(s) to your configuration!");
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]No additional Tapo devices found on the network.[/]");
                }
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Configuration stored in [dim]~/.tapo/[/]");

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[red]✗ Error: {ex.Message}[/]");
            return 1;
        }
    }
}