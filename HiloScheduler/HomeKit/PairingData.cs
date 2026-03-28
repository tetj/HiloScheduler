using System.Text.Json.Serialization;

namespace HiloScheduler.HomeKit;

public record PairingFile(string Alias, PairingRecord Pairing);

public record PairingRecord(
    [property: JsonPropertyName("AccessoryPairingID")] string AccessoryPairingId,
    [property: JsonPropertyName("AccessoryLTPK")]      string AccessoryLtpk,
    [property: JsonPropertyName("iOSPairingId")]       string IosPairingId,
    [property: JsonPropertyName("iOSDeviceLTSK")]      string IosDeviceLtsk,
    [property: JsonPropertyName("iOSDeviceLTPK")]      string IosDeviceLtpk,
    [property: JsonPropertyName("AccessoryIP")]        string AccessoryIp,
    [property: JsonPropertyName("AccessoryPort")]      int    AccessoryPort,
    [property: JsonPropertyName("Connection")]         string Connection
)
{
    public byte[] GetAccessoryLtpk()  => Convert.FromHexString(AccessoryLtpk);
    public byte[] GetIosDeviceLtsk()  => Convert.FromHexString(IosDeviceLtsk);
    public byte[] GetIosDeviceLtpk()  => Convert.FromHexString(IosDeviceLtpk);
}
