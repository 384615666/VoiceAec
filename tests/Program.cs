using System.Net;
using VoiceAec;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS: " + name);
}

if (args.Length == 2 && args[0] == "--validate-bundle")
{
    var verifier = new ObsPluginInstaller(args[1], Path.Combine(Path.GetTempPath(), "VoiceAec-validation", "plugins"),
        Path.Combine(Path.GetTempPath(), "VoiceAec-validation", "store"), () => false);
    var payload = verifier.ReadPayload();
    Check(payload.Files.Length >= 6, $"published bundle validates all {payload.Files.Length} OBS payload files");
    return;
}

var ring = new SampleRing(1024);
var large = Enumerable.Range(0, 4097).Select(i => (byte)(i % 251)).ToArray();
Check(ring.Write(large, large.Length) == 3073, "oversized write reports discarded prefix");
var actual = new byte[1024];
Check(ring.Read(actual, 0, actual.Length) == 1024 && actual.SequenceEqual(large.TakeLast(1024)),
    "oversized write keeps the newest samples in order");
ring.Write(large, 900);
ring.Read(actual, 0, 800);
ring.Write(large, 1024);
Check(ring.Read(actual, 0, actual.Length) == 1024 && actual.SequenceEqual(large.Take(1024)),
    "wraparound and overflow preserve queue ordering");
ring.Write(large, 900);
ring.Clear();
Check(ring.BufferedBytes == 0 && ring.Read(actual, 0, actual.Length) == 0,
    "clearing on pause discards buffered audio");

Check(LocalApiPolicy.Allows("localhost:8765", "http://localhost:8765", "same-origin", IPAddress.Loopback, 8765),
    "same-origin panel accepted");
Check(!LocalApiPolicy.Allows("evil.example:8765", null, null, IPAddress.Loopback, 8765),
    "DNS rebinding host rejected");
Check(!LocalApiPolicy.Allows("localhost:8765", "https://evil.example", "cross-site", IPAddress.Loopback, 8765),
    "foreign web origin rejected");
Check(!LocalApiPolicy.Allows("localhost:8765", "null", null, IPAddress.Loopback, 8765),
    "opaque origin rejected");
Check(!LocalApiPolicy.Allows("localhost:8765", null, null, IPAddress.Parse("192.168.1.1"), 8765),
    "non-loopback peer rejected");
Check(!LocalApiPolicy.TokenMatches(null, "secret") && !LocalApiPolicy.TokenMatches("wrong", "secret") &&
    LocalApiPolicy.TokenMatches("secret", "secret"), "API session token required");

using var primary = new SingleInstanceGuard();
Check(primary.IsPrimary, "first launch acquires instance mutex");
using var secondary = new SingleInstanceGuard();
Check(!secondary.IsPrimary && primary.Activation.WaitOne(1000),
    "second launch requests activation without owning audio");

ObsInstallerTests.Run();
