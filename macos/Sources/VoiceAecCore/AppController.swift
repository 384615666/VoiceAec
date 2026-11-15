import Foundation
import AppKit

final class PanelAPI {
    private let config: ConfigStore
    private let devices: AudioDeviceManager
    private let engine: AudioEngine

    var openURL: (URL) -> Void = { NSWorkspace.shared.open($0) }
    var quit: () -> Void = { DispatchQueue.main.async { NSApplication.shared.terminate(nil) } }

    init(config: ConfigStore, devices: AudioDeviceManager, engine: AudioEngine) {
        self.config = config
        self.devices = devices
        self.engine = engine
    }

    var currentLanguage: String { config.snapshot().lang }

    func handle(_ request: HTTPRequest) -> HTTPResponse {
        switch (request.method, request.path) {
        case ("GET", "/api/status"):
            return .json(statusPayload())
        case ("GET", "/api/devices"):
            return .json(devicePayload())
        case ("POST", "/api/devices"):
            return switchDevices(request)
        case ("POST", "/api/model"):
            let requested = request.text.trimmingCharacters(in: .whitespacesAndNewlines)
            if let error = engine.setMode(requested) {
                return .json(["ok": false, "error": error])
            }
            return .json(["ok": true, "mode": engine.currentMode])
        case ("POST", "/api/pause"):
            let paused = request.text.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() == "true"
            engine.setPaused(paused)
            return .json(["ok": true, "paused": engine.isPausedValue])
        case ("POST", "/api/lang"):
            let lang = request.text.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            guard lang == "zh" || lang == "en" else {
                return .json(["ok": false, "error": "unknown language"])
            }
            config.update { $0.lang = lang }
            return .json(["ok": true, "lang": lang])
        case ("GET", "/api/autostart"):
            return .json(["ok": true, "enabled": Autostart.isEnabled])
        case ("POST", "/api/autostart"):
            let enabled = request.text.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() == "true"
            if let error = Autostart.setEnabled(enabled) {
                return .json(["ok": false, "enabled": Autostart.isEnabled, "error": error])
            }
            config.update { $0.autoStart = enabled }
            return .json(["ok": true, "enabled": Autostart.isEnabled])
        case ("POST", "/api/recheck"):
            if let error = engine.recheck() {
                return .json(["ok": false, "error": error])
            }
            devices.refresh()
            return .json(["ok": true])
        case ("POST", "/api/vbcable/install"):
            openURL(URL(string: "https://existential.audio/blackhole/")!)
            return .json(["ok": true, "running": false,
                          "message": "已打开 BlackHole 下载页。安装完成并重启后，VoiceAec 会自动检测到它。"])
        case ("POST", "/api/sound-settings"):
            if let url = URL(string: "x-apple.systempreferences:com.apple.Sound-Settings.extension") {
                openURL(url)
            }
            return .json(["ok": true])
        case ("POST", "/api/quit"):
            quit()
            return .json(["ok": true])
        case ("POST", "/api/reset"):
            config.resetToDefaults()
            let action = quit
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { action() }
            return .json(["ok": true, "message": "settings reset"])
        case ("POST", "/api/pin-default"):
            return .json(["ok": true, "pinDefaultMic": false])
        case ("GET", "/api/obs-plugin"):
            return .json(["ok": true, "installed": false, "managed": false, "busy": false,
                          "canInstall": false, "canUninstall": false, "payloadAvailable": false,
                          "message": "macOS 版暂未提供 OBS 插件。"])
        case ("POST", "/api/obs-plugin/install"), ("POST", "/api/obs-plugin/uninstall"):
            return .json(["ok": false, "error": "macOS 版暂未提供 OBS 插件。"])
        default:
            return .json(["ok": false, "error": "not found"], status: 404)
        }
    }

    private func statusPayload() -> [String: Any] {
        let status = engine.status()
        let lang = status.lang
        let missing = lang == "en" ? "(device not found)" : "（未找到设备）"
        var models: [String: Any] = [:]
        for info in ModelCatalog.all(lang: lang) {
            models[info.mode] = ["available": info.available, "label": info.label]
        }
        let refName = status.refName
        return [
            "ok": true,
            "running": status.running,
            "version": status.version,
            "lang": lang,
            "mode": status.mode,
            "switching": status.switching,
            "paused": status.paused,
            "uptimeSec": status.uptimeSec,
            "cpuPercent": status.cpuPercent,
            "mic": [
                "name": status.micName.isEmpty ? missing : status.micName,
                "levelDb": status.micLevelDb,
                "bufferMs": status.micBufferMs,
            ],
            "ref": [
                "name": refName,
                "active": status.refActive,
                "levelDb": status.refLevelDb,
                "bufferMs": status.refBufferMs,
            ],
            "out": [
                "name": status.outName.isEmpty ? missing : status.outName,
                "levelDb": status.outLevelDb,
                "bufferMs": status.outBufferMs,
            ],
            "stats": [
                "hops": status.hops,
                "processedSec": status.processedSec,
                "hopsPerSec": status.hopsPerSec,
                "silentRefHops": status.silentRefHops,
                "underruns": status.underruns,
                "trims": status.trims,
                "passthroughs": status.passthroughs,
            ],
            "models": models,
            "autostart": Autostart.isEnabled,
            "pinDefaultMic": false,
            "cableMissing": status.cableMissing,
            "lanAvailable": false,
            "lanUrl": "",
            "hint": status.hint,
            "deviceNotice": status.deviceNotice,
            "refError": status.refError ?? "",
        ]
    }

