import Foundation

public struct AppConfig: Codable, Equatable {
    public var micUID: String?
    public var outUID: String?
    public var mode: String
    public var lang: String
    public var paused: Bool
    public var refDelayMs: Int
    public var autoStart: Bool
    public var outUserChosen: Bool

    public init(micUID: String? = nil, outUID: String? = nil, mode: String = "v1.2",
                lang: String = "zh", paused: Bool = false, refDelayMs: Int = 0,
                autoStart: Bool = false, outUserChosen: Bool = false) {
        self.micUID = micUID
        self.outUID = outUID
        self.mode = mode
        self.lang = lang
        self.paused = paused
        self.refDelayMs = refDelayMs
        self.autoStart = autoStart
        self.outUserChosen = outUserChosen
    }

    private enum CodingKeys: String, CodingKey {
        case micUID, outUID, mode, lang, paused, refDelayMs, autoStart, outUserChosen
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        micUID = try c.decodeIfPresent(String.self, forKey: .micUID)
        outUID = try c.decodeIfPresent(String.self, forKey: .outUID)
        mode = try c.decodeIfPresent(String.self, forKey: .mode) ?? "v1.2"
        lang = try c.decodeIfPresent(String.self, forKey: .lang) ?? "zh"
        paused = try c.decodeIfPresent(Bool.self, forKey: .paused) ?? false
        refDelayMs = try c.decodeIfPresent(Int.self, forKey: .refDelayMs) ?? 0
        autoStart = try c.decodeIfPresent(Bool.self, forKey: .autoStart) ?? false
        outUserChosen = try c.decodeIfPresent(Bool.self, forKey: .outUserChosen) ?? false
    }
}

public final class ConfigStore {
    private let lock = NSLock()
    private var value: AppConfig

    public init() {
        AppPaths.ensureSupportDir()
        if let data = try? Data(contentsOf: AppPaths.configURL),
           let decoded = try? JSONDecoder().decode(AppConfig.self, from: data) {
            value = decoded
        } else {
            value = AppConfig()
        }
        if value.lang != "zh" && value.lang != "en" { value.lang = "zh" }
        if value.refDelayMs < 0 || value.refDelayMs > 500 { value.refDelayMs = 0 }
    }

    public func snapshot() -> AppConfig {
        lock.lock(); defer { lock.unlock() }
        return value
    }

    /// Restore factory defaults (used by the `--reset` maintenance command).
    public func resetToDefaults() {
        lock.lock()
        value = AppConfig()
        let copy = value
        lock.unlock()
        save(copy)
    }

    public func update(_ body: (inout AppConfig) -> Void) {
        lock.lock()
        body(&value)
        let copy = value
        lock.unlock()
        save(copy)
    }

    private func save(_ config: AppConfig) {
        do {
            try FileManager.default.createDirectory(at: AppPaths.supportDir, withIntermediateDirectories: true)
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            let data = try encoder.encode(config)
            try data.write(to: AppPaths.configURL, options: .atomic)
        } catch {
            Log.error("config save failed: \(error)")
        }
    }
}
