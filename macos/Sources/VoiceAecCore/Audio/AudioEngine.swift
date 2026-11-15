import Foundation
import Darwin
import AVFoundation
import CoreAudio

public enum DeviceKind: String {
    case mic
    case reference
    case output
}

public struct EngineStatus {
    public var version = "0.6.3"
    public var lang = "zh"
    public var mode = "v1.2"
    public var switching = false
    public var paused = false
    public var uptimeSec = 0
    public var cpuPercent: Double = 0
    public var micName = ""
    public var refName = ""
    public var outName = ""
    public var micLevelDb: Double = -120
    public var refLevelDb: Double = -120
    public var outLevelDb: Double = -120
    public var micBufferMs: Double = 0
    public var refBufferMs: Double = 0
    public var outBufferMs: Double = 0
    public var refActive = false
    public var hops: Int64 = 0
    public var processedSec: Double = 0
    public var hopsPerSec: Double = 0
    public var silentRefHops: Int64 = 0
    public var underruns: Int64 = 0
    public var trims: Int64 = 0
    public var passthroughs: Int64 = 0
    public var micAvailable = false
    public var outAvailable = false
    public var cableMissing = true
    public var hint = ""
    public var deviceNotice = ""
    public var refError: String?
    public var running = false
}

public final class AudioEngine {
    public static let sampleRate = 16000
    public static let hop = 256
    public static let referenceUID = "system-screen-capture"
    public static func referenceName(lang: String) -> String {
        lang == "en" ? "System audio (ScreenCaptureKit)" : "系统声音（ScreenCaptureKit）"
    }

    private let config: ConfigStore
    private let devices: AudioDeviceManager
    private let micRing = FloatRing(capacity: AudioEngine.sampleRate * 4)
    private let refRing = FloatRing(capacity: AudioEngine.sampleRate * 2)
    private let outRing = FloatRing(capacity: AudioEngine.sampleRate * 2)

    private let stateLock = NSLock()
    private let engineLock = NSLock()
    private let modeLock = NSLock()
    private let deviceLock = NSRecursiveLock()
    private let metricsLock = NSLock()
    private let controlQueue = DispatchQueue(label: "voiceaec.engine.control")

    private var micCapture: AUHALCapture?
    private var outputUnit: AUHALOutput?
    private var systemCapture: AnyObject?
    private var aec: LocalVQEContext?

    private var mode = "v1.2"
    private var switching = false
    private var paused = false
    private var running = false
    private var processorRunning = false
    private var processor: Thread?
    private var startedAt = Date()

    private var micUID: String?
    private var outUID: String?
    private var micName = ""
    private var outName = ""
    private var micAvailable = false
    private var outAvailable = false
    private var refError: String?
    private var deviceNotice = ""

    // Metrics
    private var micDb: Double = -120
    private var refDb: Double = -120
    private var outDb: Double = -120
    private var lastRefAt = Date.distantPast
    private var hops: Int64 = 0
    private var silentRefHops: Int64 = 0
    private var underruns: Int64 = 0
    private var trims: Int64 = 0
    private var passthroughs: Int64 = 0
    private var hopsPerSec: Double = 0
    private var lastRateAt = Date()
    private var lastRateHops: Int64 = 0
    private var lastCpuAt = Date()
    private var lastCpuTime: Double = 0
    private var cpuPercent: Double = 0

    public var onChange: (() -> Void)?

    public init(config: ConfigStore, devices: AudioDeviceManager) {
        self.config = config
        self.devices = devices
        let cfg = config.snapshot()
        mode = ModelCatalog.isKnown(cfg.mode) ? cfg.mode : "v1.2"
        paused = cfg.paused
        micUID = cfg.micUID
        outUID = cfg.outUID
    }

    public func start() {
        controlQueue.async { [weak self] in self?.startLocked() }
    }

    /// Synchronous variant used by tests / headless mode.
    public func startAndWait() {
        controlQueue.sync { [weak self] in self?.startLocked() }
    }

    public func stop() {
        controlQueue.sync { [weak self] in self?.stopLocked() }
    }

    private func startLocked() {
        deviceLock.lock(); defer { deviceLock.unlock() }
        guard !running else { return }
        startedAt = Date()
        devices.refresh()
        Log.info("engine starting")
        selectDevicesLocked()
        loadModeLocked(mode, persist: false)
        startOutputLocked()
        startMicLocked()
        startSystemCaptureLocked()
        startProcessorLocked()
        running = true
        refreshMetricsClock()
        notify()
    }

