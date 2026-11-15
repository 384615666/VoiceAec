import AppKit
import AVFoundation
import CoreGraphics
import VoiceAecCore

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var controller: AppController?
    private var windowController: PanelWindowController?
    private var menuBar: MenuBarController?
    private var screenPermissionAlertShown = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildMainMenu()
        do {
            let controller = try AppController()
            self.controller = controller
            let windowController = PanelWindowController(controller: controller)
            let menuBar = MenuBarController(controller: controller, windowController: windowController)
            self.windowController = windowController
            self.menuBar = menuBar
            controller.onChange = { [weak self] in
                self?.menuBar?.refresh()
                self?.windowController?.notifyDeviceChanged()
                self?.checkScreenPermission()
            }
            windowController.showWindow()
            // Ask for the microphone before touching CoreAudio. Starting a HAL
            // unit while the TCC prompt is pending can block coreaudiod.
            ensureMicrophonePermission { [weak self] granted in
                controller.start()
                self?.menuBar?.refresh()
                if !granted { self?.showMicrophoneAlert() }
            }
        } catch {
            let alert = NSAlert()
            alert.messageText = "VoiceAec 启动失败"
            alert.informativeText = error.localizedDescription
            alert.alertStyle = .critical
            alert.addButton(withTitle: "退出")
            alert.runModal()
            NSApp.terminate(nil)
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        controller?.stop()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        windowController?.showWindow()
        return true
    }

    func applicationDidBecomeActive(_ notification: Notification) {
        guard let controller else { return }
        // The user may have just granted Screen Recording in System Settings.
        if #available(macOS 13.0, *), CGPreflightScreenCaptureAccess(),
           controller.engine.status().refError != nil {
            DispatchQueue.global(qos: .userInitiated).async { _ = controller.recheck() }
        }
    }

    func showAbout() {
        windowController?.showWindow()
        windowController?.showAbout()
    }

    private func ensureMicrophonePermission(_ completion: @escaping (Bool) -> Void) {
        let status = AVCaptureDevice.authorizationStatus(for: .audio)
        switch status {
        case .authorized:
            completion(true)
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .audio) { granted in
                DispatchQueue.main.async { completion(granted) }
            }
        default:
            completion(false)
        }
    }

    private func checkScreenPermission() {
        guard !screenPermissionAlertShown, let controller else { return }
        let status = controller.engine.status()
        guard let error = status.refError, !error.isEmpty,
              error.contains("屏幕") || error.lowercased().contains("screen") else { return }
        screenPermissionAlertShown = true
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "需要屏幕录制权限"
        alert.informativeText = "macOS 通过「屏幕录制」权限采集系统声音，作为回声消除的参考信号。请在「系统设置 → 隐私与安全性 → 屏幕录制」中勾选 VoiceAec，然后重新打开应用。"
        alert.alertStyle = .warning
        alert.addButton(withTitle: "打开系统设置")
        alert.addButton(withTitle: "稍后")
        if alert.runModal() == .alertFirstButtonReturn {
            controller.openScreenRecordingSettings()
        }
    }

    private func showMicrophoneAlert() {
        let alert = NSAlert()
        alert.messageText = "需要麦克风权限"
        alert.informativeText = "VoiceAec 需要访问麦克风才能消除回声。请在「系统设置 → 隐私与安全性 → 麦克风」中允许 VoiceAec，然后重新打开应用。"
        alert.alertStyle = .warning
        alert.addButton(withTitle: "打开系统设置")
        alert.addButton(withTitle: "稍后")
        if alert.runModal() == .alertFirstButtonReturn,
           let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone") {
            NSWorkspace.shared.open(url)
        }
    }

    private func buildMainMenu() {
        let mainMenu = NSMenu()

        let appItem = NSMenuItem()
        mainMenu.addItem(appItem)
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "关于 VoiceAec", action: #selector(menuAbout), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "隐藏 VoiceAec", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "退出 VoiceAec", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu

        let editItem = NSMenuItem()
        mainMenu.addItem(editItem)
        let editMenu = NSMenu(title: "编辑")
        editMenu.addItem(withTitle: "撤销", action: Selector(("undo:")), keyEquivalent: "z")
        editMenu.addItem(withTitle: "重做", action: Selector(("redo:")), keyEquivalent: "Z")
        editMenu.addItem(.separator())
        editMenu.addItem(withTitle: "剪切", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        editMenu.addItem(withTitle: "复制", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "粘贴", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: "全选", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = editMenu

        NSApp.mainMenu = mainMenu
    }

    @objc private func menuAbout() {
        showAbout()
    }
}