    private func devicePayload() -> [String: Any] {
        let status = engine.status()
        return [
            "ok": true,
            "inputs": engine.inputDeviceNames,
            "loops": [["name": status.refName, "kind": "playback"]],
            "outputs": engine.outputDeviceNames,
            "ref2Choices": [],
            "current": [
                "mic": status.micName,
                "ref": status.refName,
                "ref2": "none",
                "out": status.outName,
            ],
        ]
    }

    private func switchDevices(_ request: HTTPRequest) -> HTTPResponse {
        guard let object = try? JSONSerialization.jsonObject(with: request.body) as? [String: Any] else {
            return .json(["ok": false, "error": "invalid json"])
        }
        var firstError: String?
        if let name = object["mic"] as? String, let error = engine.setDevice(kind: .mic, name: name) {
            firstError = firstError ?? error
        }
        if let name = object["ref"] as? String, let error = engine.setDevice(kind: .reference, name: name) {
            firstError = firstError ?? error
        }
        if let name = object["out"] as? String, let error = engine.setDevice(kind: .output, name: name) {
            firstError = firstError ?? error
        }
        let status = engine.status()
        let payload: [String: Any] = [
            "ok": firstError == nil,
            "error": firstError ?? "",
            "current": [
                "mic": status.micName,
                "ref": status.refName,
                "ref2": "none",
                "out": status.outName,
            ],
        ]
        return .json(payload)
    }
}

public final class AppController {
    public let config: ConfigStore
    public let devices: AudioDeviceManager
    public let engine: AudioEngine
    public let server: PanelHTTPServer
    public var onChange: (() -> Void)?

    private let api: PanelAPI

    public init() throws {
        Log.rotateIfNeeded()
        let config = ConfigStore()
        let devices = AudioDeviceManager()
        let engine = AudioEngine(config: config, devices: devices)
        let api = PanelAPI(config: config, devices: devices, engine: engine)
        let server = PanelHTTPServer(api: api)
        self.config = config
        self.devices = devices
        self.engine = engine
        self.api = api
        self.server = server

        api.openURL = { url in
            DispatchQueue.main.async { NSWorkspace.shared.open(url) }
        }
        api.quit = {
            DispatchQueue.main.async { NSApplication.shared.terminate(nil) }
        }
        engine.onChange = { [weak self] in
            DispatchQueue.main.async { self?.onChange?() }
        }
        devices.onChange = { [weak self] in
            self?.engine.devicesDidChange()
            self?.onChange?()
        }
        try server.start()
        Log.info("VoiceAec Mac started, panel=\(server.baseURL.absoluteString)")
    }

    public var panelURL: URL { server.baseURL }

    public func start() {
        engine.start()
        devices.startMonitoring()
        onChange?()
    }

    public func stop() {
        devices.stopMonitoring()
        engine.stop()
        server.stop()
    }

    public func setMode(_ mode: String) -> String? { engine.setMode(mode) }
    public func setPaused(_ paused: Bool) { engine.setPaused(paused) }
    public func restartMicrophone() { engine.restartMicrophone() }
    public func togglePaused() { engine.togglePaused() }
    public func recheck() -> String? { engine.recheck() }

    public var autostartEnabled: Bool { Autostart.isEnabled }

    @discardableResult
    public func setAutostart(_ enabled: Bool) -> String? {
        if let error = Autostart.setEnabled(enabled) { return error }
        config.update { $0.autoStart = enabled }
        return nil
    }

    public func openScreenRecordingSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
            NSWorkspace.shared.open(url)
        }
    }

    public func openBlackHoleDownload() {
        if let url = URL(string: "https://existential.audio/blackhole/") {
            NSWorkspace.shared.open(url)
        }
    }

    public func openSoundSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.Sound-Settings.extension") {
            NSWorkspace.shared.open(url)
        }
    }

    /// Restore default settings and quit so the next launch starts clean.
    public func resetSettings() {
        config.resetToDefaults()
        Log.info("settings reset; quitting")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
            NSApplication.shared.terminate(nil)
        }
    }

    public func openLogFolder() {
        NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: AppPaths.supportDir.path)
    }
}
