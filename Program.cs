using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceAec;

if (args.Contains("--obs-plugin-action"))
{
    // The helper runs in its own (usually elevated) process before the normal
    // config initialization below. Apply the persisted language first so every
    // install/uninstall result message uses the user's interface language.
    UiText.ApplySavedLanguage();
    return ObsPluginManager.RunHelper(args);
}

// A GUI-subsystem executable never gets a console window; --console/--list
// attach to the caller's console (or allocate one) and re-open the streams.
if (args.Contains("--console") || args.Contains("--list"))
{
    if (GetConsoleWindow() == IntPtr.Zero && !AttachConsole(0xFFFFFFFF)) AllocConsole();
    var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
    Console.SetOut(stdout);
    var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
    Console.SetError(stderr);
}

[DllImport("kernel32.dll")]
static extern IntPtr GetConsoleWindow();

[DllImport("kernel32.dll")]
static extern bool AttachConsole(uint processId);

[DllImport("kernel32.dll")]
static extern bool AllocConsole();

// Set process DPI awareness before either the tray or WebView creates HWNDs.
Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.EnableVisualStyles();

var argsList = args.ToList();
string Arg(string name, string fallback)
{
    var i = argsList.IndexOf(name);
    return i >= 0 && i + 1 < argsList.Count ? argsList[i + 1] : fallback;
}

// ── 设备配置持久化:命令行 > voiceaec.json > 智能默认 ────────────────
var portableCfgPath = Path.Combine(AppContext.BaseDirectory, "voiceaec.json");
var fallbackCfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceAec", "voiceaec.json");
static bool CanWriteDirectory(string directory)
{
    try
    {
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, ".voiceaec-write-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe)) { }
        File.Delete(probe);
        return true;
    }
    catch { return false; }
}
// A per-machine install under Program Files cannot store settings next to the
// exe; fall back to LocalAppData so both install shapes behave the same.
var cfgPath = File.Exists(fallbackCfgPath) ? fallbackCfgPath
    : CanWriteDirectory(AppContext.BaseDirectory) ? portableCfgPath : fallbackCfgPath;
var cfgLock = new object();
Dictionary<string, string> LoadCfg()
{
    try
    {
        if (File.Exists(cfgPath))
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(cfgPath)) ?? [];
    }
    catch { }
    return [];
}
var cfg = LoadCfg();
// The stored language is applied before anything user-visible is produced;
// missing or invalid values fall back to Chinese.
UiText.SetLang(cfg.GetValueOrDefault("lang"));
void SaveCfg()
{
    lock (cfgLock)
    {
        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        foreach (var path in new[] { cfgPath, fallbackCfgPath }.Distinct())
        {
            string? temporary = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, json);
                File.Move(temporary, path, overwrite: true);
                cfgPath = path;
                return;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[config] {path}: {ex.Message}"); }
            finally { if (temporary != null) try { File.Delete(temporary); } catch { } }
        }
    }
}
bool HasArg(string name) => argsList.Contains(name);

var pluginData = @"C:\ProgramData\obs-studio\plugins\obs-localvqe\data";
var modelDirs = new[] { Path.Combine(AppContext.BaseDirectory, "models"), pluginData };
var modelFiles = new Dictionary<string, string>
{
    ["v1.2"] = "localvqe-v1.2-1.3M-f32.gguf",
    ["v1.3"] = "localvqe-v1.3-4.8M-f32.gguf",
    ["v1.4"] = "localvqe-v1.4-aec-200K-f32.gguf",
};
string? ModelPath(string m) => modelFiles.TryGetValue(m, out var f)
    ? modelDirs.Select(d => Path.Combine(d, f)).FirstOrDefault(File.Exists)
    : null;
var modelKeys = new[] { "off", "v1.2", "v1.3", "v1.4" };
bool KnownMode(string m) => modelKeys.Contains(m);
string ModelLabel(string m) => m switch
{
    "off" => UiText.S("关闭（直通）", "Off (passthrough)"),
    "v1.2" => UiText.S("均衡模式", "Balanced mode"),
    "v1.3" => UiText.S("强力模式", "Aggressive mode"),
    "v1.4" => UiText.S("纯回声消除模式", "Echo-only mode"),
    _ => m,
};

if (argsList.Contains("--list"))
{
    var enumDevices = new MMDeviceEnumerator();
    Console.WriteLine(UiText.S("== 采集设备 ==", "== Capture =="));
    foreach (var d in enumDevices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        Console.WriteLine($"  {d.FriendlyName}");
    Console.WriteLine(UiText.S("== 播放设备 ==", "== Render =="));
    foreach (var d in enumDevices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        Console.WriteLine($"  {d.FriendlyName}");
    return 0;
}

// ── 局域网面板:只枚举私有 IPv4,优先 192.168.x.x ─────────────────────
var lanAddresses = new List<IPAddress>();
try
{
    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (nic.OperationalStatus != OperationalStatus.Up) continue;
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
        foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
        {
            var address = unicast.Address;
            if (address.AddressFamily == AddressFamily.InterNetwork && LocalApiPolicy.IsPrivate(address))
                lanAddresses.Add(address);
        }
    }
}
catch (Exception ex) { Console.Error.WriteLine($"[lan] enumerate: {ex.Message}"); }
lanAddresses = lanAddresses.Distinct().ToList();
var primaryLan = lanAddresses
    .OrderBy(a => a.GetAddressBytes()[0] == 192 ? 0 : a.GetAddressBytes()[0] == 10 ? 1 : 2)
    .FirstOrDefault();
var lanEnabled = !argsList.Contains("--no-lan") && primaryLan != null;

// Acquire before loading models or opening audio devices. A second launch
// only wakes the primary UI, and cannot mix a second signal into VB-CABLE.
using var instance = new SingleInstanceGuard();
if (!instance.IsPrimary) return 0;

var micSub = HasArg("--mic") ? Arg("--mic", "") : cfg.GetValueOrDefault("mic", "");
var refSub = HasArg("--ref") ? Arg("--ref", "") : cfg.GetValueOrDefault("ref", "");
var ref2Sub = HasArg("--ref2") ? Arg("--ref2", "") : cfg.GetValueOrDefault("ref2", "");
var outSub = HasArg("--out") ? Arg("--out", "") : cfg.GetValueOrDefault("out", "");
var threads = int.Parse(Arg("--threads", "2"));
var startMode = Arg("--model", cfg.GetValueOrDefault("model", "v1.2"));
var startModeFromConfig = !HasArg("--model");
var port = int.Parse(Arg("--port", "8765"));
var noBrowser = argsList.Contains("--nobrowser");
var trayMode = argsList.Contains("--tray");     // 后台托盘运行：隐藏控制台 + 不开浏览器
var keepConsole = argsList.Contains("--console"); // 调试：保留控制台窗口
var paused = argsList.Contains("--start-paused") || cfg.GetValueOrDefault("paused") == "1" ? 1 : 0;
using var shutdown = new CancellationTokenSource();
using var audioEnabled = new ManualResetEventSlim(paused == 0);
if (trayMode) noBrowser = true;

if (!keepConsole && !argsList.Contains("--list"))
    TraySupport.HideConsole();

Console.WriteLine("VoiceAec");
Console.WriteLine($"  mic:{micSub}  ref:{refSub} + {ref2Sub}  out:{outSub}  threads:{threads}  mode:{startMode}");

var enumerator = new MMDeviceEnumerator();
MMDevice Find(DataFlow flow, string substring)
{
    var hits = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
        .Where(d => d.FriendlyName.Contains(substring, StringComparison.OrdinalIgnoreCase)).ToList();
    if (hits.Count == 0)
        throw new Exception(flow == DataFlow.Capture
            ? UiText.F("没有找到匹配「{0}」的可用采集设备（可用 --list 查看）",
                "no active capture device matching '{0}' (use --list)", substring)
            : UiText.F("没有找到匹配「{0}」的可用播放设备（可用 --list 查看）",
                "no active playback device matching '{0}' (use --list)", substring));
    return hits[0];
}

// 智能默认:未配置时取系统默认通信麦克风 + 默认播放设备;输出必须是 CABLE Input
if (micSub.Length == 0)
{
    try { micSub = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).FriendlyName; }
    catch { micSub = UiText.S("麦克风", "Microphone"); }
}
if (refSub.Length == 0)
{
    try { refSub = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).FriendlyName; }
    catch { refSub = UiText.S("扬声器", "Speakers"); }
}
if (ref2Sub.Length == 0) ref2Sub = "none";
if (outSub.Length == 0) outSub = "CABLE Input";

static string ShortName(string fn) => fn.Contains('(') ? fn[..fn.IndexOf('(')].Trim() : fn;

MMDevice? FindAny(string substring)
{
    var hits = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
        .Where(d => d.FriendlyName.Contains(substring, StringComparison.OrdinalIgnoreCase)).ToList();
    if (hits.Count > 0) return hits[0];
    var caps = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
        .Where(d => d.FriendlyName.Contains(substring, StringComparison.OrdinalIgnoreCase)).ToList();
    return caps.Count > 0 ? caps[0]
        : throw new Exception(UiText.F("没有找到匹配「{0}」的可用设备（可用 --list 查看）",
            "no active device matching '{0}' (use --list)", substring));
}

var micConfigured = HasArg("--mic") || cfg.ContainsKey("mic");
var refConfigured = HasArg("--ref") || cfg.ContainsKey("ref");
var outConfigured = HasArg("--out") || cfg.ContainsKey("out");
// A notice keeps the configured device name that went missing; its text is
// rendered on demand so a runtime language switch takes effect immediately.
string? micNoticeSource = null;
string? refNoticeSource = null;
string? outNoticeSource = null;
var devicesMissingNotice = false;

