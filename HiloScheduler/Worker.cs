using HiloScheduler.Hilo;
using HiloScheduler.HomeKit;
using Microsoft.Extensions.Options;

namespace HiloScheduler;

public class Worker(
    ILogger<Worker> logger,
    IOptions<SchedulerOptions> options,
    HiloClient hilo,
    EcobeeClient ecobee) : BackgroundService
{
    private readonly SchedulerOptions _opts = options.Value;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            logger.LogInformation("=== Hilo event check ===");
            try
            {
                await RunOnceAsync(ct);
            }
            catch (FileNotFoundException ex)
            {
                logger.LogError(ex.Message);
                return;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("--login"))
            {
                logger.LogError("No Hilo tokens found. Run: HiloScheduler.exe --login");
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error during event check.");
            }

            var next = DateTime.Now.AddHours(_opts.CheckIntervalHours);
            logger.LogInformation("Next check at {Next} (sleeping {Hours}h)",
                next.ToString("yyyy-MM-dd HH:mm"), _opts.CheckIntervalHours);
            await Task.Delay(TimeSpan.FromHours(_opts.CheckIntervalHours), ct);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        await RunOnceInternalAsync(ct);
    }

    internal async Task RunOnceInternalAsync(CancellationToken ct)
    {
        var token      = await hilo.GetAccessTokenAsync(ct);
        var locationId = await hilo.GetLocationIdAsync(token, ct);
        var ev         = await hilo.GetNextEventAsync(token, locationId, ct);

        if (ev is null)
        {
            logger.LogInformation("No upcoming Hilo events found.");
            return;
        }

        logger.LogInformation("Next Hilo event: {Start:yyyy-MM-dd HH:mm} UTC", ev.Start);
        await HandleEventAsync(ev, ct);
    }

    private async Task HandleEventAsync(HiloEvent ev, CancellationToken ct)
    {
        var start       = ev.Start.UtcDateTime;
        var end         = start.AddMinutes(_opts.EventDurationMin);
        var preheatLow  = start.AddMinutes(-_opts.PreheatTotalMin);
        var preheatHigh = start.AddMinutes(-_opts.PreheatHighMin);
        var now         = DateTime.UtcNow;

        logger.LogInformation(
            "Event schedule:\n" +
            "  {PreheatLow:HH:mm} UTC  ->>  Preheat low  ({Low}°C)\n" +
            "  {PreheatHigh:HH:mm} UTC  ->>  Preheat high ({High}°C)\n" +
            "  {Start:HH:mm} UTC  ->>  Reduction    ({Reduction}°C)\n" +
            "  {End:HH:mm} UTC  ->>  Recovery     (restore saved)",
            preheatLow, _opts.TempPreheatLow,
            preheatHigh, _opts.TempPreheatHigh,
            start, _opts.TempReduction,
            end);

        if (preheatLow > now)
        {
            await SleepUntilAsync(preheatLow, "PREHEAT LOW", ct);
        }
        else
        {
            logger.LogInformation("[PREHEAT LOW] Already in preheat window.");
        }

        var saved = await ecobee.GetTargetTempAsync(ct);
        logger.LogInformation("Saved setpoint: {Saved}°C (will restore after event)", saved);
        await ecobee.SetTargetTempAsync(_opts.TempPreheatLow, ct);

        if (preheatHigh > DateTime.UtcNow)
        {
            await SleepUntilAsync(preheatHigh, "PREHEAT HIGH", ct);
            await ecobee.SetTargetTempAsync(_opts.TempPreheatHigh, ct);
        }
        else
        {
            logger.LogInformation("[PREHEAT HIGH] Skipped (already past this window).");
        }

        if (start > DateTime.UtcNow)
        {
            await SleepUntilAsync(start, "REDUCTION", ct);
        }
        await ecobee.SetTargetTempAsync(_opts.TempReduction, ct);

        await SleepUntilAsync(end, "RECOVERY", ct);
        await ecobee.SetTargetTempAsync(saved, ct);
        logger.LogInformation("Event complete. Temperature restored to {Saved}°C.", saved);
    }

    private async Task SleepUntilAsync(DateTime target, string label, CancellationToken ct)
    {
        var delay = target - DateTime.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
            logger.LogInformation("[{Label}] Time already passed — acting immediately.", label);
            return;
        }
        logger.LogInformation("[{Label}] Waiting {Minutes:F1} min until {Target:HH:mm} UTC...",
            label, delay.TotalMinutes, target);
        await Task.Delay(delay, ct);
    }
}
