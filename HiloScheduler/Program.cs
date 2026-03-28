using HiloScheduler;
using HiloScheduler.Hilo;
using HiloScheduler.HomeKit;
using Microsoft.Extensions.Options;
using System.Text.Json;

// --login: interactive one-time Hilo authentication
if (args.Contains("--login"))
{
    var loginHost = Host.CreateDefaultBuilder(args)
        .ConfigureServices((ctx, services) =>
        {
            services.Configure<SchedulerOptions>(ctx.Configuration.GetSection("Scheduler"));
            services.AddHttpClient<HiloClient>();
        })
        .Build();
    await loginHost.Services.GetRequiredService<HiloClient>().InteractiveLoginAsync();
    return;
}

// --pair: interactive one-time ecobee HomeKit pairing
if (args.Contains("--pair"))
{
    var pairHost = Host.CreateDefaultBuilder(args)
        .ConfigureServices((ctx, services) =>
        {
            services.Configure<SchedulerOptions>(ctx.Configuration.GetSection("Scheduler"));
        })
        .Build();

    var opts = pairHost.Services.GetRequiredService<IOptions<SchedulerOptions>>().Value;

    var pinArg = GetArg(args, "--pin");
    var ipArg  = GetArg(args, "--ip");

    Console.WriteLine("\n=== ecobee HomeKit Pairing ===");
    Console.WriteLine("Make sure HomeKit is enabled on the ecobee:");
    Console.WriteLine("  Thermostat screen ->> Menu ->> Settings ->> HomeKit ->> Enable");
    Console.WriteLine(">>> Once enabled, your ecobee screen will show a PIN. <<<\n");

    Console.WriteLine($"Discovering HomeKit device{(ipArg is not null ? $" at {ipArg}" : "")} via mDNS...");
    var device = await MdnsDiscovery.FindAsync(filterIp: ipArg);

    if (pinArg is null)
    {
        Console.Write("Enter the PIN shown on the ecobee screen (format: XXX-XX-XXX): ");
        pinArg = Console.ReadLine()?.Trim();
    }

    var pin = pinArg!.Replace("-", "");
    if (pin.Length == 8 && pin.All(char.IsDigit))
    {
        pin = $"{pin[..3]}-{pin[3..5]}-{pin[5..]}";
    }
    Console.WriteLine($"Using PIN: {pin}");

    var pairing = await PairSetup.PerformAsync(device.Host, device.Port, pin);
    var file = new PairingFile("ecobee", pairing);
    var path = opts.ResolvePath(opts.PairingFile);
    File.WriteAllText(path, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"\nOK Paired successfully! Credentials saved to '{path}'.");
    Console.WriteLine("You can now install and start the service.");
    return;
}

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options => options.ServiceName = "HiloScheduler")
    .ConfigureServices((ctx, services) =>
    {
        services.Configure<SchedulerOptions>(ctx.Configuration.GetSection("Scheduler"));
        services.AddHttpClient<HiloClient>();
        services.AddSingleton<EcobeeClient>();
        services.AddHostedService<Worker>();
    })
    .Build();

await host.RunAsync();

static string? GetArg(string[] args, string flag)
{
    var idx = Array.IndexOf(args, flag);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}
