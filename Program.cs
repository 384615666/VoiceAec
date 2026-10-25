using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceAec;

var argsList = args.ToList();
string Arg(string name, string fallback)
{
    var i = argsList.IndexOf(name);
    return i >= 0 && i + 1 < argsList.Count ? argsList[i + 1] : fallback;
}

// ── 设备配置持久化:命令行 > voiceaec.json > 智能默认 ────────────────
var cfgPath = Path.Combine(AppContext.BaseDirectory, "voiceaec.json");
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
void SaveCfg() { try { File.WriteAllText(cfgPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true })); } catch { } }
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
    ["v1.2"] = "v1.2 · 均衡",
    ["v1.3"] = "v1.3 · 最强消除",
    ["v1.4"] = "v1.4 · 纯回声消除",
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
if (trayMode) noBrowser = true;

if (!keepConsole && !argsList.Contains("--list"))
    TraySupport.HideConsole();

Console.WriteLine("VoiceAec · 回声消除麦克风");
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

var micDevice = Find(DataFlow.Capture, micSub);
var refDevice = Find(DataFlow.Render, refSub);
MMDevice? ref2Device = null;
if (!ref2Sub.Equals("none", StringComparison.OrdinalIgnoreCase))
{
    try { ref2Device = Find(DataFlow.Render, ref2Sub); }
    catch { Console.WriteLine("  (ref2 not found, single-ref)"); }
}
var outDevice = Find(DataFlow.Render, outSub);
cfg["mic"] = micDevice.FriendlyName;   // 固化本次解析结果,后续系统默认变化不影响
cfg["ref"] = refDevice.FriendlyName;
cfg["ref2"] = ref2Device?.FriendlyName ?? "none";
cfg["out"] = outDevice.FriendlyName;
SaveCfg();

// ── engine / mode management ─────────────────────────────────────────
const int Rate = 16000;
const int Hop = 256;
const int HopBytes = Hop * sizeof(float);

var engineLock = new object();
LocalVqe.Context? engine = null;
var mode = "off";
var switching = false;

bool ModelExists(string m) => m == "off" || ModelPath(m) != null;

