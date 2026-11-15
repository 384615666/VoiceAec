import Foundation
import ServiceManagement

public enum Autostart {
    public static var isEnabled: Bool {
        if #available(macOS 13.0, *) {
            return SMAppService.mainApp.status == .enabled
        }
        return false
    }

    public static var needsApproval: Bool {
        if #available(macOS 13.0, *) {
            return SMAppService.mainApp.status == .requiresApproval
        }
        return false
    }

    public static func setEnabled(_ enabled: Bool) -> String? {
        guard #available(macOS 13.0, *) else { return "开机自启需要 macOS 13 或更高版本" }
        do {
            if enabled {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
            return nil
        } catch {
            Log.error("autostart \(enabled) failed: \(error)")
            return "设置开机自启失败：\(error.localizedDescription)"
        }
    }
}