MMDevice? TryDefaultDevice(DataFlow flow, Role role)
{
    try { return enumerator.GetDefaultAudioEndpoint(flow, role); }
    catch (Exception ex)
    {
        Console.WriteLine($"[audio] default {flow} device unavailable: {ex.Message}");
        return null;
    }
}

MMDevice? micDevice;
var micFallback = false;
try { micDevice = Find(DataFlow.Capture, micSub); }
catch (Exception)
{
    micFallback = true;
    micDevice = TryDefaultDevice(DataFlow.Capture, Role.Communications);
    if (micDevice != null && micConfigured)
        micNoticeSource = micSub;   // 全名:区分信息常在括号里(如"麦克风 (Xmic Z3)")
}

MMDevice? refDevice;
var refFallback = false;
try { refDevice = FindAny(refSub); }   // 主参考也允许麦克风(如近讲麦场景);默认仍是播放设备
catch (Exception)
{
    refFallback = true;
    refDevice = TryDefaultDevice(DataFlow.Render, Role.Console);
    if (refDevice != null && refConfigured)
        refNoticeSource = refSub;
}

MMDevice? ref2Device = null;
if (!ref2Sub.Equals("none", StringComparison.OrdinalIgnoreCase))
{
    try { ref2Device = FindAny(ref2Sub); }   // ref2 可为播放设备回采或另一只麦克风(噪声参考)
    catch { Console.WriteLine("  (ref2 not found, single-ref)"); }
}

MMDevice? outDevice = null;
var cableMissing = false;
var outFallback = false;
try
{
    outDevice = Find(DataFlow.Render, outSub);
}
catch (Exception)
{
    if (outSub.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
    {
        // VB-CABLE may expose a slightly different FriendlyName after an
        // update; match the device family before declaring it missing.
        try
        {
            outDevice = Find(DataFlow.Render, "CABLE");
            outSub = outDevice.FriendlyName;
        }
        catch
        {
            cableMissing = true;
            outDevice = TryDefaultDevice(DataFlow.Render, Role.Multimedia);
        }
    }
    else
    {
        outFallback = true;
        outDevice = TryDefaultDevice(DataFlow.Render, Role.Multimedia);
        if (outDevice != null && outConfigured)
            outNoticeSource = outSub;
    }
}

var devicesMissing = micDevice == null || refDevice == null || outDevice == null;
if (devicesMissing) devicesMissingNotice = true;

// Persist only real devices; a temporary fallback keeps the user's original
// choice so it comes back when the device does.
if (!micFallback && micDevice != null) cfg["mic"] = micDevice.FriendlyName;
if (!refFallback && refDevice != null) cfg["ref"] = refDevice.FriendlyName;
if (ref2Sub.Equals("none", StringComparison.OrdinalIgnoreCase) || ref2Device != null)
    cfg["ref2"] = ref2Device?.FriendlyName ?? "none";
if (!cableMissing && !outFallback && outDevice != null) cfg["out"] = outDevice.FriendlyName;
SaveCfg();

// Never route the processed microphone to physical speakers just because the
// virtual cable is missing, and never start audio without the devices it needs.
if (cableMissing || devicesMissing)
{
    paused = 1;
    audioEnabled.Reset();
    Console.WriteLine($"[audio] start paused (cableMissing={cableMissing}, devicesMissing={devicesMissing})");
}

// ── engine / mode management ─────────────────────────────────────────
const int Rate = 16000;
const int Hop = 256;
const int HopBytes = Hop * sizeof(float);

var engineLock = new object();
LocalVqe.Context? engine = null;
var mode = "off";
var switching = false;
var modeSwitchLock = new object();

bool ModelExists(string m) => m == "off" || ModelPath(m) != null;

void PersistKey(string key, string value)
{
    lock (cfgLock) cfg[key] = value;
    SaveCfg();
}

void PersistMode(string requested) => PersistKey("model", requested);

string SwitchModel(string requested, bool persist = true)
{
    lock (modeSwitchLock) return SwitchModelCore(requested, persist);
}

string SwitchModelCore(string requested, bool persist)
{
    if (!KnownMode(requested))
        return UiText.F("未知模式 '{0}'", "Unknown mode '{0}'", requested);
    if (!ModelExists(requested))
        return UiText.F("模型文件不存在: {0}(已查找 {1})", "Model file not found: {0} (searched {1})",
            modelFiles[requested], string.Join(UiText.S("、", ", "), modelDirs));
    Volatile.Write(ref switching, true);
    try
    {
        if (requested == "off")
        {
            lock (engineLock)
            {
                engine?.Dispose();
                engine = null;
                mode = "off";
            }
            Console.WriteLine("[mode] off (passthrough)");
            if (persist) PersistMode("off");
            return "";
        }
        var fresh = LocalVqe.Context.Create(ModelPath(requested)!, threads);
        LocalVqe.Context? old = null;
        lock (engineLock)
        {
            old = engine;
            engine = fresh;
            mode = requested;
        }
        old?.Dispose();
        Console.WriteLine($"[mode] {requested} loaded (sr={fresh.SampleRate} hop={fresh.Hop})");
        if (persist) PersistMode(requested);
        return "";
    }
    catch (Exception ex)
    {
        return UiText.F("加载失败: {0}", "Failed to load: {0}", ex.Message);
    }
    finally { Volatile.Write(ref switching, false); }
}

{
    if (startModeFromConfig && (!KnownMode(startMode) || !ModelExists(startMode)))
    {
        Console.WriteLine($"[mode] saved mode '{startMode}' unavailable -> v1.2");
        startMode = "v1.2";
    }
    var err = SwitchModel(startMode, persist: false);
    if (err != "") Console.WriteLine($"[mode] init failed -> off: {err}");
}

var target16k = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);
var micRing = new SampleRing(Rate * 4 * 4);
var refRing = new SampleRing(Rate / 2 * 4);
var ref2Ring = new SampleRing(Rate / 2 * 4);   // 始终创建,便于运行时热加 ref2
var outRing = new SampleRing(Rate * 2 * 4);
using var inputReady = new AutoResetEvent(false);
var devSwitchLock = new object();

// 设备拔出时 WASAPI 会话直接死亡且无声；会话守卫与插拔通知都汇到这里排队自愈
var recoveryRequested = new AutoResetEvent(false);
void QueueRecovery() => recoveryRequested.Set();
string currentMic = micDevice?.FriendlyName ?? "", currentRef = refDevice?.FriendlyName ?? "";
string currentRef2 = ref2Device?.FriendlyName ?? "none", currentOut = outDevice?.FriendlyName ?? "";

long hops = 0, passthroughs = 0, outUnderruns = 0, refTrims = 0, refZeroHops = 0;
double micDb = -120, outDb = -120, refDb = -120;
long refActiveTick = 0;

CaptureSession StartCapture(MMDevice dev, SampleRing ring, string label)
{
    return new CaptureSession(dev, ring, target16k, label == "mic" ? () => inputReady.Set() : null);
}

static double RmsDb(float[] buf)
{
    double s = 0;
    for (var i = 0; i < buf.Length; i++) s += (double)buf[i] * buf[i];
    var rms = Math.Sqrt(s / buf.Length);
    return rms <= 0 ? -120 : Math.Max(-120, Math.Min(0, 20 * Math.Log10(rms)));
}

CaptureSession? micCap = null, refCap = null, ref2Cap = null;
WasapiOut? waveOut = null;
// 通道死亡标记：仅表示"当前绑定的会话已停止出声"，自愈成功或用户换设备后清除
bool micDead = false, refDead = false, ref2Dead = false, outDead = false;
long micSilentSince = 0;

// 守卫在会话所属线程回调，绝不拿 devSwitchLock（自愈会在持锁时 Dispose 旧会话，
// 那里要 join 会话线程，等锁会死锁）；引用比较足够判断"还是当前绑定的那个会话"。
void GuardCapture(string label, CaptureSession session)
{
    session.Stopped += () =>
    {
        try
        {
            if (Volatile.Read(ref paused) != 0) return;
            var alive = label switch
            {
                "mic" => ReferenceEquals(micCap, session),
                "ref" => ReferenceEquals(refCap, session),
                _ => ReferenceEquals(ref2Cap, session),
            };
            if (!alive) return;
            if (label == "mic") micDead = true;
            else if (label == "ref") refDead = true;
            else ref2Dead = true;
            QueueRecovery();
        }
        catch { }
    };
}

void GuardOutput(WasapiOut output)
{
    output.PlaybackStopped += (_, _) =>
    {
        try
        {
            if (Volatile.Read(ref paused) != 0) return;
            if (!ReferenceEquals(waveOut, output)) return;
            outDead = true;
            QueueRecovery();
        }
        catch { }
    };
}
void TrimRef(SampleRing r)
{
    var excess = r.BufferedBytes - Rate / 2 * 4;
    if (excess <= HopBytes) return;
    var scratch = new byte[excess];
    r.Read(scratch, 0, excess);
    Interlocked.Increment(ref refTrims);
}

void DisposeAudio(IDisposable? resource)
{
    try { resource?.Dispose(); }
    catch (Exception ex) { Console.Error.WriteLine($"[audio] dispose: {ex.Message}"); }
}

WasapiOut StartOutput(MMDevice device)
{
    var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
    try
    {
        output.Init(new CableFeedProvider(outRing, () =>
        {
            if (Volatile.Read(ref paused) == 0) Interlocked.Increment(ref outUnderruns);
        }));
        output.Play();
        return output;
    }
    catch { DisposeAudio(output); throw; }
}

void ClearAudioBuffers()
{
    micRing.Clear(); refRing.Clear(); ref2Ring.Clear(); outRing.Clear();
    micDb = refDb = outDb = -120;
    Interlocked.Exchange(ref refActiveTick, 0);
}