string SwitchModel(string requested)
{
    if (!modelLabels.ContainsKey(requested)) return $"未知模式 '{requested}'";
    if (!ModelExists(requested))
        return $"模型文件不存在: {modelFiles[requested]}(已查找 {string.Join("、", modelDirs)})";
    switching = true;
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
    finally { switching = false; }
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

long hops = 0, passthroughs = 0, outUnderruns = 0, refTrims = 0, refZeroHops = 0;
double micDb = -120, outDb = -120, refDb = -120;
long refActiveTick = 0;

static void SetBufferMs(WasapiCapture c, int ms)
{
    var f = typeof(WasapiCapture).GetField("audioBufferMillisecondsLength", BindingFlags.Instance | BindingFlags.NonPublic);
    f?.SetValue(c, Math.Max(10, ms));
}

WasapiCapture StartCapture(MMDevice dev, SampleRing ring, string label)
{
    WasapiCapture cap = dev.DataFlow == DataFlow.Capture
        ? new WasapiCapture(dev)
        : new WasapiLoopbackCapture(dev);
    SetBufferMs(cap, 20);
    var bp = new BufferedWaveProvider(cap.WaveFormat) { DiscardOnBufferOverflow = true, ReadFully = false, BufferDuration = TimeSpan.FromSeconds(2) };
    var resampler = new MediaFoundationResampler(bp, target16k);
    cap.DataAvailable += (_, e) =>
    {
        if (e.BytesRecorded == 0) return;
        lock (bp) bp.AddSamples(e.Buffer, 0, e.BytesRecorded);
        var tmp = new byte[8192];
        int got;
        do
        {
            lock (bp) got = resampler.Read(tmp, 0, tmp.Length);
            if (got > 0) ring.Write(tmp, got);
        } while (got > 0);
    };
    cap.StartRecording();
    Console.WriteLine($"  [capture] {label}: {cap.WaveFormat} -> 16k mono");
    return cap;
}

static double RmsDb(float[] buf)
{
    double s = 0;
    for (var i = 0; i < buf.Length; i++) s += (double)buf[i] * buf[i];
    var rms = Math.Sqrt(s / buf.Length);
    return rms <= 0 ? -120 : Math.Max(-120, Math.Min(0, 20 * Math.Log10(rms)));
}

var micCap = StartCapture(micDevice, micRing, "mic");
var refCap = StartCapture(refDevice, refRing, "ref");
var ref2Cap = ref2Device != null ? StartCapture(ref2Device, ref2Ring!, "ref2") : null;
void TrimRef(SampleRing r)
{
    var excess = r.BufferedBytes - Rate / 2 * 4;
    if (excess <= HopBytes) return;
    var scratch = new byte[excess];
    r.Read(scratch, 0, excess);
    Interlocked.Increment(ref refTrims);
}

var feed = new CableFeedProvider(outRing, () => Interlocked.Increment(ref outUnderruns));
var waveOut = new WasapiOut(outDevice, AudioClientShareMode.Shared, true, 50);
waveOut.Init(feed);
waveOut.Play();
Console.WriteLine($"  [output]  -> {outDevice.FriendlyName}");

// ── 设备热切换:采集端/输出端重绑,推理引擎与 ring 不动 ────────────────
var devSwitchLock = new object();

string SwitchDevices(string? micName, string? refName, string? ref2Name, string? outName)
{
    lock (devSwitchLock)
    {
        try
        {
            if (!string.IsNullOrEmpty(micName))
            {
                var d = Find(DataFlow.Capture, micName);
                var old = micCap;
                micDevice = d;
                micCap = StartCapture(d, micRing, "mic");
                try { old?.StopRecording(); old?.Dispose(); } catch { }
            }
            if (!string.IsNullOrEmpty(refName))
            {
                var d = Find(DataFlow.Render, refName);
                var old = refCap;
                refDevice = d;
                refCap = StartCapture(d, refRing, "ref");
                try { old?.StopRecording(); old?.Dispose(); } catch { }
            }
            if (ref2Name != null)
            {
                var old2 = ref2Cap;
                ref2Cap = null;
                try { old2?.StopRecording(); old2?.Dispose(); } catch { }
                ref2Device = null;
                if (!ref2Name.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    var d = Find(DataFlow.Render, ref2Name);
                    ref2Device = d;
                    ref2Cap = StartCapture(d, ref2Ring, "ref2");
                }
            }
            if (!string.IsNullOrEmpty(outName))
            {
                var d = Find(DataFlow.Render, outName);
                try { waveOut.Stop(); waveOut.Dispose(); } catch { }
                outDevice = d;
                var wo = new WasapiOut(d, AudioClientShareMode.Shared, true, 50);
                wo.Init(feed);
                wo.Play();
                waveOut = wo;
            }
        }
        catch (Exception ex)
        {
            return $"设备切换失败: {ex.Message}";
        }
    }
    cfg["mic"] = micDevice.FriendlyName;
    cfg["ref"] = refDevice.FriendlyName;
    cfg["ref2"] = ref2Device?.FriendlyName ?? "none";
    cfg["out"] = outDevice.FriendlyName;
    SaveCfg();
    Console.WriteLine($"[devices] mic={cfg["mic"]} ref={cfg["ref"]} ref2={cfg["ref2"]} out={cfg["out"]}");
    return "";
}

var running = true;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; running = false; };

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
    while (running)
    {
        if (micRing.BufferedBytes < HopBytes) { Thread.Sleep(4); continue; }
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
}) { IsBackground = true };
proc.Start();

// ── HTTP control server ──────────────────────────────────────────────
var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var startAt = DateTime.UtcNow;
var self = Process.GetCurrentProcess();
var rateGate = new object();
long lastHops = 0; DateTime lastRateAt = DateTime.UtcNow; double hopsPerSec = 0;
double lastCpuMs = 0; DateTime lastCpuAt = DateTime.UtcNow; double cpuPercent = 0;

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
    return new
    {
        ok = true,
        mode = curMode,
        switching,
        uptimeSec = (int)(DateTime.UtcNow - startAt).TotalSeconds,
        cpuPercent = Math.Round(cpuPercent, 1),
        mic = new { name = micDevice.FriendlyName, levelDb = Math.Round(micDb, 1), bufferMs = Math.Round(micRing.BufferedBytes * 1000.0 / (Rate * 4), 0) },
        @ref = new
        {
            name = refDevice.FriendlyName,
            active = refActiveTick != 0 && Environment.TickCount64 - refActiveTick < 1500,
            levelDb = Math.Round(refDb, 1),
            bufferMs = Math.Round(refRing.BufferedBytes * 1000.0 / (Rate * 4), 0)
        },
        @out = new { name = outDevice.FriendlyName, levelDb = Math.Round(outDb, 1), bufferMs = Math.Round(outRing.BufferedBytes * 1000.0 / (Rate * 4), 0) },
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
        hint = "语音软件里选择 CABLE Output 作为麦克风；游戏和语音的播放设备都走 USB HiFi 音箱效果最佳"
    };
}

