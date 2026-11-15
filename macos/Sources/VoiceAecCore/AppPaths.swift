import Foundation

/// Well-known locations for the macOS port.
public enum AppPaths {
    public static let supportDir: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support")
        return base.appendingPathComponent("VoiceAec", isDirectory: true)
    }()

    public static let configURL = supportDir.appendingPathComponent("config.json")
    public static let logURL = supportDir.appendingPathComponent("VoiceAec.log")
    public static let userModelsDir = supportDir.appendingPathComponent("models", isDirectory: true)

    /// Directory that contains `wwwroot/index.html`, `macos.css`, `macos.js`
    /// and `models/`. Works both inside `VoiceAec.app/Contents/Resources` and
    /// from a SwiftPM build tree (via `VOICEAEC_RESOURCES`).
    public static let resourcesDir: URL? = {
        var candidates: [URL] = []
        if let env = ProcessInfo.processInfo.environment["VOICEAEC_RESOURCES"], !env.isEmpty {
            candidates.append(URL(fileURLWithPath: env))
        }
        if let res = Bundle.main.resourceURL {
            candidates.append(res)
        }
        let exe = URL(fileURLWithPath: CommandLine.arguments[0]).resolvingSymlinksInPath()
        let dir = exe.deletingLastPathComponent()
        candidates.append(dir)
        candidates.append(dir.appendingPathComponent("../Resources").standardizedFileURL)
        candidates.append(dir.appendingPathComponent("../../Resources").standardizedFileURL)
        candidates.append(dir.appendingPathComponent("../../../macos/Resources").standardizedFileURL)
        candidates.append(URL(fileURLWithPath: FileManager.default.currentDirectoryPath).appendingPathComponent("macos/Resources"))
        for c in candidates {
            if FileManager.default.fileExists(atPath: c.appendingPathComponent("wwwroot/index.html").path) {
                return c
            }
        }
        return nil
    }()

    public static func ensureSupportDir() {
        try? FileManager.default.createDirectory(at: supportDir, withIntermediateDirectories: true)
        try? FileManager.default.createDirectory(at: userModelsDir, withIntermediateDirectories: true)
    }

    /// Places the AEC models may live, highest priority first.
    public static var modelDirs: [URL] {
        var dirs: [URL] = []
        if let env = ProcessInfo.processInfo.environment["VOICEAEC_MODELS"], !env.isEmpty {
            dirs.append(URL(fileURLWithPath: env))
        }
        if let res = resourcesDir {
            dirs.append(res.appendingPathComponent("models", isDirectory: true))
        }
        dirs.append(userModelsDir)
        dirs.append(URL(fileURLWithPath: FileManager.default.currentDirectoryPath).appendingPathComponent("models"))
        return dirs
    }
}
