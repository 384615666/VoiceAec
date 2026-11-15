import Foundation

public struct ModelInfo: Equatable {
    public let mode: String
    public let file: String
    public let label: String
    public let available: Bool
    public let path: String?
}

public enum ModelCatalog {
    public static let modes = ["off", "v1.2", "v1.3", "v1.4"]

    private static let files: [String: String] = [
        "v1.2": "localvqe-v1.2-1.3M-f32.gguf",
        "v1.3": "localvqe-v1.3-4.8M-f32.gguf",
        "v1.4": "localvqe-v1.4-aec-200K-f32.gguf",
    ]

    public static func isKnown(_ mode: String) -> Bool { modes.contains(mode) }

    public static func path(for mode: String) -> String? {
        guard let file = files[mode] else { return nil }
        for dir in AppPaths.modelDirs {
            let candidate = dir.appendingPathComponent(file)
            if FileManager.default.fileExists(atPath: candidate.path) { return candidate.path }
        }
        return nil
    }

    public static func label(for mode: String, lang: String) -> String {
        let zh = lang != "en"
        switch mode {
        case "off": return zh ? "关闭（直通）" : "Off (passthrough)"
        case "v1.2": return zh ? "均衡模式" : "Balanced mode"
        case "v1.3": return zh ? "强力模式" : "Aggressive mode"
        case "v1.4": return zh ? "纯回声消除模式" : "Echo-only mode"
        default: return mode
        }
    }

    public static func info(for mode: String, lang: String) -> ModelInfo {
        let path = self.path(for: mode)
        return ModelInfo(mode: mode, file: files[mode] ?? "", label: label(for: mode, lang: lang),
                         available: mode == "off" || path != nil, path: path)
    }

    public static func all(lang: String) -> [ModelInfo] {
        modes.map { info(for: $0, lang: lang) }
    }
}
