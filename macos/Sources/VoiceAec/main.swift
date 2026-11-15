import AppKit
import Darwin

// Single instance: keep the audio engine from being mixed twice.
let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
    .appendingPathComponent("VoiceAec", isDirectory: true)
try? FileManager.default.createDirectory(at: support, withIntermediateDirectories: true)
let lockPath = support.appendingPathComponent("instance.lock").path
let lockFD = open(lockPath, O_CREAT | O_RDWR, 0o644)
if lockFD < 0 { fputs("VoiceAec: cannot open instance lock: errno=\(errno)\n", stderr) }
if lockFD >= 0 && flock(lockFD, LOCK_EX | LOCK_NB) != 0 {
    if let running = NSRunningApplication.runningApplications(withBundleIdentifier: "xyz.lsjx.voiceaec").first {
        running.activate(options: [.activateIgnoringOtherApps])
    }
    fputs("VoiceAec is already running.\n", stderr)
    exit(0)
}

let application = NSApplication.shared
let appDelegate = AppDelegate()
application.delegate = appDelegate
application.setActivationPolicy(.accessory)
application.run()