// The API reports a channel-specific error without repeating the generic
// "device switch failed" prefix, in whichever language produced it.
static string StripDeviceSwitchPrefix(string error)
{
    foreach (var prefix in new[] { "设备切换失败: ", "Device switch failed: " })
        if (error.StartsWith(prefix, StringComparison.Ordinal)) return error[prefix.Length..];
    return error;
}

// 重绑一个采集通道到 name。force=true 时忽略"名字没变"直接重开（自愈用）；
// 失败时抛异常且原绑定保持不动。成功后清除该通道的死亡标记。
void ReplaceCapture(string name, DataFlow? flow, SampleRing ring, string label,
    ref MMDevice? device, ref CaptureSession? capture, ref string selected, bool force = false)
{
    if (!force && name == selected) return;
    MMDevice? freshDevice = name.Equals("none", StringComparison.OrdinalIgnoreCase)
        && flow == null ? null : (flow == null ? FindAny(name) : Find(flow.Value, name));
    CaptureSession? freshCapture = null;
    try
    {
        var freshName = freshDevice?.FriendlyName ?? "none";
        if (freshDevice != null && Volatile.Read(ref paused) == 0)
        {
            freshCapture = StartCapture(freshDevice, ring, label);
            GuardCapture(label, freshCapture);
        }
        var oldDevice = device; var oldCapture = capture;
        device = freshDevice; capture = freshCapture; selected = freshName;
        DisposeAudio(oldCapture); DisposeAudio(oldDevice);
        ring.Clear();
        if (label == "mic") micDead = false;
        else if (label == "ref") refDead = false;
        else ref2Dead = false;
    }
    catch { DisposeAudio(freshCapture); DisposeAudio(freshDevice); throw; }
}

// 重绑输出设备并重启输出；失败时抛异常且原绑定保持不动。
void ReplaceOutput(string name)
{
    var freshDevice = Find(DataFlow.Render, name);
    WasapiOut? freshOutput = null;
    try
    {
        var freshName = freshDevice.FriendlyName;
        if (Volatile.Read(ref paused) == 0)
        {
            freshOutput = StartOutput(freshDevice);
            GuardOutput(freshOutput);
        }
        var oldOutput = waveOut; var oldDevice = outDevice;
        waveOut = freshOutput; outDevice = freshDevice; currentOut = freshName;
        DisposeAudio(oldOutput); DisposeAudio(oldDevice);
        outDead = false;
    }
    catch { DisposeAudio(freshOutput); DisposeAudio(freshDevice); throw; }
}

string RecheckDevices()
{
    var micTarget = (micNoticeSource != null || micDevice == null) ? cfg.GetValueOrDefault("mic", "") : null;
    var refTarget = (refNoticeSource != null || refDevice == null) ? cfg.GetValueOrDefault("ref", "") : null;
    string? outTarget = null;
    if (cableMissing) outTarget = "CABLE Input";
    else if (outNoticeSource != null || outDevice == null) outTarget = cfg.GetValueOrDefault("out", "");
    if (string.IsNullOrEmpty(micTarget)) micTarget = null;
    if (string.IsNullOrEmpty(refTarget)) refTarget = null;
    if (string.IsNullOrEmpty(outTarget)) outTarget = null;
    if (micTarget == null && refTarget == null && outTarget == null) return "";
    var error = SwitchDevices(micTarget, refTarget, null, outTarget);
    return error == "" ? "" : StripDeviceSwitchPrefix(error);
}

string SetPaused(bool value, bool persist = true)
{
    lock (devSwitchLock)
    {
        if (value && Volatile.Read(ref paused) != 0) return "";
        if (!value && Volatile.Read(ref paused) == 0 && micCap != null) return "";
        Volatile.Write(ref paused, 1);
        audioEnabled.Reset();
        if (value)
        {
            DisposeAudio(waveOut); waveOut = null;
            DisposeAudio(micCap); micCap = null;
            DisposeAudio(refCap); refCap = null;
            DisposeAudio(ref2Cap); ref2Cap = null;
            ClearAudioBuffers();
            if (persist) PersistKey("paused", "1");
            return "";
        }

        if (cableMissing)
        {
            try
            {
                var cable = Find(DataFlow.Render, "CABLE");
                outDevice = cable;
                currentOut = cable.FriendlyName;
                outFallback = false;
                cableMissing = false;
                outNoticeSource = null;
                cfg["out"] = cable.FriendlyName;
                SaveCfg();
                Console.WriteLine("[audio] VB-CABLE is back; output rebound");
            }
            catch
            {
                return UiText.S("没有找到 VB-CABLE，已保持暂停。请先安装虚拟声卡，或在输出下拉里选择其他设备。",
                    "VB-CABLE was not found; audio stays paused. Install the virtual cable first, or pick another device in the Output dropdown.");
            }
        }
        if (micDevice == null || refDevice == null || outDevice == null)
            return UiText.S("缺少音频设备：请先在设备下拉里选择麦克风 / 参考 / 输出，然后再恢复。",
                "Audio devices are missing: choose Microphone / Reference / Output in the device dropdowns, then resume.");

        // Publish the complete resumed pipeline only after every resource
        // started successfully. Failure leaves the application safely muted.
        CaptureSession? newMic = null, newRef = null, newRef2 = null;
        WasapiOut? newOutput = null;
        try
        {
            ClearAudioBuffers();
            lock (engineLock) engine?.Reset();
            newMic = StartCapture(micDevice, micRing, "mic");
            newRef = StartCapture(refDevice, refRing, "ref");
            if (ref2Device != null) newRef2 = StartCapture(ref2Device, ref2Ring, "ref2");
            newOutput = StartOutput(outDevice);
            GuardCapture("mic", newMic);
            GuardCapture("ref", newRef);
            if (newRef2 != null) GuardCapture("ref2", newRef2);
            GuardOutput(newOutput);
            micCap = newMic; refCap = newRef; ref2Cap = newRef2; waveOut = newOutput;
            micDead = refDead = ref2Dead = outDead = false;
            Volatile.Write(ref paused, 0);
            audioEnabled.Set();
            if (persist) PersistKey("paused", "0");
            return "";
        }
        catch (Exception ex)
        {
            DisposeAudio(newOutput); DisposeAudio(newMic); DisposeAudio(newRef); DisposeAudio(newRef2);
            ClearAudioBuffers();
            return UiText.F("恢复失败，已保持静音: {0}", "Failed to resume; audio stays muted: {0}", ex.Message);
        }
    }
}

if (Volatile.Read(ref paused) == 0)
{
    var error = SetPaused(false);
    if (error != "") Console.Error.WriteLine(error);
}

// ── 设备热切换:采集端/输出端重绑,推理引擎与 ring 不动 ────────────────
string SwitchDevices(string? micName, string? refName, string? ref2Name, string? outName)
{
    lock (devSwitchLock)
    {
        var errors = new List<string>();
        void Change(string label, Action action)
        {
            try { action(); }
            catch (Exception ex) { errors.Add($"{label}: {ex.Message}"); }
        }
        var beforeMic = currentMic; var beforeRef = currentRef;
        var beforeRef2 = currentRef2; var beforeOut = currentOut;
        var micOk = false; var refOk = false; var outOk = false;
        if (!string.IsNullOrEmpty(micName))
        {
            var n = errors.Count;
            Change(UiText.S("麦克风", "Microphone"), () =>
                ReplaceCapture(micName, DataFlow.Capture, micRing, "mic", ref micDevice, ref micCap, ref currentMic));
            micOk = errors.Count == n;
        }
        if (!string.IsNullOrEmpty(refName))
        {
            var n = errors.Count;
            Change(UiText.S("参考回采", "Reference loopback"), () =>
                ReplaceCapture(refName, null, refRing, "ref", ref refDevice, ref refCap, ref currentRef));
            refOk = errors.Count == n;
        }
        if (ref2Name != null) Change(UiText.S("第二参考", "Second reference"), () =>
            ReplaceCapture(ref2Name, null, ref2Ring, "ref2", ref ref2Device, ref ref2Cap, ref currentRef2));
        if (!string.IsNullOrEmpty(outName))
        {
            var n = errors.Count;
            if (outName != currentOut) Change(UiText.S("输出", "Output"), () => ReplaceOutput(outName));
            outOk = errors.Count == n;
        }
        // An explicit, successful selection resolves that channel, even when
        // it picked the device already in use.
        if (micOk) micNoticeSource = null;
        if (refOk) refNoticeSource = null;
        if (outOk) { outNoticeSource = null; cableMissing = false; }
        // Only persist what this request actually changed; a fallback that is
        // still in effect must not overwrite the user's original choice.
        var changed = false;
        if (currentMic != beforeMic) { cfg["mic"] = currentMic; changed = true; micNoticeSource = null; }
        if (currentRef != beforeRef) { cfg["ref"] = currentRef; changed = true; refNoticeSource = null; }
        if (currentRef2 != beforeRef2) { cfg["ref2"] = currentRef2; changed = true; }
        if (currentOut != beforeOut)
        {
            cfg["out"] = currentOut; changed = true;
            outNoticeSource = null;
            // An explicit output choice (including physical speakers) ends the
            // missing-cable state; only automatic fallbacks keep it.
            cableMissing = false;
        }
        if (micDevice != null && refDevice != null && outDevice != null) devicesMissingNotice = false;
        if (changed) SaveCfg();
        return errors.Count == 0 ? ""
            : UiText.S("设备切换失败: ", "Device switch failed: ") + string.Join(UiText.S("；", "; "), errors);
    }
}

