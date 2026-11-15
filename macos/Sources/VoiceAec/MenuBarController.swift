import AppKit
import VoiceAecCore

final class MenuBarController: NSObject, NSMenuDelegate {
    private let controller: AppController
    private weak var windowController: PanelWindowController?
    private let statusItem: NSStatusItem
    private let menu = NSMenu()

    init(controller: AppController, windowController: PanelWindowController) {
        self.controller = controller
        self.windowController = windowController
        self.statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        super.init()
        if let button = statusItem.button {
            button.image = NSImage(systemSymbolName: "waveform", accessibilityDescription: "VoiceAec")
            button.image?.isTemplate = true
            if button.image == nil { button.title = "VA" }
            button.toolTip = "VoiceAec"
        }
        menu.delegate = self
        statusItem.menu = menu
        refresh()
    }

    func refresh() {
        let status = controller.engine.status()
        if let button = statusItem.button {
            button.image = NSImage(systemSymbolName: status.paused ? "waveform.slash" : "waveform",
                                   accessibilityDescription: "VoiceAec")
            button.image?.isTemplate = true
        }
        rebuild(status: status)
    }

    func menuWillOpen(_ menu: NSMenu) {
        refresh()
    }

    private func rebuild(status: EngineStatus) {
        menu.removeAllItems()

        let open = NSMenuItem(title: "打开面板", action: #selector(openPanel), keyEquivalent: "o")
        open.target = self
        menu.addItem(open)
        menu.addItem(.separator())

        let pause = NSMenuItem(title: status.paused ? "恢复处理与输出" : "暂停并静音",
                               action: #selector(togglePause), keyEquivalent: "")
        pause.target = self
        menu.addItem(pause)

        let modeItem = NSMenuItem(title: "处理模式", action: nil, keyEquivalent: "")
        let modeMenu = NSMenu(title: "处理模式")
        for mode in ModelCatalog.modes {
            let info = ModelCatalog.info(for: mode, lang: status.lang)
            let item = NSMenuItem(title: info.label, action: #selector(selectMode(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = mode
            item.state = status.mode == mode ? .on : .off
            item.isEnabled = info.available || mode == "off"
            modeMenu.addItem(item)
        }
        modeItem.submenu = modeMenu
        menu.addItem(modeItem)

        let outputItem = NSMenuItem(title: "输出设备", action: nil, keyEquivalent: "")
        let outputMenu = NSMenu(title: "输出设备")
        let outputs = controller.engine.outputDeviceNames
        for name in outputs {
            let item = NSMenuItem(title: name, action: #selector(selectOutput(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = name
            item.state = status.outName == name ? .on : .off
            outputMenu.addItem(item)
        }
        if outputs.isEmpty {
            let item = NSMenuItem(title: "没有可用输出设备", action: nil, keyEquivalent: "")
            item.isEnabled = false
            outputMenu.addItem(item)
        }
        if status.cableMissing {
            outputMenu.addItem(.separator())
            let install = NSMenuItem(title: "安装 BlackHole 虚拟声卡…", action: #selector(openBlackHole), keyEquivalent: "")
            install.target = self
            outputMenu.addItem(install)
        }
        outputItem.submenu = outputMenu
        menu.addItem(outputItem)

        menu.addItem(.separator())

        let recheck = NSMenuItem(title: "重新检测设备", action: #selector(recheckDevices), keyEquivalent: "")
        recheck.target = self
        menu.addItem(recheck)

        let screen = NSMenuItem(title: "屏幕录制权限…", action: #selector(openScreenPermission), keyEquivalent: "")
        screen.target = self
        menu.addItem(screen)

        let autostart = NSMenuItem(title: "开机自启", action: #selector(toggleAutostart), keyEquivalent: "")
        autostart.target = self
        autostart.state = Autostart.isEnabled ? .on : .off
        menu.addItem(autostart)

        menu.addItem(.separator())

        let reset = NSMenuItem(title: "恢复默认设置…", action: #selector(resetSettings), keyEquivalent: "")
        reset.target = self
        menu.addItem(reset)

        let logs = NSMenuItem(title: "打开日志文件夹", action: #selector(openLogs), keyEquivalent: "")
        logs.target = self
        menu.addItem(logs)

        let about = NSMenuItem(title: "关于 VoiceAec", action: #selector(showAbout), keyEquivalent: "")
        about.target = self
        menu.addItem(about)

        menu.addItem(.separator())
        let quit = NSMenuItem(title: "退出 VoiceAec", action: #selector(quit), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)
    }

    @objc private func openPanel() {
        windowController?.showWindow()
    }

    @objc private func togglePause() {
        controller.togglePaused()
        refresh()
    }

    @objc private func selectMode(_ sender: NSMenuItem) {
        guard let mode = sender.representedObject as? String else { return }
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let error = self?.controller.setMode(mode)
            DispatchQueue.main.async {
                self?.refresh()
                if let error { self?.alert("切换模式失败", error) }
            }
        }
    }

    @objc private func selectOutput(_ sender: NSMenuItem) {
        guard let name = sender.representedObject as? String else { return }
        if let error = controller.engine.setDevice(kind: .output, name: name) {
            alert("切换输出设备失败", error)
        }
        refresh()
    }

    @objc private func recheckDevices() {
        if let error = controller.recheck() { alert("重新检测", error) }
        refresh()
    }

    @objc private func openScreenPermission() {
        controller.openScreenRecordingSettings()
    }

    @objc private func toggleAutostart() {
        let enabled = !Autostart.isEnabled
        if let error = controller.setAutostart(enabled) { alert("设置开机自启", error) }
        refresh()
    }

    @objc private func openBlackHole() {
        controller.openBlackHoleDownload()
    }

    @objc private func resetSettings() {
        let alert = NSAlert()
        alert.messageText = "恢复默认设置？"
        alert.informativeText = "语言、模式、输出设备和暂停状态会恢复为默认值，然后 VoiceAec 会退出。"
        alert.alertStyle = .warning
        alert.addButton(withTitle: "恢复并退出")
        alert.addButton(withTitle: "取消")
        if alert.runModal() == .alertFirstButtonReturn {
            controller.resetSettings()
        }
    }

    @objc private func openLogs() {
        controller.openLogFolder()
    }

    @objc private func showAbout() {
        if let delegate = NSApp.delegate as? AppDelegate {
            delegate.showAbout()
        }
    }

    @objc private func quit() {
        NSApp.terminate(nil)
    }

    private func alert(_ title: String, _ message: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.alertStyle = .warning
        alert.addButton(withTitle: "好")
        alert.runModal()
    }
}
