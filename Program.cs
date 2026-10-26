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

if (args.Contains("--obs-plugin-action")) return ObsPluginManager.RunHelper(args);

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
var modelLabels = new Dictionary<string, string>
{
    ["off"] = "关闭（直通）",
    ["v1.2"] = "均衡模式",
    ["v1.3"] = "强力模式",
    ["v1.4"] = "纯回声消除模式",
};

if (argsList.Contains("--list"))
{
    var enumDevices = new MMDeviceEnumerator();
    Console.WriteLine("== Capture ==");
    foreach (var d in enumDevices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        Console.WriteLine($"  {d.FriendlyName}");
    Console.WriteLine("== Render ==");
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
var startMode = Arg("--model", "v1.2");
var port = int.Parse(Arg("--port", "8765"));
var noBrowser = argsList.Contains("--nobrowser");
var trayMode = argsList.Contains("--tray");     // 后台托盘运行：隐藏控制台 + 不开浏览器
var keepConsole = argsList.Contains("--console"); // 调试：保留控制台窗口
var paused = argsList.Contains("--start-paused") ? 1 : 0;
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
        throw new Exception($"no active {flow} device matching '{substring}' (use --list)");
    return hits[0];
}

// 智能默认:未配置时取系统默认通信麦克风 + 默认播放设备;输出必须是 CABLE Input
if (micSub.Length == 0)
{
    try { micSub = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).FriendlyName; }
    catch { micSub = "麦克风"; }
}
if (refSub.Length == 0)
{
    try { refSub = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).FriendlyName; }
    catch { refSub = "扬声器"; }
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
    return caps.Count > 0 ? caps[0] : throw new Exception($"no active device matching '{substring}'");
}

var micDevice = Find(DataFlow.Capture, micSub);
var refDevice = FindAny(refSub);   // 主参考也允许麦克风(如近讲麦场景);默认仍是播放设备
MMDevice? ref2Device = null;
if (!ref2Sub.Equals("none", StringComparison.OrdinalIgnoreCase))
{
    try { ref2Device = FindAny(ref2Sub); }   // ref2 可为播放设备回采或另一只麦克风(噪声参考)
    catch { Console.WriteLine("  (ref2 not found, single-ref)"); }
}
MMDevice outDevice;
var cableMissing = false;
try
{
    outDevice = Find(DataFlow.Render, outSub);
}
catch (Exception) when (outSub.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
{
    // The default target is VB-CABLE. Without it the app must still start and
    // tell the user what is missing instead of dying in a hidden console.
    cableMissing = true;
    outDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
}
cfg["mic"] = micDevice.FriendlyName;   // 固化本次解析结果,后续系统默认变化不影响
cfg["ref"] = refDevice.FriendlyName;
cfg["ref2"] = ref2Device?.FriendlyName ?? "none";
if (!cableMissing) cfg["out"] = outDevice.FriendlyName;   // keep CABLE as the target for the next launch
SaveCfg();

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

string SwitchModel(string requested)
{
    lock (modeSwitchLock) return SwitchModelCore(requested);
}

string SwitchModelCore(string requested)
{
    if (!modelLabels.ContainsKey(requested)) return $"未知模式 '{requested}'";
    if (!ModelExists(requested))
        return $"模型文件不存在: {modelFiles[requested]}(已查找 {string.Join("、", modelDirs)})";
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
        return "";
    }
    catch (Exception ex)
    {
        return $"加载失败: {ex.Message}";
    }
    finally { Volatile.Write(ref switching, false); }
}

{
    var err = SwitchModel(startMode);
    if (err != "") Console.WriteLine($"[mode] init failed -> off: {err}");
}

var target16k = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);
var micRing = new SampleRing(Rate * 4 * 4);
var refRing = new SampleRing(Rate / 2 * 4);
var ref2Ring = new SampleRing(Rate / 2 * 4);   // 始终创建,便于运行时热加 ref2
var outRing = new SampleRing(Rate * 2 * 4);
using var inputReady = new AutoResetEvent(false);
var devSwitchLock = new object();
string currentMic = micDevice.FriendlyName, currentRef = refDevice.FriendlyName;
string currentRef2 = ref2Device?.FriendlyName ?? "none", currentOut = outDevice.FriendlyName;

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