// 设备插拔自愈：拔出时 WASAPI 会话死亡（无声、电平静止），插回后把死掉的通道
// 重绑到当前选择的同名设备并重启，免去手动"暂停→恢复"。
void RecoverAudio(bool eventDriven)
{
    lock (devSwitchLock)
    {
        if (Volatile.Read(ref paused) != 0)
        {
            // 只有启动时因缺设备而暂停才自动恢复；用户手动暂停的不打扰
            if (!eventDriven || !devicesMissingNotice) return;
            try { RecheckDevices(); SetPaused(false); }
            catch (Exception ex) { Console.Error.WriteLine($"[audio] auto-resume: {ex.Message}"); }
            return;
        }

        // 兜底：就算 RecordingStopped 没触发（个别驱动会让采集线程卡住），
        // 未暂停时麦克风环形缓冲持续 5 秒颗粒无收也算死亡。回采通道不适用——
        // 系统静音时 loopback 本来就不出包。
        void NoteSilence(ref long since, SampleRing ring, ref bool dead)
        {
            if (ring.BufferedBytes >= HopBytes) { since = 0; return; }
            if (since == 0) { since = Environment.TickCount64; return; }
            if (Environment.TickCount64 - since > 5000) { dead = true; since = 0; }
        }
        if (micCap != null) NoteSilence(ref micSilentSince, micRing, ref micDead);

        if (micDead && !string.IsNullOrEmpty(currentMic))
            try { ReplaceCapture(currentMic, DataFlow.Capture, micRing, "mic", ref micDevice, ref micCap, ref currentMic, force: true); }
            catch (Exception ex) { Console.Error.WriteLine($"[audio] recover mic: {ex.Message}"); }
        if (refDead && !string.IsNullOrEmpty(currentRef))
            try { ReplaceCapture(currentRef, null, refRing, "ref", ref refDevice, ref refCap, ref currentRef, force: true); }
            catch (Exception ex) { Console.Error.WriteLine($"[audio] recover ref: {ex.Message}"); }
        if (ref2Dead && !string.IsNullOrEmpty(currentRef2) && !currentRef2.Equals("none", StringComparison.OrdinalIgnoreCase))
            try { ReplaceCapture(currentRef2, null, ref2Ring, "ref2", ref ref2Device, ref ref2Cap, ref currentRef2, force: true); }
            catch (Exception ex) { Console.Error.WriteLine($"[audio] recover ref2: {ex.Message}"); }
        if (outDead && !string.IsNullOrEmpty(currentOut))
            try { ReplaceOutput(currentOut); }
            catch (Exception ex) { Console.Error.WriteLine($"[audio] recover out: {ex.Message}"); }
    }
}

var recoveryThread = new Thread(() =>
{
    while (!shutdown.IsCancellationRequested)
    {
        var eventDriven = recoveryRequested.WaitOne(2000);
        if (shutdown.IsCancellationRequested) break;
        if (eventDriven)
        {
            Thread.Sleep(400);   // 插拔一次会连发多条通知，等它收敛
            try { recoveryRequested.Reset(); } catch { }
        }
        try { RecoverAudio(eventDriven); }
        catch (Exception ex) { Console.Error.WriteLine($"[audio] recover: {ex.Message}"); }
    }
}) { IsBackground = true };
recoveryThread.Start();

Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };

var proc = new Thread(() =>
{
    Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
    var hopMic = new float[Hop];
    var hopRefA = new float[Hop];
    var hopRefB = new float[Hop];
    var hopRef = new float[Hop];
    var hopOut = new float[Hop];
    var micBytes = new byte[HopBytes];
    var refBytes = new byte[HopBytes];
    var outBytes = new byte[HopBytes];
    var swLog = Stopwatch.StartNew();
    var wakeHandles = new[] { inputReady, shutdown.Token.WaitHandle };
    while (!shutdown.IsCancellationRequested)
    {
        try { audioEnabled.Wait(shutdown.Token); }
        catch (OperationCanceledException) { break; }
        if (micRing.BufferedBytes < HopBytes) { WaitHandle.WaitAny(wakeHandles); continue; }
        lock (devSwitchLock)
        {
        if (Volatile.Read(ref paused) != 0 || micRing.BufferedBytes < HopBytes) continue;
        TrimRef(refRing);
        if (ref2Ring != null) TrimRef(ref2Ring);

        micRing.Read(micBytes, 0, HopBytes);
        Buffer.BlockCopy(micBytes, 0, hopMic, 0, HopBytes);

        var gotA = refRing.Read(refBytes, 0, HopBytes);
        if (gotA == HopBytes) Buffer.BlockCopy(refBytes, 0, hopRefA, 0, HopBytes);
        else Array.Clear(hopRefA);
        Array.Clear(hopRefB);
        if (ref2Ring != null)
        {
            var gotB = ref2Ring!.Read(refBytes, 0, HopBytes);
            if (gotB == HopBytes) Buffer.BlockCopy(refBytes, 0, hopRefB, 0, HopBytes);
        }

        var anyRef = false;
        for (var i = 0; i < Hop; i++)
        {
            var v = hopRefA[i] + hopRefB[i];
            if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
            hopRef[i] = v;
            anyRef |= v != 0f;
        }
        if (anyRef)
        {
            refDb = RmsDb(hopRef);
            refActiveTick = Environment.TickCount64;
        }
        else
        {
            refDb = -120;
            Interlocked.Increment(ref refZeroHops);
        }
        micDb = RmsDb(hopMic);

        lock (engineLock)
        {
            if (mode == "off" || engine == null)
            {
                Buffer.BlockCopy(hopMic, 0, hopOut, 0, HopBytes);
            }
            else
            {
                var rc = engine.Process(hopMic, hopRef, hopOut);
                if (rc != 0)
                {
                    Buffer.BlockCopy(hopMic, 0, hopOut, 0, HopBytes);
                    Interlocked.Increment(ref passthroughs);
                }
            }
        }
        outDb = RmsDb(hopOut);
        Buffer.BlockCopy(hopOut, 0, outBytes, 0, HopBytes);
        outRing.Write(outBytes, HopBytes);
        Interlocked.Increment(ref hops);

        if (swLog.ElapsedMilliseconds >= 10000)
        {
            swLog.Restart();
            Console.WriteLine($"[dbg] hops={Interlocked.Read(ref hops)} mic={micRing.BufferedBytes * 1000.0 / (Rate * 4):F0}ms ref={refRing.BufferedBytes * 1000.0 / (Rate * 4):F0}ms out={outRing.BufferedBytes * 1000.0 / (Rate * 4):F0}ms mode={mode}");
        }
        }
    }
}) { IsBackground = true };
proc.Start();

// ── HTTP control server ──────────────────────────────────────────────
var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var startAt = DateTime.UtcNow;
var self = Process.GetCurrentProcess();
var actualPort = port;
var lanAvailable = false;
var rateGate = new object();
long lastHops = 0; DateTime lastRateAt = DateTime.UtcNow; double hopsPerSec = 0;
double lastCpuMs = self.TotalProcessorTime.TotalMilliseconds; DateTime lastCpuAt = DateTime.UtcNow; double cpuPercent = 0;

