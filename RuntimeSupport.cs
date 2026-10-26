using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoiceAec;

internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex mutex;
    public EventWaitHandle Activation { get; }
    public bool IsPrimary { get; }

    public SingleInstanceGuard()
    {
        var key = @"Local\VoiceAec-" + WindowsIdentity.GetCurrent().User!.Value;
        mutex = new Mutex(true, key, out var created);
        IsPrimary = created;
        Activation = new EventWaitHandle(false, EventResetMode.AutoReset, key + "-activate");
        if (!created) Activation.Set();
    }

    public void Dispose()
    {
        Activation.Dispose();
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}

internal static class LocalApiPolicy
{
    // Loopback is always allowed. When the panel is exposed on the home LAN,
    // private IPv4 peers are allowed too, but only for this machine's own
    // addresses: a foreign Host/Origin still means DNS rebinding or a
    // cross-site request and is rejected.
    public static bool Allows(string host, string? origin, string? fetchSite,
        IPAddress? remote, int port, IReadOnlyList<IPAddress>? lanAddresses = null)
    {
        if (remote == null) return false;
        var loopback = IPAddress.IsLoopback(remote);
        var lan = lanAddresses is { Count: > 0 } && IsPrivate(remote);
        if (!loopback && !lan) return false;

        var hostOk = host.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase)
            || host.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
            || (lanAddresses != null && lanAddresses.Any(a => host.Equals($"{a}:{port}", StringComparison.OrdinalIgnoreCase)));
        if (!hostOk) return false;
        if (fetchSite is "cross-site") return false;
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme != "http" || uri.Port != port) return false;
        if (uri.Host is "localhost" or "127.0.0.1") return true;
        return lanAddresses != null &&
            lanAddresses.Any(a => uri.Host.Equals(a.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168);
    }

    public static bool TokenMatches(string? supplied, string expected) =>
        supplied != null && supplied.Length == expected.Length &&
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(supplied),
            System.Text.Encoding.ASCII.GetBytes(expected));
}

// Own every resource in a capture pipeline. Reuse scratch memory rather than
// allocating 8 KB for every audio callback; release the resampler on stop.
internal sealed class CaptureSession : IDisposable
{
    private readonly object callbackLock = new();
    private readonly WasapiCapture capture;
    private readonly MediaFoundationResampler resampler;
    private readonly BufferedWaveProvider provider;
    private readonly SampleRing destination;
    private readonly byte[] scratch = new byte[8192];
    private readonly Action? onSamples;
    private int disposed;

    public CaptureSession(MMDevice device, SampleRing destination, WaveFormat target, Action? onSamples = null)
    {
        this.destination = destination;
        this.onSamples = onSamples;
        capture = device.DataFlow == DataFlow.Capture
            ? new WasapiCapture(device, true, 20) : new WasapiLoopbackCapture(device);
        if (device.DataFlow == DataFlow.Render)
            typeof(WasapiCapture).GetField("audioBufferMillisecondsLength",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(capture, 20);
        try
        {
            provider = new BufferedWaveProvider(capture.WaveFormat)
            {
                DiscardOnBufferOverflow = true, ReadFully = false,
                BufferDuration = TimeSpan.FromSeconds(2)
            };
            resampler = new MediaFoundationResampler(provider, target);
            capture.DataAvailable += OnData;
            capture.StartRecording();
        }
        catch
        {
            capture.Dispose();
            resampler?.Dispose();
            throw;
        }
    }

    private void OnData(object? sender, WaveInEventArgs args)
    {
        lock (callbackLock)
        {
            if (Volatile.Read(ref disposed) != 0 || args.BytesRecorded == 0) return;
            provider.AddSamples(args.Buffer, 0, args.BytesRecorded);
            int count;
            do
            {
                count = resampler.Read(scratch, 0, scratch.Length);
                if (count > 0) destination.Write(scratch, count);
            } while (count > 0);
            onSamples?.Invoke();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { capture.StopRecording(); }
        finally
        {
            capture.Dispose();
            lock (callbackLock) resampler.Dispose();
        }
    }
}
