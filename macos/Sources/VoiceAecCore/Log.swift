import Foundation

/// Small thread-safe logger. Writes to stderr (useful when launched from a
/// terminal) and to `~/Library/Application Support/VoiceAec/VoiceAec.log`.
public enum Log {
    private static let lock = NSLock()
    private static let formatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm:ss.SSS"
        f.locale = Locale(identifier: "en_US_POSIX")
        return f
    }()

    public static func info(_ message: String) { write("I", message) }
    public static func warn(_ message: String) { write("W", message) }
    public static func error(_ message: String) { write("E", message) }

    public static func rotateIfNeeded() {
        AppPaths.ensureSupportDir()
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: AppPaths.logURL.path),
              let size = attrs[.size] as? NSNumber, size.intValue > 2 * 1024 * 1024 else { return }
        let old = AppPaths.logURL.appendingPathExtension("old")
        try? FileManager.default.removeItem(at: old)
        try? FileManager.default.moveItem(at: AppPaths.logURL, to: old)
    }

    private static func write(_ level: String, _ message: String) {
        let line = "[\(formatter.string(from: Date()))] [\(level)] \(message)\n"
        lock.lock()
        defer { lock.unlock() }
        FileHandle.standardError.write(Data(line.utf8))
        AppPaths.ensureSupportDir()
        if let handle = try? FileHandle(forWritingTo: AppPaths.logURL) {
            defer { try? handle.close() }
            _ = try? handle.seekToEnd()
            try? handle.write(contentsOf: Data(line.utf8))
        } else {
            try? Data(line.utf8).write(to: AppPaths.logURL)
        }
    }
}