object BuildStatus()
{
    lock (rateGate)
    {
        var now = DateTime.UtcNow;
        var dt = (now - lastRateAt).TotalSeconds;
        if (dt >= 1.0)
        {
            hopsPerSec = (Interlocked.Read(ref hops) - lastHops) / dt;
            lastHops = Interlocked.Read(ref hops);
            lastRateAt = now;
        }
        var dc = (now - lastCpuAt).TotalSeconds;
        if (dc >= 1.0)
        {
            self.Refresh();
            var cur = self.TotalProcessorTime.TotalMilliseconds;
            cpuPercent = (cur - lastCpuMs) / (dc * 1000.0 * Environment.ProcessorCount) * 100.0;
            lastCpuMs = cur;
            lastCpuAt = now;
        }
    }
    string curMode;
    lock (engineLock) curMode = mode;
    string micName, refName, ref2Name, outName, deviceNotice;
    lock (devSwitchLock)
    {
        var missingName = UiText.S("（未找到设备）", "(device not found)");
        micName = string.IsNullOrEmpty(currentMic) ? missingName : currentMic;
        refName = string.IsNullOrEmpty(currentRef) ? missingName : currentRef;
        ref2Name = currentRef2;
        outName = string.IsNullOrEmpty(currentOut) ? missingName : currentOut;
        // Notices are re-rendered on every poll so switching the UI language
        // updates the hint without waiting for the device state to change.
        var notices = new List<string>();
        if (micNoticeSource != null)
            notices.Add(UiText.F("麦克风「{0}」不在了，已临时改用「{1}」；插回后重启即可恢复。",
                "Microphone \"{0}\" is gone; temporarily using \"{1}\". Reconnect it and restart to restore.",
                micNoticeSource, currentMic));
        if (refNoticeSource != null)
            notices.Add(UiText.F("参考回采「{0}」不在了，已临时改用「{1}」；插回后重启即可恢复。",
                "Reference loopback \"{0}\" is gone; temporarily using \"{1}\". Reconnect it and restart to restore.",
                refNoticeSource, currentRef));
        if (outNoticeSource != null)
            notices.Add(UiText.F("输出设备「{0}」不在了，已临时改用「{1}」。",
                "Output device \"{0}\" is gone; temporarily using \"{1}\".",
                outNoticeSource, currentOut));
        if (devicesMissingNotice)
            notices.Add(UiText.S("没有找到可用的麦克风/扬声器设备，已暂停处理；接好设备后在面板选择并点「恢复处理与输出」。",
                "No usable microphone or speaker was found, so processing is paused. Connect your devices, pick them in the panel, then click \"Resume processing & output\"."));
        // 运行期拔出：与启动期缺设备不同，通道自愈在等设备插回。
        // 显示完整 FriendlyName——区分信息常在括号里(ShortName 会把"麦克风 (Xmic Z3)"截成"麦克风")。
        if (micDead && !string.IsNullOrEmpty(currentMic))
            notices.Add(UiText.F("麦克风「{0}」被拔出了，插回后声音会自动恢复。",
                "Microphone \"{0}\" was unplugged; audio resumes automatically once it is replugged.",
                currentMic));
        if (refDead && !string.IsNullOrEmpty(currentRef))
            notices.Add(UiText.F("参考回采「{0}」被拔出了，插回后声音会自动恢复。",
                "Reference loopback \"{0}\" was unplugged; audio resumes automatically once it is replugged.",
                currentRef));
        if (ref2Dead && !string.IsNullOrEmpty(currentRef2) && !currentRef2.Equals("none", StringComparison.OrdinalIgnoreCase))
            notices.Add(UiText.F("第二参考「{0}」被拔出了，插回后声音会自动恢复。",
                "Second reference \"{0}\" was unplugged; audio resumes automatically once it is replugged.",
                currentRef2));
        if (outDead && !string.IsNullOrEmpty(currentOut))
            notices.Add(UiText.F("输出设备「{0}」被拔出了，插回后声音会自动恢复。",
                "Output device \"{0}\" was unplugged; audio resumes automatically once it is replugged.",
                currentOut));
        deviceNotice = string.Join(" ", notices);
    }
    var appVersion = Assembly.GetEntryAssembly()!.GetName().Version?.ToString(3) ?? "dev";
    return new
    {
        ok = true,
        version = appVersion,
        lang = UiText.Lang,
        mode = curMode,
        switching = Volatile.Read(ref switching),
        paused = Volatile.Read(ref paused) != 0,
        uptimeSec = (int)(DateTime.UtcNow - startAt).TotalSeconds,
        cpuPercent = Math.Round(cpuPercent, 1),
        mic = new { name = micName, levelDb = Math.Round(micDb, 1), bufferMs = Math.Round(micRing.BufferedBytes * 1000.0 / (Rate * 4), 0) },
        @ref = new
        {
            name = refName,
            active = refActiveTick != 0 && Environment.TickCount64 - refActiveTick < 1500,
            levelDb = Math.Round(refDb, 1),
            bufferMs = Math.Round(refRing.BufferedBytes * 1000.0 / (Rate * 4), 0)
        },
        @out = new { name = outName, levelDb = Math.Round(outDb, 1), bufferMs = Math.Round(outRing.BufferedBytes * 1000.0 / (Rate * 4), 0) },
        stats = new
        {
            hops = Interlocked.Read(ref hops),
            processedSec = Math.Round(Interlocked.Read(ref hops) * Hop / (double)Rate, 1),
            hopsPerSec = Math.Round(hopsPerSec, 1),
            silentRefHops = Interlocked.Read(ref refZeroHops),
            underruns = Interlocked.Read(ref outUnderruns),
            trims = Interlocked.Read(ref refTrims),
            passthroughs = Interlocked.Read(ref passthroughs)
        },
        models = modelKeys.ToDictionary(k => k, k => new { available = ModelExists(k), label = ModelLabel(k) }),
        autostart = GetAutostart(),
        cableMissing,
        lanAvailable,
        lanUrl = lanAvailable && primaryLan != null ? $"http://{primaryLan}:{actualPort}/" : "",
        hint = cableMissing
            ? UiText.S("还差一步：没有找到 VB-CABLE 虚拟声卡。点上方「一键安装虚拟声卡」，装完重启电脑，语音软件里就能选到 CABLE Output。",
                "One more step: VB-CABLE is not installed. Click \"Install virtual audio cable\" above, restart your PC, then CABLE Output appears in your voice app.")
            : Volatile.Read(ref paused) != 0
            ? UiText.S("已暂停：现在不采集也不输出声音，点右上角「恢复处理与输出」就能继续。",
                "Paused: no audio is captured or played. Click \"Resume processing & output\" in the top-right corner to continue.")
            : deviceNotice.Length > 0
            ? deviceNotice
            : UiText.F("打开 QQ 或游戏语音，把「麦克风」选成 CABLE Output；声音继续从「{0}」播放，就能一边外放一边消除回声。",
                "Open QQ or your game voice app and set the microphone to CABLE Output. Audio keeps playing through \"{0}\", so you can use speakers and still cancel the echo.",
                ShortName(refName))
    };
}

HttpListener? listener = null;
void TryBind(IEnumerable<string> prefixes, int candidate)
{
    if (listener != null) return;
    foreach (var prefix in prefixes)
    {
        try
        {
            var l = new HttpListener();
            l.Prefixes.Add(prefix);
            l.Start();
            listener = l;
            actualPort = candidate;
            return;
        }
        catch { }
    }
}
for (var p = port; p < port + 10 && listener == null; p++)
    TryBind(lanEnabled ? new[] { $"http://+:{p}/" } : new[] { $"http://localhost:{p}/", $"http://127.0.0.1:{p}/" }, p);
// The wildcard reservation may be missing (portable copy without the URL ACL):
// keep the panel usable on this PC instead of failing to start.
if (listener == null && lanEnabled)
    for (var p = port; p < port + 10 && listener == null; p++)
        TryBind(new[] { $"http://localhost:{p}/", $"http://127.0.0.1:{p}/" }, p);
if (listener == null) throw new Exception(UiText.S("无法绑定 HTTP 监听端口（8765-8774）", "cannot bind HTTP listener (8765-8774)"));
lanAvailable = lanEnabled && listener.Prefixes.Any(x => x.Contains("+:"));
if (lanEnabled && !lanAvailable) Console.WriteLine("[lan] wildcard bind denied; panel is loopback-only");

var indexHtml = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
var apiToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
var obsPlugins = new ObsPluginManager();

// ── VB-CABLE helper: an unmodified official package, verified by hash ───
const string VbCableUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";
const string VbCableSha256 = "B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB";
var vbCableLock = new object();
var vbCableGate = new object();
var vbCableRunning = false;
var vbCableOk = false;
var vbCableMessage = "";
var vbCableReboot = false;
var vbCableCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceAec", "VbCable");

string EnsureVbCableSetup()
{
    var bundled = Path.Combine(AppContext.BaseDirectory, "vendor", "VBCABLE", "VBCABLE_Setup_x64.exe");
    if (File.Exists(bundled)) return bundled;

    var setup = Path.Combine(vbCableCache, "VBCABLE_Setup_x64.exe");
    if (File.Exists(setup)) return setup;

    Directory.CreateDirectory(vbCableCache);
    var zip = Path.Combine(vbCableCache, "VBCABLE_Driver_Pack45.zip");
    using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
        File.WriteAllBytes(zip, client.GetByteArrayAsync(VbCableUrl).GetAwaiter().GetResult());
    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
    if (!hash.Equals(VbCableSha256, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException(UiText.S("下载的 VB-CABLE 安装包校验失败，已停止安装。",
            "The downloaded VB-CABLE package failed verification; installation was stopped."));
    ZipFile.ExtractToDirectory(zip, vbCableCache, overwriteFiles: true);
    if (!File.Exists(setup))
        throw new InvalidOperationException(UiText.S("VB-CABLE 安装文件不完整。", "The VB-CABLE installer files are incomplete."));
    return setup;
}

(bool Ok, string Message, bool Reboot) InstallVbCable()
{
    lock (vbCableLock)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            return (false, UiText.S("仅支持 Windows 10 及以上。", "Windows 10 or later is required."), false);
        try
        {
            var setup = EnsureVbCableSetup();
            using var process = Process.Start(new ProcessStartInfo(setup)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "-i -h",
                WorkingDirectory = Path.GetDirectoryName(setup)!,
            });
            if (process == null)
                return (false, UiText.S("无法启动 VB-CABLE 安装程序。", "Could not start the VB-CABLE installer."), false);
            if (!process.WaitForExit(180_000))
            {
                return (false, UiText.S("VB-CABLE 安装超时，请到官网手动安装。",
                    "VB-CABLE installation timed out; install it manually from the official website."), false);
            }
            return process.ExitCode is 0 or 3010
                ? (true, UiText.S("虚拟声卡已安装。请重启电脑，重启后会自动找到 CABLE Output。",
                    "Virtual audio cable installed. Restart your PC and CABLE Output will appear automatically."), true)
                : (false, UiText.F("VB-CABLE 安装程序返回代码 {0}，请到官网手动安装。",
                    "The VB-CABLE installer returned code {0}; install it manually from the official website.",
                    process.ExitCode), false);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, UiText.S("已取消管理员授权，虚拟声卡没有安装。",
                "Administrator permission was declined; the virtual audio cable was not installed."), false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vbcable] {ex}");
            return (false, UiText.F("安装失败：{0}", "Installation failed: {0}", ex.Message), false);
        }
    }
}