string SetPaused(bool value)
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
            return "";
        }

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
            micCap = newMic; refCap = newRef; ref2Cap = newRef2; waveOut = newOutput;
            Volatile.Write(ref paused, 0);
            audioEnabled.Set();
            return "";
        }
        catch (Exception ex)
        {
            DisposeAudio(newOutput); DisposeAudio(newMic); DisposeAudio(newRef); DisposeAudio(newRef2);
            ClearAudioBuffers();
            return $"恢复失败，已保持静音: {ex.Message}";
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
        void ReplaceCapture(string name, DataFlow? flow, SampleRing ring, string label,
            ref MMDevice? device, ref CaptureSession? capture, ref string selected)
        {
            if (name == selected) return;
            MMDevice? freshDevice = name.Equals("none", StringComparison.OrdinalIgnoreCase)
                && flow == null ? null : (flow == null ? FindAny(name) : Find(flow.Value, name));
            CaptureSession? freshCapture = null;
            try
            {
                var freshName = freshDevice?.FriendlyName ?? "none";
                if (freshDevice != null && Volatile.Read(ref paused) == 0)
                    freshCapture = StartCapture(freshDevice, ring, label);
                var oldDevice = device; var oldCapture = capture;
                device = freshDevice; capture = freshCapture; selected = freshName;
                DisposeAudio(oldCapture); DisposeAudio(oldDevice);
                ring.Clear();
            }
            catch { DisposeAudio(freshCapture); DisposeAudio(freshDevice); throw; }
        }
        if (!string.IsNullOrEmpty(micName)) Change("麦克风", () =>
            ReplaceCapture(micName, DataFlow.Capture, micRing, "mic", ref micDevice, ref micCap, ref currentMic));
        if (!string.IsNullOrEmpty(refName)) Change("参考回采", () =>
            ReplaceCapture(refName, null, refRing, "ref", ref refDevice, ref refCap, ref currentRef));
        if (ref2Name != null) Change("第二参考", () =>
            ReplaceCapture(ref2Name, null, ref2Ring, "ref2", ref ref2Device, ref ref2Cap, ref currentRef2));
        if (!string.IsNullOrEmpty(outName) && outName != currentOut) Change("输出", () =>
        {
            var freshDevice = Find(DataFlow.Render, outName);
            WasapiOut? freshOutput = null;
            try
            {
                var freshName = freshDevice.FriendlyName;
                if (Volatile.Read(ref paused) == 0) freshOutput = StartOutput(freshDevice);
                var oldOutput = waveOut; var oldDevice = outDevice;
                waveOut = freshOutput; outDevice = freshDevice; currentOut = freshName;
                DisposeAudio(oldOutput); DisposeAudio(oldDevice);
            }
            catch { DisposeAudio(freshOutput); DisposeAudio(freshDevice); throw; }
        });
        cfg["mic"] = currentMic; cfg["ref"] = currentRef;
        cfg["ref2"] = currentRef2; cfg["out"] = currentOut;
        SaveCfg();
        return errors.Count == 0 ? "" : "设备切换失败: " + string.Join("；", errors);
    }
}

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
    string micName, refName, ref2Name, outName;
    lock (devSwitchLock)
    {
        micName = currentMic; refName = currentRef; ref2Name = currentRef2; outName = currentOut;
    }
    var appVersion = Assembly.GetEntryAssembly()!.GetName().Version?.ToString(3) ?? "dev";
    return new
    {
        ok = true,
        version = appVersion,
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
        models = modelLabels.ToDictionary(kv => kv.Key, kv => new { available = ModelExists(kv.Key), label = kv.Value }),
        autostart = GetAutostart(),
        cableMissing,
        lanAvailable,
        lanUrl = lanAvailable && primaryLan != null ? $"http://{primaryLan}:{actualPort}/" : "",
        hint = cableMissing
            ? "还差一步：没有找到 VB-CABLE 虚拟声卡。点上方「一键安装虚拟声卡」，装完重启电脑，语音软件里就能选到 CABLE Output。"
            : Volatile.Read(ref paused) != 0
            ? "已暂停：现在不采集也不输出声音，点右上角「恢复处理与输出」就能继续。"
            : $"打开 QQ 或游戏语音，把「麦克风」选成 CABLE Output；声音继续从「{ShortName(refName)}」播放，就能一边外放一边消除回声。"
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
if (listener == null) throw new Exception("cannot bind HTTP listener (8765-8774)");
lanAvailable = lanEnabled && listener.Prefixes.Any(x => x.Contains("+:"));
if (lanEnabled && !lanAvailable) Console.WriteLine("[lan] wildcard bind denied; panel is loopback-only");

var indexHtml = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
var apiToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
var obsPlugins = new ObsPluginManager();

// ── VB-CABLE helper: an unmodified official package, verified by hash ───
const string VbCableUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";
const string VbCableSha256 = "B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB";
var vbCableLock = new object();
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
        throw new InvalidOperationException("下载的 VB-CABLE 安装包校验失败，已停止安装。");
    ZipFile.ExtractToDirectory(zip, vbCableCache, overwriteFiles: true);
    if (!File.Exists(setup)) throw new InvalidOperationException("VB-CABLE 安装文件不完整。");
    return setup;
}

