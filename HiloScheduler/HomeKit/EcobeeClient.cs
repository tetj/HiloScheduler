using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace HiloScheduler.HomeKit;

/// <summary>Controls the ecobee thermostat via the HomeKit Accessory Protocol (local network only).</summary>
public class EcobeeClient(IOptions<SchedulerOptions> options, ILogger<EcobeeClient> logger)
{
    private const string TargetTempUuid = "00000035-0000-1000-8000-0026BB765291";

    private readonly SchedulerOptions _opts = options.Value;

    public async Task<double> GetTargetTempAsync(CancellationToken ct = default)
    {
        var (aid, iid) = await FindCharacteristicAsync(ct);
        await using var session = await OpenSessionAsync(ct);
        var body = await session.GetAsync($"/characteristics?id={aid}.{iid}", ct);
        var value = ParseCharacteristicValue(body);
        logger.LogInformation("Current ecobee target temperature: {Temp}°C", value);
        return value;
    }

    public async Task SetTargetTempAsync(double temp, CancellationToken ct = default)
    {
        var (aid, iid) = await FindCharacteristicAsync(ct);
        await using var session = await OpenSessionAsync(ct);
        var payload = JsonSerializer.Serialize(new
        {
            characteristics = new[] { new { aid, iid, value = temp } }
        });
        await session.PutAsync("/characteristics", payload, ct);
        logger.LogInformation("ecobee target temperature set to {Temp}°C", temp);
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private (int Aid, int Iid)? _cachedChar;

    private async Task<(int Aid, int Iid)> FindCharacteristicAsync(CancellationToken ct)
    {
        if (_cachedChar.HasValue)
        {
            return _cachedChar.Value;
        }

        await using var session = await OpenSessionAsync(ct);
        var body        = await session.GetAsync("/accessories", ct);
        var result      = FindCharacteristic(body, TargetTempUuid);
        _cachedChar     = result;
        return result;
    }

    private async Task<HapSession> OpenSessionAsync(CancellationToken ct)
    {
        var pairing = LoadPairing();
        var session = await HapSession.ConnectAsync(pairing.AccessoryIp, pairing.AccessoryPort, ct);
        await PairVerify.PerformAsync(session, pairing, ct);
        return session;
    }

    private PairingRecord LoadPairing()
    {
        var path = _opts.ResolvePath(_opts.PairingFile);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Pairing file not found: {path}. Run --pair first.");
        }
        var file = JsonSerializer.Deserialize<PairingFile>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Failed to parse pairing file.");
        return file.Pairing;
    }

    private static (int Aid, int Iid) FindCharacteristic(string accessoriesJson, string uuid)
    {
        uuid = uuid.ToUpperInvariant();
        var headerEnd = accessoriesJson.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var json      = headerEnd >= 0 ? accessoriesJson[(headerEnd + 4)..] : accessoriesJson;
        var doc       = JsonDocument.Parse(json);

        foreach (var accessory in doc.RootElement.GetProperty("accessories").EnumerateArray())
        {
            var aid = accessory.GetProperty("aid").GetInt32();
            foreach (var service in accessory.GetProperty("services").EnumerateArray())
            {
                foreach (var ch in service.GetProperty("characteristics").EnumerateArray())
                {
                    var type = ch.GetProperty("type").GetString()?.ToUpperInvariant() ?? "";
                    // HAP UUIDs can be short ("35") or full ("00000035-0000-1000-8000-0026BB765291")
                    if (type == uuid || type.TrimStart('0') == uuid.TrimStart('0'))
                    {
                        return (aid, ch.GetProperty("iid").GetInt32());
                    }
                }
            }
        }
        throw new InvalidOperationException($"Characteristic {uuid} not found on ecobee. Try re-pairing.");
    }

    private static double ParseCharacteristicValue(string responseJson)
    {
        var headerEnd = responseJson.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var json      = headerEnd >= 0 ? responseJson[(headerEnd + 4)..] : responseJson;
        var doc       = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("characteristics")[0]
            .GetProperty("value")
            .GetDouble();
    }
}
