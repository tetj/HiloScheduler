using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace HiloScheduler.HomeKit;

/// <summary>
/// Performs the HAP Pair-Verify handshake over an existing <see cref="HapSession"/>,
/// establishing the encrypted session keys.
/// </summary>
public static class PairVerify
{
    private static readonly KeyAgreementAlgorithm X25519   = KeyAgreementAlgorithm.X25519;
    private static readonly SignatureAlgorithm    Ed25519  = SignatureAlgorithm.Ed25519;
    private static readonly KeyDerivationAlgorithm Hkdf512 = KeyDerivationAlgorithm.HkdfSha512;

    public static async Task PerformAsync(HapSession session, PairingRecord pairing, CancellationToken ct = default)
    {
        // --- M1: Generate ephemeral X25519 key, send to accessory ---
        using var clientEphKey = Key.Create(X25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });
        var clientEphPub = clientEphKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var m1 = Tlv8.Encode(
            (Tlv8.State,     [(byte)0x01]),
            (Tlv8.PublicKey, clientEphPub)
        );
        var m2Bytes = await session.PostTlvAsync("/pair-verify", m1, ct);
        var m2      = Tlv8.Decode(m2Bytes);

        // --- M2: Derive shared secret and session key, verify accessory ---
        var accessoryEphPub = m2[Tlv8.PublicKey];
        var accessoryEphKey = PublicKey.Import(X25519, accessoryEphPub, KeyBlobFormat.RawPublicKey);

        using var sharedSecret = X25519.Agree(clientEphKey, accessoryEphKey)
            ?? throw new InvalidOperationException("X25519 key agreement failed.");

        var pvKey = Hkdf512.DeriveBytes(
            sharedSecret,
            Encoding.ASCII.GetBytes("Pair-Verify-Encryption-Salt"),
            Encoding.ASCII.GetBytes("Pair-Verify-Encryption-Info"),
            32);

        var encData  = m2[Tlv8.EncryptedData];
        var decrypted = DecryptChaCha(pvKey, "PV-Msg02", encData);
        var m2Sub    = Tlv8.Decode(decrypted);

        var accessoryIdBytes = m2Sub[Tlv8.Identifier];
        var accessorySignature = m2Sub[Tlv8.Signature];
        var accessoryInfo    = (byte[])[.. accessoryEphPub, .. accessoryIdBytes, .. clientEphPub];
        var accessoryPubKey  = PublicKey.Import(Ed25519, pairing.GetAccessoryLtpk(), KeyBlobFormat.RawPublicKey);

        if (!Ed25519.Verify(accessoryPubKey, accessoryInfo, accessorySignature))
        {
            throw new InvalidOperationException("Accessory signature verification failed during pair-verify.");
        }

        // --- M3: Sign our own info, send to accessory ---
        var clientIdBytes = Encoding.UTF8.GetBytes(pairing.IosPairingId);
        var clientInfo    = (byte[])[.. clientEphPub, .. clientIdBytes, .. accessoryEphPub];

        using var sigKey      = Key.Import(Ed25519, pairing.GetIosDeviceLtsk(), KeyBlobFormat.RawPrivateKey);
        var clientSignature   = Ed25519.Sign(sigKey, clientInfo);
        var m3SubTlv          = Tlv8.Encode(
            (Tlv8.Identifier, clientIdBytes),
            (Tlv8.Signature,  clientSignature)
        );
        var m3Encrypted = EncryptChaCha(pvKey, "PV-Msg03", m3SubTlv);
        var m3 = Tlv8.Encode(
            (Tlv8.State,         [(byte)0x03]),
            (Tlv8.EncryptedData, m3Encrypted)
        );
        var m4Bytes = await session.PostTlvAsync("/pair-verify", m3, ct);
        var m4      = Tlv8.Decode(m4Bytes);

        // --- M4: Check for error, derive final session keys ---
        if (m4.TryGetValue(Tlv8.Error, out var errCode))
        {
            throw new InvalidOperationException($"Pair-verify rejected by accessory (error 0x{errCode[0]:X2}).");
        }

        var readKey  = Hkdf512.DeriveBytes(sharedSecret,
            Encoding.ASCII.GetBytes("Control-Salt"),
            Encoding.ASCII.GetBytes("Control-Read-Encryption-Key"),
            32);
        var writeKey = Hkdf512.DeriveBytes(sharedSecret,
            Encoding.ASCII.GetBytes("Control-Salt"),
            Encoding.ASCII.GetBytes("Control-Write-Encryption-Key"),
            32);

        session.SetSessionKeys(readKey, writeKey);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static byte[] DecryptChaCha(byte[] key, string nonceStr, byte[] ciphertextAndTag)
    {
        var nonce      = PadNonce(nonceStr);
        var ciphertext = ciphertextAndTag[..^16];
        var tag        = ciphertextAndTag[^16..];
        var plaintext  = new byte[ciphertext.Length];
        using var cipher = new System.Security.Cryptography.ChaCha20Poly1305(key);
        cipher.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private static byte[] EncryptChaCha(byte[] key, string nonceStr, byte[] plaintext)
    {
        var nonce      = PadNonce(nonceStr);
        var ciphertext = new byte[plaintext.Length];
        var tag        = new byte[16];
        using var cipher = new System.Security.Cryptography.ChaCha20Poly1305(key);
        cipher.Encrypt(nonce, plaintext, ciphertext, tag);
        return [.. ciphertext, .. tag];
    }

    /// <summary>HAP pair-verify nonce: 4 zero bytes + ASCII string bytes.</summary>
    private static byte[] PadNonce(string s)
    {
        var nonce = new byte[12];
        var bytes = Encoding.ASCII.GetBytes(s);
        bytes.CopyTo(nonce, 4);
        return nonce;
    }
}
