using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HiloScheduler.HomeKit;

/// <summary>
/// Manages a HAP TCP session: plain HTTP for pair-verify, then
/// ChaCha20-Poly1305 frame-encrypted HTTP for characteristic access.
/// </summary>
public sealed class HapSession : IAsyncDisposable
{
    private readonly TcpClient       _tcp;
    private readonly NetworkStream   _stream;
    private readonly string          _host;
    private readonly int             _port;
    private ChaCha20Poly1305?        _readCipher;
    private ChaCha20Poly1305?        _writeCipher;
    private ulong                    _readNonce;
    private ulong                    _writeNonce;

    private HapSession(TcpClient tcp, NetworkStream stream, string host, int port)
    {
        _tcp    = tcp;
        _stream = stream;
        _host   = host;
        _port   = port;
    }

    public static async Task<HapSession> ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);
        return new HapSession(tcp, tcp.GetStream(), host, port);
    }

    /// <summary>Called after pair-verify to switch to encrypted mode.</summary>
    public void SetSessionKeys(byte[] readKey, byte[] writeKey)
    {
        _readCipher  = new ChaCha20Poly1305(readKey);
        _writeCipher = new ChaCha20Poly1305(writeKey);
        _readNonce   = 0;
        _writeNonce  = 0;
    }

    // -----------------------------------------------------------------------
    // Unencrypted HTTP (used during pair-verify only)
    // -----------------------------------------------------------------------

    public async Task<byte[]> PostTlvAsync(string path, byte[] body, CancellationToken ct = default)
    {
        var header = $"POST {path} HTTP/1.1\r\n" +
                     $"Host: {_host}:{_port}\r\n" +
                     "Content-Type: application/pairing+tlv8\r\n" +
                     $"Content-Length: {body.Length}\r\n" +
                     "Connection: keep-alive\r\n\r\n";
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await _stream.WriteAsync(body, ct);
        return await ReadHttpBodyAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Encrypted HTTP (after pair-verify)
    // -----------------------------------------------------------------------

    public async Task<string> GetAsync(string path, CancellationToken ct = default) =>
        await SendEncryptedAsync("GET", path, null, null, ct);

    public async Task<string> PutAsync(string path, string json, CancellationToken ct = default) =>
        await SendEncryptedAsync("PUT", path, "application/hap+json", json, ct);

    private async Task<string> SendEncryptedAsync(
        string method, string path, string? contentType, string? body, CancellationToken ct)
    {
        var bodyBytes = body is not null ? Encoding.UTF8.GetBytes(body) : null;
        var sb = new StringBuilder();
        sb.Append($"{method} {path} HTTP/1.1\r\n");
        sb.Append($"Host: {_host}:{_port}\r\n");
        sb.Append("Connection: keep-alive\r\n");
        if (bodyBytes is not null)
        {
            sb.Append($"Content-Type: {contentType}\r\n");
            sb.Append($"Content-Length: {bodyBytes.Length}\r\n");
        }
        sb.Append("\r\n");

        var requestBytes = bodyBytes is not null
            ? [.. Encoding.UTF8.GetBytes(sb.ToString()), .. bodyBytes]
            : Encoding.UTF8.GetBytes(sb.ToString());

        await WriteEncryptedFramesAsync(requestBytes, ct);
        return Encoding.UTF8.GetString(await ReadEncryptedResponseAsync(ct));
    }

    private async Task WriteEncryptedFramesAsync(byte[] data, CancellationToken ct)
    {
        const int chunkSize = 1024;
        var offset = 0;
        while (offset < data.Length)
        {
            var chunk      = data[offset..Math.Min(offset + chunkSize, data.Length)];
            offset        += chunk.Length;
            var lenBytes   = BitConverter.GetBytes((ushort)chunk.Length);   // 2-byte LE
            var ciphertext = new byte[chunk.Length];
            var tag        = new byte[16];
            _writeCipher!.Encrypt(MakeNonce(_writeNonce++), chunk, ciphertext, tag, lenBytes);
            await _stream.WriteAsync(lenBytes, ct);
            await _stream.WriteAsync(ciphertext, ct);
            await _stream.WriteAsync(tag, ct);
        }
    }

    private async Task<byte[]> ReadEncryptedResponseAsync(CancellationToken ct)
    {
        var plaintext = new List<byte>();
        while (true)
        {
            var lenBytes  = await ReadExactAsync(2, ct);
            var frameLen  = BitConverter.ToUInt16(lenBytes);
            var encrypted = await ReadExactAsync(frameLen + 16, ct);
            var decrypted = new byte[frameLen];
            _readCipher!.Decrypt(MakeNonce(_readNonce++), encrypted[..frameLen], encrypted[frameLen..], decrypted, lenBytes);
            plaintext.AddRange(decrypted);
            if (IsCompleteHttpResponse([.. plaintext]))
            {
                return [.. plaintext];
            }
        }
    }

    // -----------------------------------------------------------------------
    // Shared helpers
    // -----------------------------------------------------------------------

    private async Task<byte[]> ReadHttpBodyAsync(CancellationToken ct)
    {
        var buf    = new List<byte>();
        var tmp    = new byte[4096];
        var header = string.Empty;

        // Read until we find the end of HTTP headers
        while (!header.Contains("\r\n\r\n"))
        {
            var n = await _stream.ReadAsync(tmp, ct);
            buf.AddRange(tmp[..n]);
            header = Encoding.ASCII.GetString([.. buf]);
        }

        var headerEnd   = header.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headerText  = header[..headerEnd];
        var bodyStart   = headerEnd + 4;
        var match       = Regex.Match(headerText, @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
        var bodyLength  = match.Success ? int.Parse(match.Groups[1].Value) : 0;
        var bodyBytes   = Encoding.Latin1.GetBytes(header)[bodyStart..];

        // Read any remaining body bytes
        while (bodyBytes.Length < bodyLength)
        {
            var n = await _stream.ReadAsync(tmp, ct);
            bodyBytes = [.. bodyBytes, .. tmp[..n]];
        }
        return bodyBytes[..bodyLength];
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            offset += await _stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
        }
        return buffer;
    }

    private static bool IsCompleteHttpResponse(byte[] data)
    {
        var text      = Encoding.UTF8.GetString(data);
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0)
        {
            return false;
        }
        var match = Regex.Match(text[..headerEnd], @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return true;
        }
        var expected = int.Parse(match.Groups[1].Value);
        var body     = text[(headerEnd + 4)..];
        return Encoding.UTF8.GetByteCount(body) >= expected;
    }

    /// <summary>HAP nonce: 4 zero bytes followed by counter as 8-byte little-endian.</summary>
    private static byte[] MakeNonce(ulong counter)
    {
        var nonce = new byte[12];
        BitConverter.GetBytes(counter).CopyTo(nonce, 4);
        return nonce;
    }

    public async ValueTask DisposeAsync()
    {
        _readCipher?.Dispose();
        _writeCipher?.Dispose();
        await _stream.DisposeAsync();
        _tcp.Dispose();
    }
}