HttpListener? listener = null;
var actualPort = port;
for (var p = port; p < port + 10 && listener == null; p++)
{
    try
    {
        var l = new HttpListener();
        l.Prefixes.Add($"http://localhost:{p}/");
        l.Start();
        listener = l;
        actualPort = p;
    }
    catch { }
}
if (listener == null)
{
    for (var p = port; p < port + 10 && listener == null; p++)
    {
        try
        {
            var l = new HttpListener();
            l.Prefixes.Add($"http://127.0.0.1:{p}/");
            l.Start();
            listener = l;
            actualPort = p;
        }
        catch { }
    }
}
if (listener == null) throw new Exception("cannot bind HTTP listener (8765-8774)");

var indexHtml = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");

var httpThread = new Thread(() =>
{
    try
    {
        while (running)
        {
            var ctx = listener.GetContext();
            try
            {
                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                switch (path)
                {
                    case "/" or "/index.html":
                    {
                        var bytes = File.Exists(indexHtml)
                            ? File.ReadAllBytes(indexHtml)
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
                            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                            {
                                ok = true,
                                inputs = caps,
                                loops = renders,
                                outputs = renders,
                                current = new
                                {
                                    mic = micDevice.FriendlyName,
                                    @ref = refDevice.FriendlyName,
                                    ref2 = ref2Device?.FriendlyName ?? "none",
                                    @out = outDevice.FriendlyName
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
                            lock (devSwitchLock) { nowMic = micDevice.FriendlyName; nowRef = refDevice.FriendlyName; nowRef2 = ref2Device?.FriendlyName ?? "none"; nowOut = outDevice.FriendlyName; }
                            var err = mic == null && rf == null && rf2 == null && ot == null ? "请求体需含 mic/ref/ref2/out 字段" : SwitchDevices(mic, rf, rf2, ot);
                            lock (devSwitchLock) { nowMic = micDevice.FriendlyName; nowRef = refDevice.FriendlyName; nowRef2 = ref2Device?.FriendlyName ?? "none"; nowOut = outDevice.FriendlyName; }
                            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                                err == "" ? (object)new { ok = true, current = new { mic = nowMic, @ref = nowRef, ref2 = nowRef2, @out = nowOut } }
                                          : new { ok = false, error = err }, jsonOpts);
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
                    case "/api/quit":
                    {
                        var bytes = "{\"ok\":true}"u8.ToArray();
                        ctx.Response.OutputStream.Write(bytes);
                        running = false;
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
    return k?.GetValue("VoiceAec") is not null;
}

void SetAutostart(bool on)
{
    using var k = Registry.CurrentUser.CreateSubKey(AutoRegPath);
    if (on) k.SetValue("VoiceAec", $"\"{Environment.ProcessPath}\" --tray");
    else k.DeleteValue("VoiceAec", throwOnMissingValue: false);
}

var trayThread = new Thread(() =>
{
    var panelUrl = $"http://localhost:{actualPort}/";
    var exiting = false;
    Form? panelForm = null;

    void ShowPanel()
    {
        if (panelForm == null || panelForm.IsDisposed)
        {
            panelForm = new Form
            {
                Text = "VoiceAec · 回声消除麦克风",
                StartPosition = FormStartPosition.CenterScreen,
                ClientSize = new Size(1280, 800),
                MinimumSize = new Size(960, 640),
                Icon = TraySupport.MakeIcon(),
                ShowInTaskbar = true,
            };
            var wv = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill };
            panelForm.Controls.Add(wv);
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
                    var udf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceAec", "WebView2");
                    var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, udf);
                    await wv.EnsureCoreWebView2Async(env);
                    wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    wv.CoreWebView2.Settings.IsZoomControlEnabled = false;
                    wv.CoreWebView2.WebMessageReceived += (_, e2) =>
                    {
                        if (e2.TryGetWebMessageAsString() == "close-panel")
                            panelForm.BeginInvoke(() => panelForm.Hide());
                    };
                    wv.CoreWebView2.Navigate(panelUrl);
                }
                catch (Exception ex)
                {
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
        if (panelForm.WindowState == FormWindowState.Minimized) panelForm.WindowState = FormWindowState.Normal;
        panelForm.Activate();
    }

    using var icon = new NotifyIcon
    {
        Icon = TraySupport.MakeIcon(),
        Text = "VoiceAec · 回声消除麦克风",
        Visible = true,
    };
    using var menu = new ContextMenuStrip();

    var miOpen = new ToolStripMenuItem("打开控制面板(&P)");
    miOpen.Click += (_, _) => ShowPanel();

    var modeItems = modelLabels.Select(kv =>
    {
        var mi = new ToolStripMenuItem(kv.Value) { Name = kv.Key };
        mi.Click += (_, _) =>
        {
            var err = SwitchModel(kv.Key);
            icon.ShowBalloonTip(2000, "VoiceAec",
                err == "" ? $"已切换：{modelLabels[kv.Key]}" : $"切换失败：{err}", ToolTipIcon.Info);
        };
        return mi;
    }).ToArray();
    var miModeHead = new ToolStripMenuItem("处理模式(&M)");
    miModeHead.DropDownItems.AddRange(modeItems);

    var miAuto = new ToolStripMenuItem("开机自启(&S)") { CheckOnClick = true };
    miAuto.Click += (_, _) =>
    {
        try { SetAutostart(miAuto.Checked); }
        catch (Exception ex)
        {
            miAuto.Checked = !miAuto.Checked;
            icon.ShowBalloonTip(2000, "VoiceAec", $"设置失败：{ex.Message}", ToolTipIcon.Warning);
        }
    };

    var miQuit = new ToolStripMenuItem("退出(&Q)");
    miQuit.Click += (_, _) => running = false;

    menu.Items.AddRange(new ToolStripItem[]
    {
        miOpen, miModeHead, new ToolStripSeparator(), miAuto, new ToolStripSeparator(), miQuit
    });
    menu.Opening += (_, _) =>
    {
        string cur; lock (engineLock) cur = mode;
        foreach (ToolStripMenuItem mi in miModeHead.DropDownItems) mi.Checked = mi.Name == cur;
        miAuto.Checked = GetAutostart();
        icon.Text = $"VoiceAec · {modelLabels[cur]}";
    };
    icon.ContextMenuStrip = menu;
    icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowPanel(); };
    icon.BalloonTipClicked += (_, _) => ShowPanel();
    icon.ShowBalloonTip(2500, "VoiceAec 已在后台运行",
        "左键点击图标打开控制面板；右键可选择模式 / 开机自启 / 退出", ToolTipIcon.Info);

    var quitTimer = new System.Windows.Forms.Timer { Interval = 400 };
    quitTimer.Tick += (_, _) =>
    {
        if (running) return;
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
while (running) Thread.Sleep(200);

Console.WriteLine("stopping...");
try { listener.Stop(); } catch { }
micCap.StopRecording(); refCap.StopRecording(); ref2Cap?.StopRecording();
try { waveOut.Stop(); } catch { }
micCap.Dispose(); refCap.Dispose(); ref2Cap?.Dispose(); waveOut.Dispose();
lock (engineLock) { engine?.Dispose(); engine = null; }
return 0;

// 16k mono float -> 48k stereo 16-bit, linear interpolation, exact 3x ratio.
internal sealed class CableFeedProvider(SampleRing ring, Action onUnderrun) : IWaveProvider
{
    private const int InHop = 256;
    private const int InHopBytes = InHop * 4;
    private readonly Queue<byte> _pending = new();
    private float _prevIn;
    private bool _hasPrev;

    public WaveFormat WaveFormat { get; } = new WaveFormat(48000, 16, 2);

    public int Read(byte[] buffer, int offset, int count)
    {
        var need = count;
        while (_pending.Count < need)
        {
            var inBytes = new byte[InHopBytes];
            var got = ring.Read(inBytes, 0, InHopBytes);
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
            var inFloats = new float[got / 4];
            Buffer.BlockCopy(inBytes, 0, inFloats, 0, got);
            for (var i = 0; i < inFloats.Length; i++)
            {
                var a = _hasPrev ? _prevIn : inFloats[i];
                var b = inFloats[i];
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

// 托盘支持：隐藏控制台窗口 + 程序化生成托盘图标（马卡龙绿圆底 + 白色均衡器条）
internal static class TraySupport
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static void HideConsole()
    {
        var h = GetConsoleWindow();
        if (h != IntPtr.Zero) ShowWindow(h, 0 /* SW_HIDE */);
    }

    public static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var bg = new LinearGradientBrush(
            new Rectangle(0, 0, 32, 32),
            Color.FromArgb(134, 222, 196), Color.FromArgb(86, 184, 155), 45f);
        g.FillEllipse(bg, 1, 1, 30, 30);
        // 均衡器五条白杠：中间高、两侧低
        var bars = new (int x, int top, int bottom)[] { (7, 14, 21), (11, 10, 23), (15, 7, 25), (19, 10, 23), (23, 14, 21) };
        foreach (var (x, top, bottom) in bars)
            g.FillRectangle(Brushes.White, x, top, 3, bottom - top);
        return Icon.FromHandle(bmp.GetHicon());
    }
}