    private func stopLocked() {
        deviceLock.lock(); defer { deviceLock.unlock() }
        guard running else { return }
        processorRunning = false
        if let processor {
            processor.cancel()
            _ = processor
        }
        Thread.sleep(forTimeInterval: 0.08)
        stopMicLocked()
        stopOutputLocked()
        stopSystemCaptureLocked()
        engineLock.lock()
        aec = nil
        engineLock.unlock()
        micRing.clear(); refRing.clear(); outRing.clear()
        running = false
        Log.info("engine stopped")
        notify()
    }

    private func selectDevicesLocked() {
        let cfg = config.snapshot()
        let input = [devices.device(uid: cfg.micUID), devices.device(uid: devices.defaultInputUID)]
            .compactMap { $0 }.first(where: { $0.inputs > 0 }) ?? devices.inputs.first
        // If the user explicitly picked an output, respect it. Otherwise prefer
        // BlackHole as soon as it appears (e.g. installed after the first run).
        let configuredOutput = devices.device(uid: cfg.outUID).flatMap { $0.outputs > 0 ? $0 : nil }
        let output: AudioDeviceInfo?
        if cfg.outUserChosen, let configuredOutput {
            output = configuredOutput
        } else if let blackHole = devices.blackHoleOutput {
            output = blackHole
        } else {
            // Do not blast the microphone into the speakers on first run.
            // The user can still pick a physical device explicitly in the panel.
            output = nil
        }
        micUID = input?.uid
        micName = input?.name ?? ""
        micAvailable = input != nil
        outUID = output?.uid
        outName = output?.name ?? ""
        outAvailable = output != nil
        if cfg.micUID == nil, let uid = micUID {
            config.update { $0.micUID = uid }
        }
        if cfg.outUID == nil, let uid = outUID {
            config.update { $0.outUID = uid }
        }
        Log.info("devices mic=\(micName) out=\(outName)")
    }

    private func startMicLocked() {
        guard micCapture == nil, let uid = micUID,
              let device = devices.device(uid: uid), device.inputs > 0 else {
            micAvailable = false
            return
        }
        do {
            let channels = devices.inputChannelCount(device.id)
            let rate = devices.nominalSampleRate(device.id)
            let capture = try AUHALCapture(deviceID: device.id, nativeRate: rate,
                                           channels: channels, ring: micRing)
            try capture.start()
            micCapture = capture
            micName = device.name
            micUID = device.uid
            micAvailable = true
            micRing.clear()
            if deviceNotice.hasPrefix("麦克风") { deviceNotice = "" }
            Log.info("microphone ready: \(device.name)")
        } catch {
            micAvailable = false
            deviceNotice = "麦克风「\(device.name)」启动失败：\(error.localizedDescription)"
            Log.error(deviceNotice)
        }
    }

    private func stopMicLocked() {
        micCapture?.stop()
        micCapture = nil
        micAvailable = false
    }

    private func startOutputLocked() {
        guard outputUnit == nil, let uid = outUID,
              let device = devices.device(uid: uid), device.outputs > 0 else {
            outAvailable = false
            return
        }
        do {
            let channels = devices.outputChannelCount(device.id)
            let output = AUHALOutput(deviceID: device.id, channels: channels, ring: outRing,
                                     onUnderrun: { [weak self] in self?.countUnderrun() })
            try output.start()
            outputUnit = output
            outName = device.name
            outAvailable = true
            outRing.clear()
            if deviceNotice.hasPrefix("输出设备") { deviceNotice = "" }
            Log.info("output ready: \(device.name)")
        } catch {
            outAvailable = false
            deviceNotice = "输出设备「\(device.name)」启动失败：\(error.localizedDescription)"
            Log.error(deviceNotice)
        }
    }

    private func stopOutputLocked() {
        outputUnit?.stop()
        outputUnit = nil
        outAvailable = false
    }

