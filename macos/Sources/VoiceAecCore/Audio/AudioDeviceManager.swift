import Foundation
import CoreAudio
import AudioToolbox

public struct AudioDeviceInfo: Equatable, Identifiable {
    public let id: AudioDeviceID
    public let uid: String
    public let name: String
    public let inputs: Int
    public let outputs: Int
    public let sampleRate: Double
    public let isBlackHole: Bool
    public let isVirtual: Bool

    public var identifiableID: AudioDeviceID { id }
}

/// CoreAudio device discovery and hot-plug notifications.
public final class AudioDeviceManager {
    private let system = AudioObjectID(kAudioObjectSystemObject)
    private let lock = NSLock()
    private var cached: [AudioDeviceInfo] = []
    private var cachedDefaultInput: String?
    private var cachedDefaultOutput: String?
    private var listeners: [(AudioObjectPropertyAddress, AudioObjectPropertyListenerBlock)] = []
    private var debounce: DispatchWorkItem?
    private var monitoring = false

    public var onChange: (() -> Void)?

    public init() {
        refresh()
    }

    public func startMonitoring() {
        guard !monitoring else { return }
        monitoring = true
        let queue = DispatchQueue.main
        for selector in [kAudioHardwarePropertyDevices,
                         kAudioHardwarePropertyDefaultInputDevice,
                         kAudioHardwarePropertyDefaultOutputDevice] {
            var addr = AudioObjectPropertyAddress(mSelector: selector,
                                                  mScope: kAudioObjectPropertyScopeGlobal,
                                                  mElement: kAudioObjectPropertyElementMain)
            let block: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
                self?.scheduleRefresh()
            }
            let status = AudioObjectAddPropertyListenerBlock(system, &addr, queue, block)
            if status == noErr {
                listeners.append((addr, block))
            } else {
                Log.warn("AudioObjectAddPropertyListenerBlock \(selector) failed: \(status)")
            }
        }
    }

    public func stopMonitoring() {
        guard monitoring else { return }
        monitoring = false
        for (var addr, block) in listeners {
            AudioObjectRemovePropertyListenerBlock(system, &addr, DispatchQueue.main, block)
        }
        listeners.removeAll()
        debounce?.cancel()
        debounce = nil
    }

    private func scheduleRefresh() {
        debounce?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.refresh()
            self.onChange?()
        }
        debounce = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25, execute: work)
    }

    public func refresh() {
        var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices,
                                              mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(system, &addr, 0, nil, &size) == noErr, size > 0 else {
            lock.lock(); cached = []; lock.unlock(); return
        }
        let count = Int(size) / MemoryLayout<AudioDeviceID>.size
        var ids = [AudioDeviceID](repeating: 0, count: count)
        guard AudioObjectGetPropertyData(system, &addr, 0, nil, &size, &ids) == noErr else { return }

        var found: [AudioDeviceInfo] = []
        var nameCounts: [String: Int] = [:]
        for id in ids where id != 0 {
            guard isAlive(id) else { continue }
            let rawName = getString(id, kAudioObjectPropertyName) ?? "Unknown"
            let uid = getString(id, kAudioDevicePropertyDeviceUID) ?? "\(id)"
            let ins = channelCount(id, scope: kAudioObjectPropertyScopeInput)
            let outs = channelCount(id, scope: kAudioObjectPropertyScopeOutput)
            guard ins > 0 || outs > 0 else { continue }
            let rate = getDouble(id, kAudioDevicePropertyNominalSampleRate) ?? 0
            let lower = rawName.lowercased()
            let uidLower = uid.lowercased()
            let blackHole = lower.contains("blackhole") || uidLower.contains("blackhole")
            let virtual = blackHole || lower.contains("soundflower") || lower.contains("loopback")
                || lower.contains("virtual audio") || lower.contains("vb-cable")
                || lower.contains("existential audio")
            let n = (nameCounts[rawName] ?? 0) + 1
            nameCounts[rawName] = n
            let name = n == 1 ? rawName : "\(rawName) (\(n))"
            found.append(AudioDeviceInfo(id: id, uid: uid, name: name, inputs: ins, outputs: outs,
                                         sampleRate: rate, isBlackHole: blackHole, isVirtual: virtual))
        }
        found.sort { $0.name.localizedStandardCompare($1.name) == .orderedAscending }

        var defIn: String?
        var defOut: String?
        if let id = defaultDevice(kAudioHardwarePropertyDefaultInputDevice) {
            defIn = found.first(where: { $0.id == id })?.uid
        }
        if let id = defaultDevice(kAudioHardwarePropertyDefaultOutputDevice) {
            defOut = found.first(where: { $0.id == id })?.uid
        }

        lock.lock()
        cached = found
        cachedDefaultInput = defIn
        cachedDefaultOutput = defOut
        lock.unlock()
    }

    public var devices: [AudioDeviceInfo] {
        lock.lock(); defer { lock.unlock() }
        return cached
    }

    public var inputs: [AudioDeviceInfo] {
        lock.lock(); defer { lock.unlock() }
        return cached.filter { $0.inputs > 0 }
    }

    public var outputs: [AudioDeviceInfo] {
        lock.lock(); defer { lock.unlock() }
        return cached.filter { $0.outputs > 0 }
    }

    public var defaultInputUID: String? {
        lock.lock(); defer { lock.unlock() }
        return cachedDefaultInput
    }

    public var defaultOutputUID: String? {
        lock.lock(); defer { lock.unlock() }
        return cachedDefaultOutput
    }

    public var blackHoleOutput: AudioDeviceInfo? {
        lock.lock(); defer { lock.unlock() }
        return cached.first { $0.isBlackHole && $0.outputs > 0 }
    }

    public func device(uid: String?) -> AudioDeviceInfo? {
        guard let uid else { return nil }
        lock.lock(); defer { lock.unlock() }
        return cached.first { $0.uid == uid }
    }

    public func device(name: String, input: Bool) -> AudioDeviceInfo? {
        lock.lock(); defer { lock.unlock() }
        return cached.first { $0.name == name && (input ? $0.inputs > 0 : $0.outputs > 0) }
    }

    // MARK: - CoreAudio helpers

    private func isAlive(_ id: AudioDeviceID) -> Bool {
        (getUInt32(id, kAudioDevicePropertyDeviceIsAlive) ?? 1) != 0
    }

    private func getUInt32(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector,
                           scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> UInt32? {
        var addr = AudioObjectPropertyAddress(mSelector: selector, mScope: scope,
                                              mElement: kAudioObjectPropertyElementMain)
        var value: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        guard AudioObjectGetPropertyData(id, &addr, 0, nil, &size, &value) == noErr else { return nil }
        return value
    }

    private func getDouble(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector) -> Double? {
        var addr = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        var value: Float64 = 0
        var size = UInt32(MemoryLayout<Float64>.size)
        guard AudioObjectGetPropertyData(id, &addr, 0, nil, &size, &value) == noErr else { return nil }
        return value
    }

    private func getString(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector) -> String? {
        var addr = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        let status = withUnsafeMutablePointer(to: &value) { ptr -> OSStatus in
            AudioObjectGetPropertyData(id, &addr, 0, nil, &size, ptr)
        }
        guard status == noErr, let value else { return nil }
        return value.takeRetainedValue() as String
    }

    private func defaultDevice(_ selector: AudioObjectPropertySelector) -> AudioDeviceID? {
        var addr = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        var value = AudioDeviceID(0)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(system, &addr, 0, nil, &size, &value) == noErr, value != 0 else { return nil }
        return value
    }

    private func channelCount(_ id: AudioDeviceID, scope: AudioObjectPropertyScope) -> Int {
        var addr = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreamConfiguration,
                                              mScope: scope, mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(id, &addr, 0, nil, &size) == noErr, size > 0 else { return 0 }
        let raw = UnsafeMutableRawPointer.allocate(byteCount: Int(size),
                                                   alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { raw.deallocate() }
        guard AudioObjectGetPropertyData(id, &addr, 0, nil, &size, raw) == noErr else { return 0 }
        let list = UnsafeMutableAudioBufferListPointer(raw.assumingMemoryBound(to: AudioBufferList.self))
        var channels = 0
        for buffer in list { channels += Int(buffer.mNumberChannels) }
        return channels
    }

    /// Physical input channel count used for the AUHAL client format.
    public func inputChannelCount(_ id: AudioDeviceID) -> Int {
        channelCount(id, scope: kAudioObjectPropertyScopeInput)
    }

    public func outputChannelCount(_ id: AudioDeviceID) -> Int {
        channelCount(id, scope: kAudioObjectPropertyScopeOutput)
    }

    public func nominalSampleRate(_ id: AudioDeviceID) -> Double {
        getDouble(id, kAudioDevicePropertyNominalSampleRate) ?? 48000
    }
}