(bool Ok, string Message, bool Reboot) InstallVbCable()
{
    lock (vbCableLock)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return (false, "仅支持 Windows 10 及以上。", false);
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
            if (process == null) return (false, "无法启动 VB-CABLE 安装程序。", false);
            if (!process.WaitForExit(180_000))
            {
                return (false, "VB-CABLE 安装超时，请到官网手动安装。", false);
            }
            return process.ExitCode is 0 or 3010
                ? (true, "虚拟声卡已安装。请重启电脑，重启后会自动找到 CABLE Output。", true)
                : (false, $"VB-CABLE 安装程序返回代码 {process.ExitCode}，请到官网手动安装。", false);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "已取消管理员授权，虚拟声卡没有安装。", false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vbcable] {ex}");
            return (false, $"安装失败：{ex.Message}", false);
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
                    : path == "/api/devices" ? ctx.Request.HttpMethod : "POST";
                if (ctx.Request.HttpMethod != expectedMethod ||
                    (path == "/api/devices" && ctx.Request.HttpMethod is not ("GET" or "POST")))
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
                                    throw new InvalidOperationException("已有 OBS 插件任务正在执行。");
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
                            ? System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(indexHtml).Replace("__VOICEAEC_TOKEN__", apiToken))
                            : "<meta charset='utf-8'><body style='font-family:sans-serif;padding:40px'>前端文件缺失: wwwroot/index.html</body>"u8.ToArray();
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
                        if (body == "off" || body == "v1.2" || body == "v1.3" || body == "v1.4")
                        {
                            error = SwitchModel(body);
                            if (error == "") ok = true;
                        }
                        else error = $"未知模式 '{body}'";
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
                            var err = mic == null && rf == null && rf2 == null && ot == null ? "请求体需含 mic/ref/ref2/out 字段" : SwitchDevices(mic, rf, rf2, ot);
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
                                new { ok = false, error = "body 必须是 true 或 false" }, jsonOpts);
                            ctx.Response.StatusCode = 400;
                            ctx.Response.ContentType = "application/json; charset=utf-8";
                            ctx.Response.OutputStream.Write(bytes);
                        }
                        break;
                    }
                    case "/api/vbcable/install":
                    {
                        var (ok, message, reboot) = InstallVbCable();
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(
                            new { ok, message, reboot }, jsonOpts);
                        ctx.Response.StatusCode = ok ? 200 : 500;
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
            panelForm = new Form
            {
                Text = "VoiceAec",
                StartPosition = FormStartPosition.CenterScreen,
                ClientSize = new Size(1280, 800),
                MinimumSize = new Size(960, 640),
                Icon = TraySupport.AppIcon(64),
                ShowInTaskbar = true,
                AutoScaleDimensions = new SizeF(96, 96),
                AutoScaleMode = AutoScaleMode.Dpi,
            };
            Console.WriteLine("[panel] form created");
            var wv = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill };
            Console.WriteLine("[panel] webview control created");
            panelWebView = wv;
            panelForm.Controls.Add(wv);
            panelForm.VisibleChanged += (_, _) => UpdatePanelVisibility();
            panelForm.Resize += (_, _) => UpdatePanelVisibility();
            panelForm.FormClosing += (_, e) =>
            {
                if (exiting) return;
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
                        if (e2.TryGetWebMessageAsString() == "close-panel")
                            panelForm.BeginInvoke(() => panelForm.Hide());
                    };
                    wv.CoreWebView2.Navigate(panelUrl);
                    wv.CoreWebView2.NavigationCompleted += (_, _) => UpdatePanelVisibility();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[panel] initialization failed: {ex}");
                    var tip = new Label
                    {
                        Dock = DockStyle.Fill,
                        Text = $"面板加载失败（WebView2）：{ex.Message}\r\n音频处理仍在后台运行。",
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
    }

    using var trayIcon = TraySupport.TrayIcon();
    using var icon = new NotifyIcon
    {
        Icon = trayIcon,
        Text = "VoiceAec",
        Visible = true,
    };
    Console.WriteLine("[panel] tray icon ready");

    var modeEntries = new List<TrayModeEntry>
    {
        new("off", modelLabels["off"], Color.FromArgb(0xE8, 0xB8, 0x4B), true),
        new("v1.2", modelLabels["v1.2"], Color.FromArgb(0x2E, 0xC4, 0x89), ModelExists("v1.2")),
        new("v1.3", modelLabels["v1.3"], Color.FromArgb(0xF2, 0x7B, 0xA9), ModelExists("v1.3")),
        new("v1.4", modelLabels["v1.4"], Color.FromArgb(0x4F, 0xA8, 0xE8), ModelExists("v1.4")),
    };
    TrayMenuState CurrentTrayState()
    {
        string current;
        lock (engineLock) current = mode;
        if (!modelLabels.ContainsKey(current)) current = "off";
        return new TrayMenuState(current, modelLabels[current], Volatile.Read(ref paused) != 0,
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
            error == "" ? $"已切换：{modelLabels[requested]}" : $"切换失败：{error}", ToolTipIcon.Info);
    };
    menu.AutostartRequested += enabled =>
    {
        try { SetAutostart(enabled); }
        catch (Exception ex) { icon.ShowBalloonTip(2000, "VoiceAec", $"设置失败：{ex.Message}", ToolTipIcon.Warning); }
    };
    menu.QuitRequested += () => shutdown.Cancel();
    menu.AboutRequested += () =>
    {
        ShowPanel();
        _ = OpenAboutAsync();
    };
    menu.Opening += (_, _) =>
    {
        string current;
        lock (engineLock) current = mode;
        icon.Text = Volatile.Read(ref paused) != 0 ? "VoiceAec · 已暂停并静音" : $"VoiceAec · {modelLabels[current]}";
    };
    icon.ContextMenuStrip = menu;
    icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowPanel(); };
    icon.BalloonTipClicked += (_, _) => ShowPanel();
    icon.ShowBalloonTip(2500, "VoiceAec 已在后台运行",
        "左键点击图标打开控制面板；右键可选择模式 / 开机自启 / 退出", ToolTipIcon.Info);

    var quitTimer = new System.Windows.Forms.Timer { Interval = 400 };
    quitTimer.Tick += (_, _) =>
    {
        if (instance.Activation.WaitOne(0)) ShowPanel();
        if (!shutdown.IsCancellationRequested) return;
        quitTimer.Stop();
        exiting = true;
        try { panelForm?.Close(); } catch { }
        icon.Visible = false;
        Application.ExitThread();
    };
    quitTimer.Start();
    if (!trayMode) ShowPanel();   // 手动启动直接显示窗口；--tray（开机自启）仅托盘
    Application.Run();
}) { IsBackground = true };
trayThread.SetApartmentState(ApartmentState.STA);
trayThread.Start();

Console.WriteLine($"  [http]    http://localhost:{actualPort}/");
// 面板由托盘线程的原生窗口（WebView2）承载，不再拉起系统浏览器
Console.WriteLine("== 语音软件选 CABLE Output；窗口关闭后程序在系统托盘继续运行，右键托盘图标可退出 ==");
shutdown.Token.WaitHandle.WaitOne();

Console.WriteLine("stopping...");
try { listener.Stop(); } catch { }
SetPaused(true);
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
