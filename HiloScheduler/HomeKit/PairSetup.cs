using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace HiloScheduler.HomeKit;

/// <summary>
/// Performs the HAP Pair-Setup flow (SRP-6a + Ed25519 exchange).
/// Spec: HomeKit Accessory Protocol § 5.6
/// </summary>
public static class PairSetup
{
    // TLV8 type constants
    private const byte TlvState     = 0x06;
    private const byte TlvMethod    = 0x00;
    private const byte TlvPublicKey = 0x03;
    private const byte TlvProof     = 0x04;
    private const byte TlvSalt      = 0x02;
    private const byte TlvEncData   = 0x05;
    private const byte TlvIdentifier = 0x01;
    private const byte TlvSignature  = 0x0A;
    private const byte TlvError      = 0x07;

    private const byte MethodPairSetupWithAuth = 0x01;

    private static readonly KeyDerivationAlgorithm Hkdf512 = KeyDerivationAlgorithm.HkdfSha512;
    private static readonly SignatureAlgorithm      Ed25519 = SignatureAlgorithm.Ed25519;

    public static async Task<PairingRecord> PerformAsync(
        string host, int port, string pin, CancellationToken ct = default)
    {
        await using var session = await HapSession.ConnectAsync(host, port, ct);

        // --- M1 → M2: SRP start ---
        var m1 = Tlv8.Encode(
            (TlvState,  [(byte)0x01]),
            (TlvMethod, [MethodPairSetupWithAuth])
        );
        var m2Bytes = await session.PostTlvAsync("/pair-setup", m1, ct);
        var m2      = Tlv8.Decode(m2Bytes);
        CheckError(m2, "M2");

        var salt      = m2[TlvSalt];
        var serverPub = m2[TlvPublicKey];

        // --- M3 → M4: SRP verify ---
        var srp = new HapSrpClient("Pair-Setup", pin.Replace("-", ""));
        srp.SetSalt(salt);
        srp.SetServerPublicKey(serverPub);

        var m3 = Tlv8.Encode(
            (TlvState,     [(byte)0x03]),
            (TlvPublicKey, srp.GetPublicKeyBytes()),
            (TlvProof,     srp.GetProofBytes())
        );
        var m4Bytes = await session.PostTlvAsync("/pair-setup", m3, ct);
        var m4      = Tlv8.Decode(m4Bytes);
        CheckError(m4, "M4");

        if (!srp.VerifyServerProof(m4[TlvProof]))
        {
            throw new InvalidOperationException("SRP server proof verification failed.");
        }

        // --- M5 → M6: key exchange ---
        var sessionKeyBytes = srp.GetSessionKeyBytes();

        // Generate iOS device Ed25519 key pair
        using var iosKey    = Key.Create(Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });
        var iosPub          = iosKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var iosPairingId    = Guid.NewGuid().ToString();
        var iosPairingIdBytes = Encoding.UTF8.GetBytes(iosPairingId);

        // Derive iosDeviceX and session encryption key
        var iosDeviceX  = DeriveBytes(sessionKeyBytes,
            "Pair-Setup-Controller-Sign-Salt",
            "Pair-Setup-Controller-Sign-Info");
        var encKey      = DeriveBytes(sessionKeyBytes,
            "Pair-Setup-Encrypt-Salt",
            "Pair-Setup-Encrypt-Info");

        // Sign iosDeviceInfo = iosDeviceX | iosPairingId | iosPub
        var iosDeviceInfo = (byte[])[.. iosDeviceX, .. iosPairingIdBytes, .. iosPub];
        var sig           = Ed25519.Sign(iosKey, iosDeviceInfo);

        var subTlv = Tlv8.Encode(
            (TlvIdentifier, iosPairingIdBytes),
            (TlvPublicKey,  iosPub),
            (TlvSignature,  sig)
        );

