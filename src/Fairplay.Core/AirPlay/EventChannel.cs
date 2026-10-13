using System.Net;
using System.Net.Sockets;
using System.Text;
using Fairplay.Crypto;
using Fairplay.Plist;
using Fairplay.Util;

namespace Fairplay.AirPlay;

/// <summary>
/// The reverse channel the receiver opens requests on (TCP, port from the first SETUP).
/// We connect, but the receiver acts as the client: it POSTs /command messages (state updates)
/// and we must answer 200 OK. Keys are derived like the control channel but with "Events-*"
/// labels and swapped direction.
/// </summary>
public sealed class EventChannel : IDisposable
{
    readonly TcpClient _tcp;
    readonly NetworkStream _stream;
    readonly HapStreamCipher _cipher;
    readonly RtspParser _parser = new();
    readonly CancellationTokenSource _cts = new();

    public event Action<string, Dictionary<string, object?>>? CommandReceived;
    public event Action? Closed;

    EventChannel(TcpClient tcp, HapStreamCipher cipher)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _cipher = cipher;
    }

    public static async Task<EventChannel> ConnectAsync(IPAddress address, int port, byte[] sharedSecret, CancellationToken ct)
    {
        // Our write key is the receiver's read key, i.e. "Events-Read-Encryption-Key".
        var writeKey = HapKeys.Derive(sharedSecret, "Events-Salt", "Events-Read-Encryption-Key");
        var readKey = HapKeys.Derive(sharedSecret, "Events-Salt", "Events-Write-Encryption-Key");
        Exception? last = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var tcp = new TcpClient(address.AddressFamily);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                await tcp.ConnectAsync(address, port, cts.Token);
                var ch = new EventChannel(tcp, new HapStreamCipher(writeKey, readKey));
                _ = ch.RunAsync();
                return ch;
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                last = e;
                tcp.Dispose();
                await Task.Delay(300, ct);
            }
        }
        throw new IOException($"could not open event channel: {last?.Message}");
    }

    async Task RunAsync()
    {
        var buf = new byte[16384];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buf, _cts.Token);
                if (n == 0) break;
                _parser.Feed(_cipher.Decrypt(buf.AsSpan(0, n)));
                while (_parser.TryRead() is { } req)
                {
                    var dict = req.PlistDict;
                    var type = dict.TryGetValue("type", out var t) ? t?.ToString() ?? "" : "";
                    Log.Debug($"event channel: {req.StartLine} type={type}");
                    CommandReceived?.Invoke(type, dict);

                    var cseq = req.Headers.TryGetValue("CSeq", out var c) ? c : "0";
                    var proto = req.StartLine.Split(' ').LastOrDefault() ?? "RTSP/1.0";
                    var resp = $"{proto} 200 OK\r\nCSeq: {cseq}\r\nAudio-Latency: 0\r\nContent-Length: 0\r\n\r\n";
                    var wire = _cipher.Encrypt(Encoding.ASCII.GetBytes(resp));
                    await _stream.WriteAsync(wire, _cts.Token);
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or System.Security.Cryptography.CryptographicException)
        {
            if (!_cts.IsCancellationRequested) Log.Debug($"event channel ended: {e.Message}");
        }
        if (!_cts.IsCancellationRequested) Closed?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _tcp.Close(); } catch { }
        _cipher.Dispose();
    }
}