var httpThread = new Thread(() =>
{
    try
    {
        while (!shutdown.IsCancellationRequested)
        {
            var ctx = listener.GetContext();
            try
            {
                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                ctx.Response.Headers["Cache-Control"] = "no-store";
                ctx.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
                var allowed = LocalApiPolicy.Allows(ctx.Request.Headers["Host"] ?? "",
                    ctx.Request.Headers["Origin"], ctx.Request.Headers["Sec-Fetch-Site"],
                    ctx.Request.RemoteEndPoint?.Address, actualPort,
                    lanAvailable ? lanAddresses : null);
                if (!allowed || (path.StartsWith("/api/", StringComparison.Ordinal) &&
                    !LocalApiPolicy.TokenMatches(ctx.Request.Headers["X-VoiceAec-Token"], apiToken)))
                {
                    ctx.Response.StatusCode = 403;
                    continue;
                }
                var expectedMethod = path is "/api/status" or "/api/obs-plugin" or "/" or "/index.html" ? "GET"
                    : path is "/api/devices" or "/api/vbcable/install" ? ctx.Request.HttpMethod : "POST";
                if (ctx.Request.HttpMethod != expectedMethod ||
                    (path == "/api/devices" && ctx.Request.HttpMethod is not ("GET" or "POST")) ||
                    (path == "/api/vbcable/install" && ctx.Request.HttpMethod is not ("GET" or "POST")))
                {
                    ctx.Response.StatusCode = 405;
                    continue;
                }
                switch (path)
                {
                    case "/api/obs-plugin":
                    case "/api/obs-plugin/install":
                    case "/api/obs-plugin/uninstall":
                    {
                        object response;
                        try
                        {
                            if (path != "/api/obs-plugin")
                            {
                                if (!obsPlugins.Start(path.EndsWith("/uninstall", StringComparison.Ordinal) ? "uninstall" : "install"))
                                    throw new InvalidOperationException(UiText.S("已有 OBS 插件任务正在执行。",
                                        "Another OBS plugin task is already running."));
                                ctx.Response.StatusCode = 202;
                            }
                            response = obsPlugins.Status();
                        }
                        catch (Exception ex)
                        {
                            ctx.Response.StatusCode = 409;
                            response = new { ok = false, error = ex.Message };
                        }
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, jsonOpts);
                        ctx.Response.ContentLength64 = bytes.Length;
                        ctx.Response.OutputStream.Write(bytes);
                        break;
                    }
                    case "/" or "/index.html":
                    {
                        var bytes = File.Exists(indexHtml)
                            ? System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(indexHtml)
                                .Replace("__VOICEAEC_TOKEN__", apiToken)
                                .Replace("__VOICEAEC_LANG__", UiText.Lang))
                            : System.Text.Encoding.UTF8.GetBytes(
                                $"<meta charset='utf-8'><body style='font-family:sans-serif;padding:40px'>{UiText.S("前端文件缺失: wwwroot/index.html", "Frontend files missing: wwwroot/index.html")}</body>");
                        ctx.Response.ContentType = "text/html; charset=utf-8";
                        ctx.Response.ContentLength64 = bytes.Length;
                        ctx.Response.OutputStream.Write(bytes);
                        break;
                    }
                    case "/api/status":
                    {
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(BuildStatus(), jsonOpts);
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        ctx.Response.ContentLength64 = bytes.Length;
                        ctx.Response.OutputStream.Write(bytes);
                        break;
                    }
                    case "/api/model":
                    {
                        string body;
                        using (var sr = new StreamReader(ctx.Request.InputStream))
                            body = (sr.ReadToEnd() ?? "").Trim();
                        var ok = false;
                        string error = "";
                        if (KnownMode(body))
                        {
                            error = SwitchModel(body);
                            if (error == "") ok = true;
                        }
                        else error = UiText.F("未知模式 '{0}'", "Unknown mode '{0}'", body);
                        string nowMode;
                        lock (engineLock) nowMode = mode;
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(
                            ok ? (object)new { ok, mode = nowMode } : new { ok, error }, jsonOpts);
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        ctx.Response.OutputStream.Write(bytes);
                        break;
                    }
                    case "/api/devices":
                    {
                        if (ctx.Request.HttpMethod == "GET")
                        {
                            var caps = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(d => d.FriendlyName).ToList();
                            var renders = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(d => d.FriendlyName).ToList();
                            var ref2Choices = renders.Select(n => new { name = n, kind = "playback" })
                                .Concat(caps.Select(n => new { name = n, kind = "mic" })).ToList();
                            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                            {
                                ok = true,
                                inputs = caps,
                                loops = ref2Choices,
                                outputs = renders,
                                ref2Choices,
                                current = new
                                {
                                    mic = currentMic,
                                    @ref = currentRef,
                                    ref2 = currentRef2,
                                    @out = currentOut
                                }
                            }, jsonOpts);
                            ctx.Response.ContentType = "application/json; charset=utf-8";
                            ctx.Response.ContentLength64 = bytes.Length;
                            ctx.Response.OutputStream.Write(bytes);
                        }
                        else
                        {
                            string body;
                            using (var sr = new StreamReader(ctx.Request.InputStream))
                                body = sr.ReadToEnd() ?? "";
                            string? mic = null, rf = null, rf2 = null, ot = null;
                            try
                            {
                                using var doc = JsonDocument.Parse(body);
                                if (doc.RootElement.TryGetProperty("mic", out var m) && m.ValueKind == JsonValueKind.String) mic = m.GetString();
                                if (doc.RootElement.TryGetProperty("ref", out var r) && r.ValueKind == JsonValueKind.String) rf = r.GetString();
                                if (doc.RootElement.TryGetProperty("ref2", out var r2) && r2.ValueKind == JsonValueKind.String) rf2 = r2.GetString();
                                if (doc.RootElement.TryGetProperty("out", out var o) && o.ValueKind == JsonValueKind.String) ot = o.GetString();
                            }
                            catch { }
                            string nowMic, nowRef, nowRef2, nowOut;
                            var err = mic == null && rf == null && rf2 == null && ot == null
                                ? UiText.S("请求体需含 mic/ref/ref2/out 字段", "Request body must include mic/ref/ref2/out fields.")
                                : SwitchDevices(mic, rf, rf2, ot);
                            lock (devSwitchLock) { nowMic = currentMic; nowRef = currentRef; nowRef2 = currentRef2; nowOut = currentOut; }
                            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                                err == "" ? (object)new { ok = true, current = new { mic = nowMic, @ref = nowRef, ref2 = nowRef2, @out = nowOut } }
                                          : new { ok = false, error = err, current = new { mic = nowMic, @ref = nowRef, ref2 = nowRef2, @out = nowOut } }, jsonOpts);
                            ctx.Response.ContentType = "application/json; charset=utf-8";
                            if (err != "") ctx.Response.StatusCode = 500;
                            ctx.Response.OutputStream.Write(bytes);
                        }
                        break;
                    }
                    case "/api/autostart":
                    {
                        string body;
                        using (var sr = new StreamReader(ctx.Request.InputStream))
                            body = (sr.ReadToEnd() ?? "").Trim().ToLowerInvariant();
                        if (body is "true" or "1" or "on" or "false" or "0" or "off")
                        {
                            var on = body is "true" or "1" or "on";
                            try
                            {
                                SetAutostart(on);
                                var bytes = JsonSerializer.SerializeToUtf8Bytes(new { ok = true, autostart = on }, jsonOpts);
                                ctx.Response.ContentType = "application/json; charset=utf-8";
                                ctx.Response.OutputStream.Write(bytes);
                            }
                            catch (Exception ex)
                            {
                                var bytes = JsonSerializer.SerializeToUtf8Bytes(new { ok = false, error = ex.Message }, jsonOpts);
                                ctx.Response.StatusCode = 500;
                                ctx.Response.ContentType = "application/json; charset=utf-8";
                                ctx.Response.OutputStream.Write(bytes);
                            }
                        }
                        else
                        {
                            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                                new { ok = false, error = UiText.S("body 必须是 true 或 false", "body must be true or false") }, jsonOpts);
                            ctx.Response.StatusCode = 400;
                            ctx.Response.ContentType = "application/json; charset=utf-8";
                            ctx.Response.OutputStream.Write(bytes);
                        }
                        break;
                    }
                    case "/api/lang":
                    {
                        string body;
                        using (var sr = new StreamReader(ctx.Request.InputStream))
                            body = (sr.ReadToEnd() ?? "").Trim();
                        if (!UiText.TryNormalizeLang(body, out var requestedLang))
                        {
                            ctx.Response.StatusCode = 400;
                            var errorBytes = JsonSerializer.SerializeToUtf8Bytes(
                                new { ok = false, error = UiText.S("语言必须是 zh 或 en。", "Language must be zh or en.") }, jsonOpts);
                            ctx.Response.ContentType = "application/json; charset=utf-8";
                            ctx.Response.OutputStream.Write(errorBytes);
                            break;
                        }
                        // Persist first, then notify the UI: a tray refresh problem
                        // must not lose the language choice or fail the response.
                        PersistKey("lang", requestedLang);
                        try { UiText.SetLang(requestedLang); } catch { }
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(
                            new { ok = true, lang = UiText.Lang }, jsonOpts);
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        ctx.Response.OutputStream.Write(bytes);
                        break;
                    }
                    case "/api/vbcable/install":
                    {
                        if (ctx.Request.HttpMethod == "POST")
                        {
                            lock (vbCableGate)
                            {
                                if (!vbCableRunning)
                                {
                                    vbCableRunning = true;
                                    vbCableOk = false;
                                    vbCableMessage = UiText.S("正在准备安装…", "Preparing installation…");
                                    vbCableReboot = false;
                                    _ = Task.Run(() =>
                                    {
                                        var result = InstallVbCable();
                                        lock (vbCableGate)
                                        {
                                            vbCableRunning = false;
                                            vbCableOk = result.Ok;
                                            vbCableMessage = result.Message;
                                            vbCableReboot = result.Reboot;
                                        }
                                    });
                                }
                            }
                        }
                        bool running, ok, reboot;
                        string message;
                        lock (vbCableGate)
                        {
                            running = vbCableRunning;
                            ok = vbCableOk;
                            message = vbCableRunning ? UiText.S("正在准备安装…", "Preparing installation…") : vbCableMessage;
                            reboot = vbCableReboot;
                        }
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(
                            new { ok = running ? true : ok, running, message, reboot }, jsonOpts);
                        ctx.Response.StatusCode = running ? 202 : (ok ? 200 : 500);
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        ctx.Response.OutputStream.Write(bytes);
                        break;
                    }
                    case "/api/recheck":
                    {
                        var error = RecheckDevices();
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { ok = error == "", error }, jsonOpts);
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        ctx.Response.OutputStream.Write(bytes);
                        break;
                    }
                    case "/api/sound-settings":
                    {
                        var ok = true;
                        string error = "";
                        try
                        {
                            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
                        }
                        catch (Exception ex)
                        {
                            ok = false;
                            error = ex.Message;
                        }
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(
                            ok ? (object)new { ok } : new { ok, error }, jsonOpts);
                        ctx.Response.StatusCode = ok ? 200 : 500;
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        ctx.Response.OutputStream.Write(bytes);
                        break;
                    }
                    case "/api/quit":
                    {
                        var bytes = "{\"ok\":true}"u8.ToArray();
                        ctx.Response.OutputStream.Write(bytes);
                        shutdown.Cancel();
                        break;
                    }
                    case "/api/pause":
                    {
                        using var reader = new StreamReader(ctx.Request.InputStream);
                        if (!bool.TryParse(reader.ReadToEnd().Trim(), out var value))
                        {
                            ctx.Response.StatusCode = 400;
                            break;
                        }
                        var error = SetPaused(value);
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        if (error != "") ctx.Response.StatusCode = 500;
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            ok = error == "", error, paused = Volatile.Read(ref paused) != 0
                        }, jsonOpts);
                        ctx.Response.OutputStream.Write(bytes);
                        break;
                    }
                    default:
                        ctx.Response.StatusCode = 404;
                        break;
                }
            }
            catch { }
            finally { try { ctx.Response.Close(); } catch { } }
        }
    }
    catch (HttpListenerException) { }
}) { IsBackground = true };
httpThread.Start();