    private func startSystemCaptureLocked() {
        guard #available(macOS 13.0, *) else {
            refError = "需要 macOS 13 或更高版本"
            return
        }
        if let capture = systemCapture as? SystemAudioCapture, capture.isRunning { return }
        let capture = SystemAudioCapture(ring: refRing)
        capture.onError = { [weak self] message in self?.setRefError(message) }
        systemCapture = capture
        Task { [weak self] in
            do {
                try await capture.start()
                self?.setRefError(nil)
            } catch {
                self?.setRefError(error.localizedDescription)
            }
        }
    }

    private func stopSystemCaptureLocked() {
        if #available(macOS 13.0, *), let capture = systemCapture as? SystemAudioCapture {
            capture.stop()
        }
        systemCapture = nil
    }

    private func setRefError(_ message: String?) {
        stateLock.lock(); refError = message; stateLock.unlock()
        if let message { Log.error("system capture: \(message)") }
        notify()
    }

    private func startProcessorLocked() {
        guard !processorRunning else { return }
        processorRunning = true
        let thread = Thread { [weak self] in self?.processLoop() }
        thread.name = "voiceaec.dsp"
        thread.qualityOfService = .userInteractive
        processor = thread
        thread.start()
    }

    private func processLoop() {
        let hop = AudioEngine.hop
        let mic = UnsafeMutablePointer<Float>.allocate(capacity: hop)
        let ref = UnsafeMutablePointer<Float>.allocate(capacity: hop)
        let out = UnsafeMutablePointer<Float>.allocate(capacity: hop)
        mic.initialize(repeating: 0, count: hop)
        ref.initialize(repeating: 0, count: hop)
        out.initialize(repeating: 0, count: hop)
        defer {
            mic.deallocate(); ref.deallocate(); out.deallocate()
        }
        while processorRunning {
            if isPaused {
                usleep(5000)
                continue
            }
            guard micRing.available >= hop else {
                usleep(2000)
                continue
            }
            let gotMic = micRing.read(into: mic, count: hop)
            guard gotMic == hop else { continue }

            let gotRef = refRing.read(into: ref, count: hop)
            if gotRef < hop { ref.advanced(by: gotRef).initialize(repeating: 0, count: hop - gotRef) }
            trimReference()

            var anyRef = false
            for i in 0..<hop where ref[i] != 0 { anyRef = true; break }
            let micLevel = rmsDb(mic, hop)
            let refLevel = rmsDb(ref, hop)

            var usedPassthrough = false
            engineLock.lock()
            if let aec, mode != "off" {
                let rc = aec.process(mic: mic, reference: ref, output: out, count: hop)
                if rc != 0 {
                    out.update(from: mic, count: hop)
                    usedPassthrough = true
                    passthroughs += 1
                }
            } else {
                out.update(from: mic, count: hop)
            }
            engineLock.unlock()

            let outLevel = rmsDb(out, hop)
            outRing.write(out, count: hop)
            hops += 1
            if anyRef {
                lastRefAt = Date()
            } else {
                silentRefHops += 1
            }
            metricsLock.lock()
            micDb = micLevel
            refDb = refLevel
            outDb = outLevel
            metricsLock.unlock()
            _ = usedPassthrough
        }
    }

    private func trimReference() {
        let available = refRing.available
        let maxSamples = AudioEngine.sampleRate * 3 / 10   // 300 ms
        guard available > maxSamples else { return }
        let target = AudioEngine.sampleRate * 3 / 50        // 60 ms
        let dropped = refRing.drop(available - target)
        if dropped > 0 { trims += 1 }
    }

    private func countUnderrun() {
        if isPaused { return }
        if Date().timeIntervalSince(startedAt) < 1.5 { return }
        underruns += 1
    }

    private var isPaused: Bool {
        stateLock.lock(); defer { stateLock.unlock() }
        return paused
    }

    public func setPaused(_ value: Bool) {
        stateLock.lock()
        paused = value
        stateLock.unlock()
        config.update { $0.paused = value }
        micRing.clear(); refRing.clear(); outRing.clear()
        engineLock.lock()
        aec?.reset()
        engineLock.unlock()
        Log.info("paused=\(value)")
        notify()
    }

    public var isPausedValue: Bool { isPaused }

    public func togglePaused() { setPaused(!isPaused) }

    @discardableResult
    public func setMode(_ requested: String, persist: Bool = true) -> String? {
        modeLock.lock(); defer { modeLock.unlock() }
        guard ModelCatalog.isKnown(requested) else { return "未知模式 '\(requested)'" }
        if requested != "off", ModelCatalog.path(for: requested) == nil {
            return "模型文件不存在：\(requested)。请把 GGUF 放到应用 Resources/models 或 ~/Library/Application Support/VoiceAec/models。"
        }
        stateLock.lock(); switching = true; stateLock.unlock()
        defer {
            stateLock.lock(); switching = false; stateLock.unlock()
        }
        if requested == "off" {
            engineLock.lock()
            let old = aec
            aec = nil
            mode = "off"
            engineLock.unlock()
            _ = old
            if persist { config.update { $0.mode = "off" } }
            notify()
            return nil
        }
        do {
            let context = try LocalVQEContext(modelPath: ModelCatalog.path(for: requested)!,
                                              threads: preferredThreads())
            engineLock.lock()
            let old = aec
            aec = context
            mode = requested
            engineLock.unlock()
            _ = old
            if persist { config.update { $0.mode = requested } }
            Log.info("mode -> \(requested)")
            notify()
            return nil
        } catch {
            Log.error("mode load failed: \(error.localizedDescription)")
            return "加载失败：\(error.localizedDescription)"
        }
    }

    private func loadModeLocked(_ requested: String, persist: Bool) {
        let choice = ModelCatalog.isKnown(requested) && ModelCatalog.info(for: requested, lang: "zh").available
            ? requested : (ModelCatalog.path(for: "v1.2") != nil ? "v1.2" : "off")
        if choice != requested {
            Log.warn("mode '\(requested)' unavailable, using '\(choice)'")
        }
        if let error = setMode(choice, persist: persist) {
            Log.error("initial mode failed: \(error); fallback to off")
            _ = setMode("off", persist: persist)
        }
    }

    private func preferredThreads() -> Int32 {
        let cores = ProcessInfo.processInfo.activeProcessorCount
        return Int32(max(1, min(4, max(2, cores / 2))))
    }

    @discardableResult
    public func setDevice(kind: DeviceKind, name: String) -> String? {
        deviceLock.lock(); defer { deviceLock.unlock() }
        let cfg = config.snapshot()
        switch kind {
        case .reference:
            return nil // ScreenCaptureKit is the only reference source on macOS.
        case .mic:
            guard let device = devices.device(name: name, input: true) else {
                return "找不到麦克风「\(name)」"
            }
            stopMicLocked()
            micUID = device.uid
            micName = device.name
            config.update { $0.micUID = device.uid }
            startMicLocked()
            if !micAvailable { return "麦克风「\(name)」启动失败" }
            micRing.clear()
        case .output:
            guard let device = devices.device(name: name, input: false) else {
                return "找不到输出设备「\(name)」"
            }
            stopOutputLocked()
            outUID = device.uid
            outName = device.name
            config.update { $0.outUID = device.uid; $0.outUserChosen = true }
            startOutputLocked()
            if !outAvailable { return "输出设备「\(name)」启动失败" }
            outRing.clear()
        }
        _ = cfg
        deviceNotice = ""
        notify()
        return nil
    }

    public func recheck() -> String? {
        deviceLock.lock(); defer { deviceLock.unlock() }
        devices.refresh()
        if !running { startLocked(); return nil }
        if !micAvailable {
            if let uid = micUID, devices.device(uid: uid) != nil { startMicLocked() }
            else if let input = devices.device(uid: devices.defaultInputUID) ?? devices.inputs.first {
                micUID = input.uid; micName = input.name; startMicLocked()
                config.update { $0.micUID = input.uid }
            }
        }
        if !outAvailable {
            let cfg = config.snapshot()
            if cfg.outUserChosen, let uid = cfg.outUID, let device = devices.device(uid: uid), device.outputs > 0 {
                outUID = device.uid; outName = device.name; startOutputLocked()
            } else if let blackHole = devices.blackHoleOutput {
                outUID = blackHole.uid; outName = blackHole.name; startOutputLocked()
            }
        }
        if #available(macOS 13.0, *),
           (systemCapture as? SystemAudioCapture)?.isRunning != true {
            stopSystemCaptureLocked()
            startSystemCaptureLocked()
        }
        notify()
        if !micAvailable && !outAvailable { return "仍没有找到可用的麦克风或输出设备" }
        if !micAvailable { return "仍没有找到可用的麦克风" }
        if !outAvailable {
            return "仍没有找到 BlackHole 虚拟声卡。请先安装 BlackHole 2ch，再点「重新检测」。"
        }
        return nil
    }

    /// Restart the microphone after the user grants Microphone access.
    public func restartMicrophone() {
        deviceLock.lock(); defer { deviceLock.unlock() }
        controlQueue.sync {
            guard running else { return }
            stopMicLocked()
            if let uid = micUID, let device = devices.device(uid: uid) {
                micUID = device.uid
                micName = device.name
            }
            startMicLocked()
            notify()
        }
    }

    public func devicesDidChange() {
        deviceLock.lock(); defer { deviceLock.unlock() }
        devices.refresh()
        guard running else { return }
        if let uid = micUID, devices.device(uid: uid) == nil, micAvailable {
            stopMicLocked()
            deviceNotice = "麦克风被拔出了，插回后会自动恢复。"
        } else if !micAvailable, let uid = micUID, devices.device(uid: uid) != nil {
            startMicLocked()
            deviceNotice = ""
        }
        if let uid = outUID, devices.device(uid: uid) == nil, outAvailable {
            stopOutputLocked()
            deviceNotice = "输出设备被拔出了，插回后会自动恢复。"
        } else if !outAvailable, let uid = outUID, devices.device(uid: uid) != nil {
            startOutputLocked()
            deviceNotice = ""
        }
        // A virtual device can be installed while VoiceAec is running.
        let cfg = config.snapshot()
        if !cfg.outUserChosen, let blackHole = devices.blackHoleOutput, blackHole.uid != outUID {
            stopOutputLocked()
            outUID = blackHole.uid
            outName = blackHole.name
            startOutputLocked()
            outRing.clear()
            deviceNotice = ""
            Log.info("auto-selected \(blackHole.name) after hot-plug")
        }
        notify()
    }

    public var currentOutputName: String {
        stateLock.lock(); defer { stateLock.unlock() }
        return outName
    }

    public var currentMicName: String {
        stateLock.lock(); defer { stateLock.unlock() }
        return micName
    }

    public var currentMode: String {
        stateLock.lock(); defer { stateLock.unlock() }
        return mode
    }

    public var cableMissing: Bool { devices.blackHoleOutput == nil }

    public var inputDeviceNames: [String] { devices.inputs.map(\.name) }
    public var outputDeviceNames: [String] { devices.outputs.map(\.name) }

    public func status() -> EngineStatus {
        let cfg = config.snapshot()
        let lang = cfg.lang
        var status = EngineStatus()
        status.lang = lang
        stateLock.lock()
        status.mode = mode
        status.switching = switching
        status.paused = paused
        status.micName = micName
        status.refName = AudioEngine.referenceName(lang: lang)
        status.outName = outName
        status.micAvailable = micAvailable
        status.outAvailable = outAvailable
        status.refError = refError
        status.deviceNotice = deviceNotice
        status.running = running
        let now = Date()
        status.uptimeSec = Int(now.timeIntervalSince(startedAt))
        status.cableMissing = devices.blackHoleOutput == nil
        stateLock.unlock()

        metricsLock.lock()
        status.micLevelDb = round1(micDb)
        status.refLevelDb = round1(refDb)
        status.outLevelDb = round1(outDb)
        status.micBufferMs = round1(Double(micRing.available) * 1000.0 / Double(AudioEngine.sampleRate))
        status.refBufferMs = round1(Double(refRing.available) * 1000.0 / Double(AudioEngine.sampleRate))
        status.outBufferMs = round1(Double(outRing.available) * 1000.0 / Double(AudioEngine.sampleRate))
        status.refActive = now.timeIntervalSince(lastRefAt) < 1.5
        status.hops = hops
        status.silentRefHops = silentRefHops
        status.underruns = underruns
        status.trims = trims
        status.passthroughs = passthroughs
        status.processedSec = round1(Double(hops * Int64(AudioEngine.hop)) / Double(AudioEngine.sampleRate))
        let elapsed = now.timeIntervalSince(lastRateAt)
        if elapsed >= 1.0 {
            hopsPerSec = Double(hops - lastRateHops) / elapsed
            lastRateHops = hops
            lastRateAt = now
            updateCPU(now: now)
        }
        status.hopsPerSec = round1(hopsPerSec)
        status.cpuPercent = round1(cpuPercent)
        metricsLock.unlock()

        if !status.outAvailable {
            status.outLevelDb = -120
            status.outBufferMs = 0
        }
        status.hint = hint(for: status)
        return status
    }

    private func hint(for status: EngineStatus) -> String {
        let zh = status.lang != "en"
        if status.cableMissing {
            return zh
                ? "还差一步：macOS 需要 BlackHole 虚拟声卡。安装后重启 VoiceAec，语音软件里就能选到 BlackHole 2ch。"
                : "One more step: macOS needs the BlackHole virtual audio device. Install it, restart VoiceAec, then pick BlackHole 2ch in your voice app."
        }
        if !status.cableMissing && !status.outName.lowercased().contains("blackhole") {
            return zh
                ? "当前输出是「\(status.outName)」，处理后的声音不会进入语音软件。请在「输出」卡片里选择 BlackHole 2ch。"
                : "The output is \"\(status.outName)\", so the cleaned audio never reaches your voice app. Pick BlackHole 2ch in the Output card."
        }
        if status.paused {
            return zh ? "已暂停：现在不采集也不输出声音，点右上角「恢复处理与输出」就能继续。"
                      : "Paused: no audio is captured or played. Click \"Resume processing & output\" in the top-right corner to continue."
        }
        if let error = status.refError, !error.isEmpty {
            return zh
                ? "系统声音回采未启动：请在「系统设置 → 隐私与安全性 → 屏幕录制」中允许 VoiceAec，然后点「重新检测」。"
                : "System audio capture is not running. Allow VoiceAec in System Settings → Privacy & Security → Screen Recording, then click Recheck."
        }
        if !status.deviceNotice.isEmpty { return status.deviceNotice }
        if !status.micAvailable {
            return zh ? "没有找到可用的麦克风，请在面板里选择输入设备。" : "No microphone found. Pick an input device in the panel."
        }
        if !status.outAvailable {
            return zh ? "没有找到可用的输出设备，请在面板里选择输出设备。" : "No output device found. Pick an output device in the panel."
        }
        return zh
            ? "打开 Discord / QQ，把「麦克风」选成 BlackHole 2ch；系统声音继续从「\(shortName(status.outName))」之外播放，就能一边外放一边消除回声。"
            : "Open Discord or your voice app and set the microphone to BlackHole 2ch. Audio keeps playing normally, so you can use speakers and still cancel the echo."
    }

    private func shortName(_ name: String) -> String {
        name.contains("(") ? String(name[..<name.firstIndex(of: "(")!]).trimmingCharacters(in: .whitespaces) : name
    }

    private func round1(_ value: Double) -> Double {
        (value * 10).rounded() / 10
    }

    private func rmsDb(_ samples: UnsafePointer<Float>, _ count: Int) -> Double {
        var sum = 0.0
        for i in 0..<count {
            let v = Double(samples[i])
            sum += v * v
        }
        let rms = sqrt(sum / Double(max(count, 1)))
        return max(-120.0, 20.0 * log10(max(rms, 1e-6)))
    }

    private func refreshMetricsClock() {
        metricsLock.lock()
        lastRateAt = Date()
        lastRateHops = hops
        lastCpuAt = Date()
        lastCpuTime = processCPUTime()
        metricsLock.unlock()
    }

    private func updateCPU(now: Date) {
        let time = processCPUTime()
        let dt = now.timeIntervalSince(lastCpuAt)
        if dt > 0.2 {
            let cores = Double(max(1, ProcessInfo.processInfo.activeProcessorCount))
            let percent = (time - lastCpuTime) / dt / cores * 100.0
            cpuPercent = max(0, min(100, percent))
            lastCpuTime = time
            lastCpuAt = now
        }
    }

    private func processCPUTime() -> Double {
        var info = task_basic_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_basic_info_data_t>.stride / MemoryLayout<natural_t>.stride)
        let result = withUnsafeMutablePointer(to: &info) { ptr in
            ptr.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                task_info(mach_task_self_, task_flavor_t(TASK_BASIC_INFO), $0, &count)
            }
        }
        guard result == KERN_SUCCESS else { return lastCpuTime }
        return Double(info.user_time.seconds + info.system_time.seconds)
            + Double(info.user_time.microseconds + info.system_time.microseconds) / 1_000_000.0
    }

    private func notify() {
        onChange?()
    }

    public var modelAvailability: [String: Bool] {
        var result: [String: Bool] = [:]
        for mode in ModelCatalog.modes { result[mode] = ModelCatalog.info(for: mode, lang: "zh").available }
        return result
    }
}
