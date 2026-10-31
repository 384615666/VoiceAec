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
    "non-loopback peer rejected without LAN mode");
var lan = new[] { IPAddress.Parse("192.168.1.10") };
Check(LocalApiPolicy.Allows("192.168.1.10:8765", "http://192.168.1.10:8765", "same-origin",
        IPAddress.Parse("192.168.1.50"), 8765, lan),
    "LAN panel accepted for this machine's address");
Check(!LocalApiPolicy.Allows("evil.example:8765", null, null, IPAddress.Parse("192.168.1.50"), 8765, lan),
    "LAN rebinding host rejected");
Check(!LocalApiPolicy.Allows("192.168.1.10:8765", "http://evil.example", "cross-site",
        IPAddress.Parse("192.168.1.50"), 8765, lan),
    "LAN cross-site origin rejected");
Check(!LocalApiPolicy.Allows("192.168.1.10:8765", null, null, IPAddress.Parse("8.8.8.8"), 8765, lan),
    "public peer rejected even with LAN mode");
Check(!LocalApiPolicy.TokenMatches(null, "secret") && !LocalApiPolicy.TokenMatches("wrong", "secret") &&
    LocalApiPolicy.TokenMatches("secret", "secret"), "API session token required");

// ── UI language: Chinese by default, English on "en…", unknown values fall back
UiText.SetLang(null);
Check(UiText.Lang == "zh" && !UiText.IsEnglish, "interface language defaults to Chinese");
Check(UiText.S("中文", "English") == "中文", "Chinese text is selected by default");
Check(UiText.F("共 {0} 个", "{0} items", 3) == "共 3 个", "Chinese format string is selected by default");
Check(UiText.NormalizeLang(" EN ") == "en" && UiText.NormalizeLang("English") == "en" &&
    UiText.NormalizeLang("zh-CN") == "zh" && UiText.NormalizeLang("fr") == "zh" &&
    UiText.NormalizeLang(null) == "zh",
    "language parsing trims, ignores case and treats unknown values as Chinese");
Check(!UiText.TryNormalizeLang("", out _) && !UiText.TryNormalizeLang("fr", out _) &&
    UiText.TryNormalizeLang("ZH", out var zhLang) && zhLang == "zh" &&
    UiText.TryNormalizeLang("EN-us", out var enLang) && enLang == "en",
    "API language validation accepts zh/en only");
Check(UiText.SetLang("English") && UiText.IsEnglish && UiText.Lang == "en",
    "switching to English takes effect");
Check(UiText.S("中文", "English") == "English", "English text is selected after switching");
Check(UiText.F("共 {0} 个", "{0} items", 3) == "3 items", "English format string is selected after switching");
Check(!UiText.SetLang("en"), "setting the same language reports no change");
UiText.SetLang(null);
Check(UiText.Lang == "zh" && UiText.S("中文", "English") == "中文" && !UiText.IsEnglish,
    "unknown value falls back to Chinese");

// ── DeviceChangeWatcher: WASAPI must accept the registration and actually
//    deliver an event. Nudging the default playback volume (then restoring it)
//    is a harmless way to force one PropertyValueChanged callback.
{
    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var watcher = new DeviceChangeWatcher();
    var fired = new ManualResetEventSlim(false);
    watcher.Changed += () => fired.Set();
    enumerator.RegisterEndpointNotificationCallback(watcher);
    Check(true, "device change watcher registration accepted");
    using var speaker = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Console);
    var original = speaker.AudioEndpointVolume.MasterVolumeLevelScalar;
    speaker.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(original + 0.02f, 0.05f, 1f);
    var delivered = fired.Wait(2000);
    speaker.AudioEndpointVolume.MasterVolumeLevelScalar = original;
    enumerator.UnregisterEndpointNotificationCallback(watcher);
    Check(delivered, "device change notification delivered within 2s");
}

// ── Elevated OBS helper: that separate process has to apply the saved language
//    from voiceaec.json before it can produce install/uninstall messages.
var langSandbox = Path.Combine(Path.GetTempPath(), "VoiceAec-lang-tests-" + Guid.NewGuid().ToString("N"));
var fallbackCfg = Path.Combine(langSandbox, "localappdata", "voiceaec.json");
var portableCfg = Path.Combine(langSandbox, "portable", "voiceaec.json");
Directory.CreateDirectory(Path.GetDirectoryName(fallbackCfg)!);
Directory.CreateDirectory(Path.GetDirectoryName(portableCfg)!);
try
{
    UiText.SetLang(null);
    Check(UiText.ResolveConfigPath(fallbackCfg, portableCfg) == fallbackCfg,
        "helper defaults to the LocalAppData config path");
    File.WriteAllText(portableCfg, "{\"lang\":\"en\"}");
    Check(UiText.ResolveConfigPath(fallbackCfg, portableCfg) == portableCfg,
        "helper reads the portable config when no LocalAppData config exists");
    File.WriteAllText(fallbackCfg, "{\"lang\":\"zh\"}");
    Check(UiText.ResolveConfigPath(fallbackCfg, portableCfg) == fallbackCfg,
        "helper prefers the LocalAppData config when both exist");

    File.WriteAllText(fallbackCfg, "{\"lang\":\"en\"}");
    Check(UiText.ApplySavedLanguage(fallbackCfg) && UiText.Lang == "en",
        "helper applies the persisted English language");
    Check(UiText.S("安装到 OBS 需要管理员权限。", "Administrator permission is required to install into OBS.")
            .StartsWith("Administrator", StringComparison.Ordinal),
        "helper result messages follow the persisted language");
    File.WriteAllText(fallbackCfg, "{\"lang\":\"fr\"}");
    Check(UiText.ApplySavedLanguage(fallbackCfg) && UiText.Lang == "zh",
        "unknown persisted language falls back to Chinese");
    File.WriteAllText(fallbackCfg, "{\"mic\":\"x\"}");
    Check(!UiText.ApplySavedLanguage(fallbackCfg) && UiText.Lang == "zh",
        "config without lang keeps Chinese");
    File.WriteAllText(fallbackCfg, "not json");
    Check(!UiText.ApplySavedLanguage(fallbackCfg) && UiText.Lang == "zh",
        "unreadable config keeps Chinese");
    File.WriteAllText(fallbackCfg, "{\"lang\":\"English\"}");
    Check(UiText.ApplySavedLanguage(fallbackCfg) && UiText.IsEnglish,
        "helper applies the en-prefix rule to the persisted value");
    File.Delete(fallbackCfg);
    Check(UiText.ApplySavedLanguage(fallbackCfg) && UiText.Lang == "zh",
        "missing config keeps Chinese");
}
finally
{
    UiText.SetLang(null);
    try { Directory.Delete(langSandbox, true); } catch { }
}

using var primary = new SingleInstanceGuard();
Check(primary.IsPrimary, "first launch acquires instance mutex");
using var secondary = new SingleInstanceGuard();
Check(!secondary.IsPrimary && primary.Activation.WaitOne(1000),
    "second launch requests activation without owning audio");

ObsInstallerTests.Run();
