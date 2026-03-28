namespace HiloScheduler.HomeKit;

/// <summary>Minimal TLV8 encoder/decoder for the HAP protocol.</summary>
public static class Tlv8
{
    public const byte State         = 0x06;
    public const byte PublicKey     = 0x03;
    public const byte EncryptedData = 0x05;
    public const byte Identifier    = 0x01;
    public const byte Signature     = 0x09;
    public const byte Error         = 0x07;

    /// <summary>Encode one or more (type, value) pairs into TLV8 bytes.</summary>
    public static byte[] Encode(params (byte Type, byte[] Value)[] items)
    {
        var buf = new List<byte>();
        foreach (var (type, value) in items)
        {
            var offset = 0;
            do
            {
                var len = Math.Min(value.Length - offset, 255);
                buf.Add(type);
                buf.Add((byte)len);
                buf.AddRange(value[offset..(offset + len)]);
                offset += len;
            }
            while (offset < value.Length);
        }
        return [.. buf];
    }

    /// <summary>Decode a TLV8 byte array into a type→value dictionary.
    /// Consecutive entries of the same type are concatenated.</summary>
    public static Dictionary<byte, byte[]> Decode(byte[] data)
    {
        var result = new Dictionary<byte, byte[]>();
        var i = 0;
        while (i + 1 < data.Length)
        {
            var type = data[i++];
            var len  = data[i++];
            var value = data[i..(i + len)];
            i += len;
            result[type] = result.TryGetValue(type, out var existing)
                ? [.. existing, .. value]
                : value;
        }
        return result;
    }
}