// ── 系统托盘（桌面小软件形态）──────────────────────────────────────
const string AutoRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

bool GetAutostart()
{
    using var k = Registry.CurrentUser.OpenSubKey(AutoRegPath);
    // Moving a portable installation must not leave the panel claiming an
    // old, now invalid startup command is enabled for this executable.
    return string.Equals(k?.GetValue("VoiceAec") as string, AutostartCommand(), StringComparison.OrdinalIgnoreCase);
}

string AutostartCommand()
{
    var executable = Environment.ProcessPath!;
    var assembly = Assembly.GetEntryAssembly()!.Location;
    return Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? $"\"{executable}\" \"{assembly}\" --tray" : $"\"{executable}\" --tray";
}

void SetAutostart(bool on)
{
    using var k = Registry.CurrentUser.CreateSubKey(AutoRegPath);
    if (on) k.SetValue("VoiceAec", AutostartCommand());
    else k.DeleteValue("VoiceAec", throwOnMissingValue: false);
}

// Repair an existing opt-in after moving the portable installation. Do not
// enable startup from scratch or replace another existing installation.
try
{
    using var key = Registry.CurrentUser.OpenSubKey(AutoRegPath);
    var saved = key?.GetValue("VoiceAec") as string;
    if (!string.IsNullOrEmpty(saved) && !GetAutostart())
    {
        var executable = saved.StartsWith('"') ? saved.Split('"')[1] : saved.Split(' ')[0];
        if (!File.Exists(executable)) SetAutostart(true);
    }
}
catch (Exception ex) { Console.Error.WriteLine($"[startup] {ex.Message}"); }

var trayThread = new Thread(() =>
{
    Console.WriteLine("[panel] tray thread started");
    // The wildcard prefix used for LAN access (http://+:port/) is not a
    // navigable URL; the panel itself always loads over loopback.
    var panelUrl = $"http://localhost:{actualPort}/";
    var exiting = false;
    Form? panelForm = null;
    Microsoft.Web.WebView2.WinForms.WebView2? panelWebView = null;

    Rectangle? savedWindow = null;
    var savedWindowValue = cfg.GetValueOrDefault("window", "");
    if (!string.IsNullOrEmpty(savedWindowValue))
    {
        var parts = savedWindowValue.Split(',');
        if (parts.Length == 4 &&
            int.TryParse(parts[0], out var wx) && int.TryParse(parts[1], out var wy) &&
            int.TryParse(parts[2], out var ww) && int.TryParse(parts[3], out var wh) &&
            ww >= 640 && wh >= 480)
        {
            var candidate = new Rectangle(wx, wy, ww, wh);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(candidate)))
                savedWindow = candidate;
        }
    }

    void SaveWindow()
    {
        var form = panelForm;
        if (form == null || form.IsDisposed) return;
        var bounds = form.WindowState == FormWindowState.Maximized ? form.RestoreBounds : form.Bounds;
        if (bounds.Width < 640 || bounds.Height < 480) return;
        PersistKey("window", $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}");
    }

    async void UpdatePanelVisibility()
    {
        var view = panelWebView;
        var core = view?.CoreWebView2;
        if (view == null || core == null || panelForm == null) return;
        var visible = panelForm.Visible && panelForm.WindowState != FormWindowState.Minimized;
        try
        {
            if (visible)
            {
                view.Visible = true;
                core.Resume();
            }
            core.PostWebMessageAsJson(visible ? "{\"panelVisible\":true}" : "{\"panelVisible\":false}");
            if (!visible)
            {
                view.Visible = false;
                await core.TrySuspendAsync();
                if (panelForm.Visible && panelForm.WindowState != FormWindowState.Minimized) core.Resume();
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[panel] visibility: {ex.Message}"); }
    }

    async Task OpenAboutAsync()
    {
        try
        {
            for (var i = 0; i < 30 && panelWebView?.CoreWebView2 == null; i++)
                await Task.Delay(100);
            var core = panelWebView?.CoreWebView2;
            if (core == null) return;
            await Task.Delay(250);   // let ShowPanel resume a suspended WebView2 first
            await core.ExecuteScriptAsync("window.dispatchEvent(new Event('voiceaec:about'))");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[panel] about: {ex.Message}"); }
    }

    void ShowPanel()
    {
        Console.WriteLine("[panel] showing control panel");
        if (panelForm == null || panelForm.IsDisposed)
        {
            panelForm = new PanelForm
            {
                Text = "VoiceAec",
                StartPosition = savedWindow.HasValue ? FormStartPosition.Manual : FormStartPosition.CenterScreen,
                ClientSize = new Size(1280, 800),
                MinimumSize = new Size(960, 640),
                Icon = TraySupport.AppIcon(64),
                ShowInTaskbar = true,
                AutoScaleDimensions = new SizeF(96, 96),
                AutoScaleMode = AutoScaleMode.Dpi,
            };
            if (savedWindow.HasValue) panelForm.Bounds = savedWindow.Value;
            Console.WriteLine("[panel] form created");
            var wv = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill };
            Console.WriteLine("[panel] webview control created");
            panelWebView = wv;
            panelForm.Controls.Add(wv);
            panelForm.VisibleChanged += (_, _) => UpdatePanelVisibility();
            panelForm.Resize += (_, _) => { UpdatePanelVisibility(); PostWindowState(); };
            panelForm.FormClosing += (_, e) =>
            {
                if (exiting) return;
                SaveWindow();
                e.Cancel = true;                 // 点 X = 缩到系统托盘继续运行
                panelForm.Hide();
            };
            panelForm.Load += async (_, _) =>
            {
                try
                {
                    var udf = Arg("--webview-data-dir", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceAec", "WebView2"));
                    var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, udf);
                    await wv.EnsureCoreWebView2Async(env);
                    wv.ZoomFactor = 1.0;
                    wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    wv.CoreWebView2.Settings.IsZoomControlEnabled = false;
                    // Let the panel's own title strip act as the native caption:
                    // dragging, double-click maximize/restore and the system menu.
                    wv.CoreWebView2.Settings.IsNonClientRegionSupportEnabled = true;
                    wv.CoreWebView2.NewWindowRequested += (_, e2) =>
                    {
                        // About-page links (target=_blank) go to the default browser.
                        e2.Handled = true;
                        if (e2.Uri?.StartsWith("https://", StringComparison.Ordinal) != true) return;
                        try { Process.Start(new ProcessStartInfo(e2.Uri) { UseShellExecute = true }); }
                        catch (Exception ex) { Console.Error.WriteLine($"[panel] open link: {ex.Message}"); }
                    };
                    wv.CoreWebView2.WebMessageReceived += (_, e2) =>
                    {
                        var command = e2.TryGetWebMessageAsString();
                        if (string.IsNullOrEmpty(command)) return;
                        panelForm.BeginInvoke(() =>
                        {
                            switch (command)
                            {
                                case "window-minimize":
                                    panelForm.WindowState = FormWindowState.Minimized;
                                    break;
                                case "window-maximize":
                                    panelForm.WindowState = panelForm.WindowState == FormWindowState.Maximized
                                        ? FormWindowState.Normal
                                        : FormWindowState.Maximized;
                                    break;
                                case "window-close":
                                case "close-panel":
                                    SaveWindow();
                                    panelForm.Hide();
                                    break;
                                case "window-drag-start":
                                    WindowTheme.BeginWindowDrag(panelForm.Handle);
                                    break;
                                case "copy-lan-url":
                                    try
                                    {
                                        if (lanAvailable && primaryLan != null && actualPort > 0)
                                        {
                                            System.Windows.Forms.Clipboard.SetText(
                                                $"http://{primaryLan}:{actualPort}/");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.Error.WriteLine($"[panel] clipboard: {ex.Message}");
                                    }
                                    break;
                            }
                        });
                    };
                    wv.CoreWebView2.Navigate(panelUrl);
                    wv.CoreWebView2.NavigationCompleted += (_, _) => { UpdatePanelVisibility(); PostWindowState(); };
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[panel] initialization failed: {ex}");
                    var tip = new Label
                    {
                        Dock = DockStyle.Fill,
                        Text = UiText.F("面板加载失败（WebView2）：{0}\r\n音频处理仍在后台运行。",
                            "Panel failed to load (WebView2): {0}\r\nAudio processing is still running in the background.",
                            ex.Message),
                        Padding = new Padding(24),
                        ForeColor = Color.Firebrick,
                    };
                    panelForm.Controls.Add(tip);
                    tip.BringToFront();
                }
            };
        }
        panelForm.Show();
        Console.WriteLine("[panel] form shown");
        if (panelForm.WindowState == FormWindowState.Minimized) panelForm.WindowState = FormWindowState.Normal;
        panelForm.Activate();

        void PostWindowState()
        {
            var core = panelWebView?.CoreWebView2;
            if (core == null || panelForm == null || panelForm.IsDisposed) return;
            try
            {
                core.PostWebMessageAsJson(panelForm.WindowState == FormWindowState.Maximized
                    ? "{\"windowMaximized\":true}"
                    : "{\"windowMaximized\":false}");
            }
            catch (Exception ex) { Console.Error.WriteLine($"[panel] window state: {ex.Message}"); }
        }
    }

    using var trayIcon = TraySupport.TrayIcon();
    using var icon = new NotifyIcon
    {
        Icon = trayIcon,
        Text = "VoiceAec",
        Visible = true,
    };
    Console.WriteLine("[panel] tray icon ready");

    TrayMenuState CurrentTrayState()
    {
        string current;
        lock (engineLock) current = mode;
        if (!KnownMode(current)) current = "off";
        var modeEntries = new List<TrayModeEntry>
        {
            new("off", ModelLabel("off"), Color.FromArgb(0xE8, 0xB8, 0x4B), true),
            new("v1.2", ModelLabel("v1.2"), Color.FromArgb(0x2E, 0xC4, 0x89), ModelExists("v1.2")),
            new("v1.3", ModelLabel("v1.3"), Color.FromArgb(0xF2, 0x7B, 0xA9), ModelExists("v1.3")),
            new("v1.4", ModelLabel("v1.4"), Color.FromArgb(0x4F, 0xA8, 0xE8), ModelExists("v1.4")),
        };
        return new TrayMenuState(current, ModelLabel(current), Volatile.Read(ref paused) != 0,
            GetAutostart(), modeEntries);
    }

    using var menu = new TrayMenu(CurrentTrayState);
    menu.OpenPanelRequested += ShowPanel;
    menu.PauseRequested += () =>
    {
        var error = SetPaused(Volatile.Read(ref paused) == 0);
        if (error != "") icon.ShowBalloonTip(2000, "VoiceAec", error, ToolTipIcon.Warning);
    };
    menu.ModeRequested += requested =>
    {
        var error = SwitchModel(requested);
        icon.ShowBalloonTip(2000, "VoiceAec",
            error == "" ? UiText.F("已切换：{0}", "Switched to: {0}", ModelLabel(requested))
                        : UiText.F("切换失败：{0}", "Switch failed: {0}", error), ToolTipIcon.Info);
    };
    menu.AutostartRequested += enabled =>
    {
        try { SetAutostart(enabled); }
        catch (Exception ex)
        {
            icon.ShowBalloonTip(2000, "VoiceAec",
                UiText.F("设置失败：{0}", "Failed to change the setting: {0}", ex.Message), ToolTipIcon.Warning);
        }
    };
    menu.QuitRequested += () => shutdown.Cancel();
    menu.AboutRequested += () =>
    {
        ShowPanel();
        _ = OpenAboutAsync();
    };
    void RefreshTrayTip()
    {
        string current;
        lock (engineLock) current = mode;
        if (!KnownMode(current)) current = "off";
        icon.Text = Volatile.Read(ref paused) != 0
            ? UiText.S("VoiceAec · 已暂停并静音", "VoiceAec · Paused and muted")
            : $"VoiceAec · {ModelLabel(current)}";
    }
    menu.Opening += (_, _) => RefreshTrayTip();
    // POST /api/lang arrives on the HTTP thread: refresh the tray text and menu
    // content on the UI thread; an open menu is closed so it reopens localized.
    UiText.LanguageChanged += () =>
    {
        try
        {
            icon.ContextMenuStrip?.BeginInvoke((Action)(() =>
            {
                try
                {
                    if (menu.Visible) menu.Close(ToolStripDropDownCloseReason.AppFocusChange);
                    menu.RefreshLanguage();
                    RefreshTrayTip();
                }
                catch (Exception) { }   // a refresh failure must not take down the tray
            }));
        }
        catch (Exception) { }   // no window handle yet or shutting down
    };
    icon.ContextMenuStrip = menu;
    icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowPanel(); };
    icon.BalloonTipClicked += (_, _) => ShowPanel();
    icon.ShowBalloonTip(2500, UiText.S("VoiceAec 已在后台运行", "VoiceAec is running in the background"),
        UiText.S("左键点击图标打开控制面板；右键可选择模式 / 开机自启 / 退出",
            "Left-click the icon to open the control panel; right-click for mode, startup and exit."), ToolTipIcon.Info);

    // 设备插拔推送：WASAPI 在自己的线程回调 DeviceChangeWatcher，插拔一次会连发
    // 多条事件，这里在 UI 线程上合并成一条 deviceChanged 消息，面板据此刷新设备列表。
    var deviceWatcher = new DeviceChangeWatcher();
    var devicePushTimer = new System.Windows.Forms.Timer { Interval = 300 };
    devicePushTimer.Tick += (_, _) =>
    {
        devicePushTimer.Stop();
        var core = panelWebView?.CoreWebView2;
        if (core == null) return;
        try { core.PostWebMessageAsJson("{\"deviceChanged\":true}"); }
        catch (Exception ex) { Console.Error.WriteLine($"[panel] device push: {ex.Message}"); }
    };
    deviceWatcher.Changed += () =>
    {
        try
        {
            var form = panelForm;
            if (form == null || form.IsDisposed || !form.IsHandleCreated) return;
            form.BeginInvoke(() => devicePushTimer.Start());
        }
        catch (Exception) { }   // the form may be tearing down between the checks and BeginInvoke
    };
    // 注册用的枚举器必须整个运行期存活，被回收后通知会失效
    var notifyEnumerator = new MMDeviceEnumerator();
    deviceWatcher.Changed += QueueRecovery;   // 插拔同时触发音频自愈
    try { notifyEnumerator.RegisterEndpointNotificationCallback(deviceWatcher); }
    catch (Exception ex) { Console.Error.WriteLine($"[panel] device watcher: {ex.Message}"); }

    var quitTimer = new System.Windows.Forms.Timer { Interval = 400 };
    quitTimer.Tick += (_, _) =>
    {
        if (instance.Activation.WaitOne(0)) ShowPanel();
        if (!shutdown.IsCancellationRequested) return;
        quitTimer.Stop();
        SaveWindow();
        exiting = true;
        try { panelForm?.Close(); } catch { }
        icon.Visible = false;
        Application.ExitThread();
    };
    quitTimer.Start();
    if (!trayMode) ShowPanel();   // 手动启动直接显示窗口；--tray（开机自启）仅托盘
    Application.Run();
    try { notifyEnumerator.UnregisterEndpointNotificationCallback(deviceWatcher); } catch { }
    notifyEnumerator.Dispose();
}) { IsBackground = true };
trayThread.SetApartmentState(ApartmentState.STA);
trayThread.Start();

