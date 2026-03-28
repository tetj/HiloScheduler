using System.Security.Cryptography;
using System.Text;

namespace HiloScheduler.Hilo;

internal static class PkceUtils
{
    public static (string Verifier, string Challenge) GeneratePair()
    {
        var verifier  = Base64UrlEncode(RandomBytes(64));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static string GenerateState() => Base64UrlEncode(RandomBytes(16));

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
