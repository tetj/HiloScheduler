using System.Net;
using System.Net.Sockets;
using System.Text;
using Makaretu.Dns;

namespace HiloScheduler.HomeKit;

public record HomeKitDevice(string Name, string Host, int Port, string Id, int FeatureFlags);

/// <summary>Discovers HomeKit devices on the local network via mDNS (_hap._tcp.local.).</summary>
public static class MdnsDiscovery
{
    public static async Task<HomeKitDevice> FindAsync(string? filterIp = null, int timeoutSeconds = 15, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<HomeKitDevice>();
        using var mdns = new MulticastService();
        using var sd   = new ServiceDiscovery(mdns);

        sd.ServiceInstanceDiscovered += (sender, e) =>
        {
            if (tcs.Task.IsCompleted)
            {
                return;
            }
            try
            {
                var srv  = e.Message.Answers.OfType<SRVRecord>().FirstOrDefault();
                var txt  = e.Message.Answers.OfType<TXTRecord>().FirstOrDefault()
                        ?? e.Message.AdditionalRecords.OfType<TXTRecord>().FirstOrDefault();
                var a    = e.Message.AdditionalRecords.OfType<ARecord>().FirstOrDefault();

                if (srv is null || txt is null)
                {
                    return;
                }

                var host    = srv.Target.ToString();
                var port    = srv.Port;
                var ip      = a?.Address.ToString() ?? host.TrimEnd('.');
                var strings = txt.Strings.ToList();
                var id      = strings.FirstOrDefault(s => s.StartsWith("id="))?[3..] ?? "";
                var ffStr   = strings.FirstOrDefault(s => s.StartsWith("ff="))?[3..] ?? "0";
                var ff      = int.TryParse(ffStr, out var f) ? f : 0;

                if (filterIp is not null && ip != filterIp)
                {
                    return;
                }

                Console.WriteLine($"Found: {id} at {ip}:{port}");
                tcs.TrySetResult(new HomeKitDevice(id, ip, port, id, ff));
            }
            catch
            {
                // ignore malformed records
            }
        };

        mdns.Start();
        sd.QueryAllServices();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        timeoutCts.Token.Register(() => tcs.TrySetCanceled());

        try
        {
            return await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"No HomeKit device found{(filterIp is not null ? $" at {filterIp}" : "")} within {timeoutSeconds} seconds.");
        }
        finally
        {
            mdns.Stop();
        }
    }
}
