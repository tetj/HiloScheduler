using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace HiloScheduler.HomeKit;

/// <summary>
/// HAP-specific SRP-6a client (3072-bit group, SHA-512).
/// Spec: HomeKit Accessory Protocol § 5.6
/// </summary>
public class HapSrpClient
{
    // HAP 3072-bit group (RFC 5054 §A.1)
    private static readonly BigInteger N = BigInteger.Parse(
        "00" +
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD1" +
        "29024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245" +
        "E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3D" +
        "C2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F" +
        "83655D23DCA3AD961C62F356208552BB9ED529077096966D" +
        "670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9" +
        "DE2BCBF6955817183995497CEA956AE515D2261898FA0510" +
        "15728E5A8AAAC42DAD33170D04507A33A85521ABDF1CBA64" +
        "ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7" +
        "ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6B" +
        "F12FFA06D98A0864D87602733EC86A64521F2B18177B200C" +
        "BBE117577A615D6C770988C0BAD946E208E24FA074E5AB31" +
        "43DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF",
        System.Globalization.NumberStyles.HexNumber);

    private static readonly BigInteger g = new(5);
    private static readonly BigInteger k;

    static HapSrpClient()
    {
        // k = H(N | PAD(g))  where PAD makes g the same byte-length as N
        var nBytes = ToBytes(N);
        var gBytes = PadTo(ToBytes(g), nBytes.Length);
        k = HashToBigInt(nBytes, gBytes);
    }

    private readonly string _username;
    private readonly string _pin;
    private BigInteger _a;
    private BigInteger _A;
    private BigInteger _salt;
    private BigInteger _B;
    private BigInteger _S;

    public HapSrpClient(string username, string pin)
    {
        _username = username;
        _pin      = pin;

        // Generate private key a
        var aBytes = new byte[32];
        RandomNumberGenerator.Fill(aBytes);
        aBytes[0] &= 0x7F; // keep positive
        _a = new BigInteger(aBytes, isUnsigned: true, isBigEndian: true);
        _A = BigInteger.ModPow(g, _a, N);
    }

    public byte[] GetPublicKeyBytes()  => PadTo(ToBytes(_A), ToBytes(N).Length);
    public byte[] GetSaltBytes()       => ToBytes(_salt);

    public void SetSalt(byte[] salt)
    {
        _salt = new BigInteger(salt, isUnsigned: true, isBigEndian: true);
    }

    public void SetServerPublicKey(byte[] b)
    {
        _B = new BigInteger(b, isUnsigned: true, isBigEndian: true);
        ComputeS();
    }

    public byte[] GetProofBytes()
    {
        // M1 = H( H(N) XOR H(g) | H(username) | salt | A | B | K )
        var nBytes    = ToBytes(N);
        var gBytes    = PadTo(ToBytes(g), nBytes.Length);
        var hn        = SHA512.HashData(nBytes);
        var hg        = SHA512.HashData(gBytes);
        var hxor      = hn.Zip(hg, (a, b) => (byte)(a ^ b)).ToArray();
        var hu        = SHA512.HashData(Encoding.UTF8.GetBytes(_username));
        var saltBytes = PadTo(ToBytes(_salt), nBytes.Length);
        var aBytes    = GetPublicKeyBytes();
        var bBytes    = PadTo(ToBytes(_B), nBytes.Length);
        var kBytes    = SHA512.HashData(PadTo(ToBytes(_S), nBytes.Length));

        return SHA512.HashData([.. hxor, .. hu, .. saltBytes, .. aBytes, .. bBytes, .. kBytes]);
    }

    public bool VerifyServerProof(byte[] serverM2)
    {
        // M2 = H(A | M1 | K)
        var nBytes  = ToBytes(N);
        var aBytes  = GetPublicKeyBytes();
        var m1      = GetProofBytes();
        var kBytes  = SHA512.HashData(PadTo(ToBytes(_S), nBytes.Length));
        var expected = SHA512.HashData([.. aBytes, .. m1, .. kBytes]);
        return expected.SequenceEqual(serverM2);
    }

    public byte[] GetSessionKeyBytes()
    {
        var nBytes = ToBytes(N);
        return SHA512.HashData(PadTo(ToBytes(_S), nBytes.Length));
    }

    private void ComputeS()
    {
        var nBytes = ToBytes(N);
        var aBytes = GetPublicKeyBytes();
        var bBytes = PadTo(ToBytes(_B), nBytes.Length);

        // u = H(A | B)
        var u = HashToBigInt(aBytes, bBytes);

        // x = H(salt | H(username | ":" | pin))
        var saltBytes  = PadTo(ToBytes(_salt), nBytes.Length);
        var innerHash  = SHA512.HashData(Encoding.UTF8.GetBytes($"{_username}:{_pin}"));
        var x          = HashToBigInt(saltBytes, innerHash);

        // S = (B - k*g^x) ^ (a + u*x) mod N
        var gx  = BigInteger.ModPow(g, x, N);
        var kgx = (k * gx) % N;
        var bkg = (((_B - kgx) % N) + N) % N;
        var exp = (_a + u * x) % (N - 1);
        _S      = BigInteger.ModPow(bkg, exp, N);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static BigInteger HashToBigInt(params byte[][] parts)
    {
        var hash = SHA512.HashData(parts.SelectMany(p => p).ToArray());
        return new BigInteger(hash, isUnsigned: true, isBigEndian: true);
    }

    private static byte[] ToBytes(BigInteger n)
    {
        var bytes = n.ToByteArray(isUnsigned: true, isBigEndian: true);
        return bytes;
    }

    private static byte[] PadTo(byte[] bytes, int length)
    {
        if (bytes.Length >= length)
        {
            return bytes;
        }
        var padded = new byte[length];
        bytes.CopyTo(padded, length - bytes.Length);
        return padded;
    }
}
