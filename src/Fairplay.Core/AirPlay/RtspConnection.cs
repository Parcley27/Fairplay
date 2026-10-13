using System.Net;
using System.Net.Sockets;
using System.Text;
using Fairplay.Crypto;
using Fairplay.Plist;
using Fairplay.Util;

namespace Fairplay.AirPlay;

public sealed class RtspMessage
{
    public string StartLine { get; init; } = "";
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; init; } = [];

    /// <summary>For responses: the numeric status code.</summary>
    public int Status => int.TryParse(StartLine.Split(' ').ElementAtOrDefault(1), out var s) ? s : 0;
    public bool IsResponse => StartLine.StartsWith("RTSP/") || StartLine.StartsWith("HTTP/");

    public object? Plist => Body.Length > 8 && Encoding.ASCII.GetString(Body, 0, 6) == "bplist" ? BinaryPlist.Read(Body) : null;
    public Dictionary<string, object?> PlistDict => Plist as Dictionary<string, object?> ?? new();

    public override string ToString() => $"{StartLine} [{Body.Length} bytes]";
}

/// <summary>
/// Incremental parser for RTSP/HTTP messages arriving on a byte stream.
/// </summary>
public sealed class RtspParser
{
    readonly MemoryStream _buf = new();

    public void Feed(ReadOnlySpan<byte> data) => _buf.Write(data);

    public RtspMessage? TryRead()
    {
        var data = _buf.GetBuffer().AsSpan(0, (int)_buf.Length);
        int headerEnd = data.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0) return null;
        var head = Encoding.UTF8.GetString(data[..headerEnd]);
        var lines = head.Split("\r\n");
        var msg = new RtspMessage { StartLine = lines[0] };
        foreach (var line in lines.Skip(1))
        {
            int c = line.IndexOf(':');
            if (c > 0) msg.Headers[line[..c].Trim()] = line[(c + 1)..].Trim();
        }
        int len = msg.Headers.TryGetValue("Content-Length", out var cl) ? int.Parse(cl) : 0;
        int total = headerEnd + 4 + len;
        if (data.Length < total) return null;
        var full = new RtspMessage { StartLine = msg.StartLine, Body = data.Slice(headerEnd + 4, len).ToArray() };
        foreach (var kv in msg.Headers) full.Headers[kv.Key] = kv.Value;
        var rest = data[total..].ToArray();
        _buf.SetLength(0);
        _buf.Write(rest);
        return full;
    }
}

/// <summary>
/// Client side of the AirPlay control connection (TCP 7000). Speaks RTSP/1.0 and HTTP/1.1 framing;
/// once pairing completes, every byte in both directions goes through a <see cref="HapStreamCipher"/>.
/// Requests are serialized: one outstanding request at a time.
/// </summary>
public sealed class RtspConnection : IDisposable
{
    readonly TcpClient _tcp;
    readonly NetworkStream _stream;
    readonly RtspParser _parser = new();
    readonly SemaphoreSlim _lock = new(1, 1);
    readonly byte[] _readBuf = new byte[16384];
    HapStreamCipher? _cipher;
    int _cseq;

    public IPAddress RemoteAddress { get; }
    public IPAddress LocalAddress { get; }
    public string DacpId { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
    public uint ActiveRemote { get; } = (uint)Random.Shared.Next();
    public bool IsEncrypted => _cipher != null;
    public bool IsConnected => _tcp.Connected;

    RtspConnection(TcpClient tcp, IPAddress remote)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = tcp.GetStream();
        RemoteAddress = remote;
        LocalAddress = ((IPEndPoint)tcp.Client.LocalEndPoint!).Address;
    }

    public static async Task<RtspConnection> ConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        var tcp = new TcpClient(address.AddressFamily);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        await tcp.ConnectAsync(address, port, cts.Token);
        tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        return new RtspConnection(tcp, address);
    }

    public void EnableEncryption(byte[] writeKey, byte[] readKey) => _cipher = new HapStreamCipher(writeKey, readKey);

    public Task<RtspMessage> RequestAsync(string method, string uri, byte[]? body = null, string? contentType = null,
        IDictionary<string, string>? headers = null, string protocol = "RTSP/1.0", CancellationToken ct = default)
        => RequestCoreAsync(method, uri, body, contentType, headers, protocol, ct);

    public Task<RtspMessage> RequestPlistAsync(string method, string uri, object plist, IDictionary<string, string>? headers = null, CancellationToken ct = default)
        => RequestCoreAsync(method, uri, BinaryPlist.Write(plist), "application/x-apple-binary-plist", headers, "RTSP/1.0", ct);

    async Task<RtspMessage> RequestCoreAsync(string method, string uri, byte[]? body, string? contentType,
        IDictionary<string, string>? headers, string protocol, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            int cseq = ++_cseq;
            var sb = new StringBuilder();
            sb.Append($"{method} {uri} {protocol}\r\n");
            sb.Append($"CSeq: {cseq}\r\n");
            sb.Append("User-Agent: AirPlay/690.7.1\r\n");
            sb.Append($"DACP-ID: {DacpId}\r\n");
            sb.Append($"Active-Remote: {ActiveRemote}\r\n");
            sb.Append($"Client-Instance: {DacpId}\r\n");
            if (headers != null)
                foreach (var kv in headers) sb.Append($"{kv.Key}: {kv.Value}\r\n");
            if (contentType != null) sb.Append($"Content-Type: {contentType}\r\n");
            sb.Append($"Content-Length: {body?.Length ?? 0}\r\n\r\n");

            var head = Encoding.UTF8.GetBytes(sb.ToString());
            var raw = new byte[head.Length + (body?.Length ?? 0)];
            head.CopyTo(raw, 0);
            body?.CopyTo(raw, head.Length);

            if (Log.IsEnabled(LogLevel.Debug))
                Log.Debug($"→ {method} {uri} ({body?.Length ?? 0} bytes){(body != null && contentType?.Contains("plist") == true ? "\n" + BinaryPlist.Dump(BinaryPlist.Read(body)) : "")}");

            var wire = _cipher != null ? _cipher.Encrypt(raw) : raw;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await _stream.WriteAsync(wire, cts.Token);

            while (true)
            {
                var msg = await ReadMessageAsync(cts.Token);
                if (!msg.IsResponse)
                {
                    Log.Debug($"ignoring unsolicited request on control channel: {msg.StartLine}");
                    continue;
                }
                if (msg.Headers.TryGetValue("CSeq", out var rc) && int.TryParse(rc, out var rcs) && rcs != cseq)
                {
                    Log.Debug($"skipping response for CSeq {rcs} (want {cseq})");
                    continue;
                }
                if (Log.IsEnabled(LogLevel.Debug))
                    Log.Debug($"← {msg.StartLine} ({msg.Body.Length} bytes){(msg.Plist is { } p ? "\n" + BinaryPlist.Dump(p) : "")}");
                return msg;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    async Task<RtspMessage> ReadMessageAsync(CancellationToken ct)
    {
        while (true)
        {
            var m = _parser.TryRead();
            if (m != null) return m;
            int n = await _stream.ReadAsync(_readBuf, ct);
            if (n == 0) throw new IOException("connection closed by receiver");
            _parser.Feed(_cipher != null ? _cipher.Decrypt(_readBuf.AsSpan(0, n)) : _readBuf.AsSpan(0, n));
        }
    }

    public void Dispose()
    {
        try { _tcp.Close(); } catch { }
        _cipher?.Dispose();
    }
}