Console.WriteLine($"  [http]    http://localhost:{actualPort}/");
// 面板由托盘线程的原生窗口（WebView2）承载，不再拉起系统浏览器
Console.WriteLine(UiText.S(
    "== 语音软件选 CABLE Output；窗口关闭后程序在系统托盘继续运行，右键托盘图标可退出 ==",
    "== Select CABLE Output in your voice app; closing the window keeps VoiceAec in the tray, right-click the icon to quit =="));
shutdown.Token.WaitHandle.WaitOne();

Console.WriteLine("stopping...");
try { listener.Stop(); } catch { }
SetPaused(true, persist: false);
proc.Join();
trayThread.Join(3000);
lock (engineLock) { engine?.Dispose(); engine = null; }
DisposeAudio(micDevice); DisposeAudio(refDevice); DisposeAudio(ref2Device); DisposeAudio(outDevice);
return 0;

// 16k mono float -> 48k stereo 16-bit, linear interpolation, exact 3x ratio.
internal sealed class CableFeedProvider(SampleRing ring, Action onUnderrun) : IWaveProvider
{
    private const int InHop = 256;
    private const int InHopBytes = InHop * 4;
    private readonly Queue<byte> _pending = new();
    private readonly byte[] _inBytes = new byte[InHopBytes];
    private readonly float[] _inFloats = new float[InHop];
    private float _prevIn;
    private bool _hasPrev;

    public WaveFormat WaveFormat { get; } = new WaveFormat(48000, 16, 2);

    public int Read(byte[] buffer, int offset, int count)
    {
        var need = count;
        while (_pending.Count < need)
        {
            var got = ring.Read(_inBytes, 0, InHopBytes);
            if (got == 0)
            {
                onUnderrun();
                for (var i = 0; i < InHop; i++)
                {
                    AppendSample(0f, 0f);
                    _hasPrev = false;
                }
                if (_pending.Count == 0) return 0;
                break;
            }
            Buffer.BlockCopy(_inBytes, 0, _inFloats, 0, got);
            for (var i = 0; i < got / 4; i++)
            {
                var a = _hasPrev ? _prevIn : _inFloats[i];
                var b = _inFloats[i];
                AppendSample(a, b);
                _prevIn = b;
                _hasPrev = true;
            }
        }
        var take = Math.Min(need, _pending.Count);
        for (var i = 0; i < take; i++) buffer[offset + i] = _pending.Dequeue();
        return take;
    }

    private void AppendSample(float a, float b)
    {
        for (var k = 0; k < 3; k++)
        {
            var v = a + (b - a) * (k / 3f);
            var s = (short)Math.Clamp((int)(v * 32767f), short.MinValue, short.MaxValue);
            var lo = (byte)(s & 0xff);
            var hi = (byte)((s >> 8) & 0xff);
            _pending.Enqueue(lo); _pending.Enqueue(hi);
            _pending.Enqueue(lo); _pending.Enqueue(hi);
        }
    }
}
