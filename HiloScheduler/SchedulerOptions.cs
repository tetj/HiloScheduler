namespace HiloScheduler;

public class SchedulerOptions
{
    public string PairingFile { get; set; } = "ecobee_pairing.json";
    public string TokenFile { get; set; } = "hilo_tokens.json";

    public double TempPreheatLow { get; set; } = 24.0;
    public double TempPreheatHigh { get; set; } = 25.0;
    public double TempReduction { get; set; } = 16.0;
    public int EventDurationMin { get; set; } = 240;
    public int PreheatTotalMin { get; set; } = 120;
    public int PreheatHighMin { get; set; } = 30;
    public int CheckIntervalHours { get; set; } = 4;

    public string ResolvePath(string relative) =>
        Path.IsPathRooted(relative) ? relative : Path.Combine(AppContext.BaseDirectory, relative);
}