        var encrypted = EncryptChaCha(encKey, "PS-Msg05", subTlv);
        var m5 = Tlv8.Encode(
            (TlvState,   [(byte)0x05]),
            (TlvEncData, encrypted)
        );
        var m6Bytes = await session.PostTlvAsync("/pair-setup", m5, ct);
        var m6      = Tlv8.Decode(m6Bytes);
        CheckError(m6, "M6");

        var decrypted = DecryptChaCha(encKey, "PS-Msg06", m6[TlvEncData]);
        var m6Sub     = Tlv8.Decode(decrypted);

        // Verify accessory's identity
        var accessoryId   = m6Sub[TlvIdentifier];
        var accessoryLtpk = m6Sub[TlvPublicKey];
        var accessorySig  = m6Sub[TlvSignature];
        var accessoryX    = DeriveBytes(sessionKeyBytes,
            "Pair-Setup-Accessory-Sign-Salt",
            "Pair-Setup-Accessory-Sign-Info");

        var accessoryInfo = (byte[])[.. accessoryX, .. accessoryId, .. accessoryLtpk];
        var accPubKey     = PublicKey.Import(Ed25519, accessoryLtpk, KeyBlobFormat.RawPublicKey);
        if (!Ed25519.Verify(accPubKey, accessoryInfo, accessorySig))
        {
            throw new InvalidOperationException("Accessory identity verification failed during pair-setup.");
        }

        // Export private key (first 32 bytes = private scalar, last 32 bytes = public key)
        var iosPrivate = iosKey.Export(KeyBlobFormat.RawPrivateKey);

        return new PairingRecord(
            AccessoryPairingId: Encoding.UTF8.GetString(accessoryId),
            AccessoryLtpk:      Convert.ToHexString(accessoryLtpk).ToLowerInvariant(),
            IosPairingId:       iosPairingId,
            IosDeviceLtsk:      Convert.ToHexString(iosPrivate).ToLowerInvariant(),
            IosDeviceLtpk:      Convert.ToHexString(iosPub).ToLowerInvariant(),
            AccessoryIp:        host,
            AccessoryPort:      port,
            Connection:         "IP"
        );
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static byte[] DeriveBytes(byte[] ikm, string salt, string info)
    {
        // HkdfSha512 via NSec requires a Key — use raw IKM approach via manual HKDF
        using var hmac     = new System.Security.Cryptography.HMACSHA512(Encoding.ASCII.GetBytes(salt));
        var prk            = hmac.ComputeHash(ikm);
        using var hmac2    = new System.Security.Cryptography.HMACSHA512(prk);
        var infoBytes      = Encoding.ASCII.GetBytes(info);
        var okm            = hmac2.ComputeHash([.. infoBytes, 0x01]);
        return okm[..32];
    }

    private static byte[] EncryptChaCha(byte[] key, string nonceStr, byte[] plaintext)
    {
        var nonce      = PadNonce(nonceStr);
        var ciphertext = new byte[plaintext.Length];
        var tag        = new byte[16];
        using var c    = new System.Security.Cryptography.ChaCha20Poly1305(key);
        c.Encrypt(nonce, plaintext, ciphertext, tag);
        return [.. ciphertext, .. tag];
    }

    private static byte[] DecryptChaCha(byte[] key, string nonceStr, byte[] ciphertextAndTag)
    {
        var nonce      = PadNonce(nonceStr);
        var ciphertext = ciphertextAndTag[..^16];
        var tag        = ciphertextAndTag[^16..];
        var plaintext  = new byte[ciphertext.Length];
        using var c    = new System.Security.Cryptography.ChaCha20Poly1305(key);
        c.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private static byte[] PadNonce(string s)
    {
        var nonce = new byte[12];
        Encoding.ASCII.GetBytes(s).CopyTo(nonce, 4);
        return nonce;
    }

    private static void CheckError(Dictionary<byte, byte[]> tlv, string step)
    {
        if (tlv.TryGetValue(TlvError, out var err))
        {
            throw new InvalidOperationException($"HAP error at {step}: 0x{err[0]:X2}");
        }
    }
}
