import Foundation
import CoreAudio
import AudioToolbox
import AVFoundation

/// Real-time AUHAL output at 16 kHz. The HAL performs the conversion to the
/// device's native rate (verified on macOS 15 for 44.1/48 kHz devices).
final class AUHALOutput {
    private var unit: AudioUnit?
    private let deviceID: AudioDeviceID
    private let channels: Int
    private let ring: FloatRing
    private let onUnderrun: () -> Void
    private let maxFrames = 4096
    private let scratch: UnsafeMutablePointer<Float>
    private var started = false

    private let renderCallback: AURenderCallback = { refCon, _, _, _, frameCount, ioData in
        let output = Unmanaged<AUHALOutput>.fromOpaque(refCon).takeUnretainedValue()
        return output.render(frameCount: frameCount, ioData: ioData)
    }

    init(deviceID: AudioDeviceID, channels: Int, ring: FloatRing, onUnderrun: @escaping () -> Void) {
        self.deviceID = deviceID
        self.channels = max(1, min(2, channels))
        self.ring = ring
        self.onUnderrun = onUnderrun
        self.scratch = UnsafeMutablePointer<Float>.allocate(capacity: 8192)
        self.scratch.initialize(repeating: 0, count: 8192)
    }

    deinit {
        stop()
        scratch.deallocate()
    }

    func start() throws {
        guard !started else { return }
        var desc = AudioComponentDescription(componentType: kAudioUnitType_Output,
                                             componentSubType: kAudioUnitSubType_HALOutput,
                                             componentManufacturer: kAudioUnitManufacturer_Apple,
                                             componentFlags: 0, componentFlagsMask: 0)
        guard let component = AudioComponentFindNext(nil, &desc) else {
            throw AudioError("找不到 HAL 输出组件")
        }
        var instance: AudioUnit?
        try check(AudioComponentInstanceNew(component, &instance), "创建输出单元")
        guard let unit = instance else { throw AudioError("输出单元为空") }
        self.unit = unit

        do {
            var enable: UInt32 = 1
            try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO,
                                           kAudioUnitScope_Output, 0, &enable, 4), "启用输出")
            var disable: UInt32 = 0
            try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO,
                                           kAudioUnitScope_Input, 1, &disable, 4), "禁用输入")
            var device = deviceID
            try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice,
                                           kAudioUnitScope_Global, 0, &device,
                                           UInt32(MemoryLayout<AudioDeviceID>.size)), "设置输出设备")
            var format = AudioStreamBasicDescription(
                mSampleRate: 16000,
                mFormatID: kAudioFormatLinearPCM,
                mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked | kAudioFormatFlagIsNonInterleaved,
                mBytesPerPacket: 4, mFramesPerPacket: 1, mBytesPerFrame: 4,
                mChannelsPerFrame: UInt32(channels), mBitsPerChannel: 32, mReserved: 0)
            try check(AudioUnitSetProperty(unit, kAudioUnitProperty_StreamFormat,
                                           kAudioUnitScope_Input, 0, &format,
                                           UInt32(MemoryLayout<AudioStreamBasicDescription>.size)), "设置输出格式")
            var maxFrames = UInt32(self.maxFrames)
            try check(AudioUnitSetProperty(unit, kAudioUnitProperty_MaximumFramesPerSlice,
                                           kAudioUnitScope_Global, 0, &maxFrames, 4), "设置输出帧数")
            var callback = AURenderCallbackStruct(inputProc: renderCallback,
                                                  inputProcRefCon: Unmanaged.passUnretained(self).toOpaque())
            try check(AudioUnitSetProperty(unit, kAudioUnitProperty_SetRenderCallback,
                                           kAudioUnitScope_Input, 0, &callback,
                                           UInt32(MemoryLayout<AURenderCallbackStruct>.size)), "设置输出回调")
            try check(AudioUnitInitialize(unit), "初始化输出单元")
            try check(AudioOutputUnitStart(unit), "启动输出单元")
            started = true
            Log.info("output started device=\(deviceID) channels=\(channels) rate=16000")
        } catch {
            AudioOutputUnitStop(unit)
            AudioUnitUninitialize(unit)
            AudioComponentInstanceDispose(unit)
            self.unit = nil
            throw error
        }
    }

    func stop() {
        guard let unit else { return }
        if started { AudioOutputUnitStop(unit) }
        AudioUnitUninitialize(unit)
        AudioComponentInstanceDispose(unit)
        self.unit = nil
        started = false
    }

    private func render(frameCount: UInt32, ioData: UnsafeMutablePointer<AudioBufferList>?) -> OSStatus {
        guard let ioData else { return noErr }
        let frames = Int(frameCount)
        let got = ring.read(into: scratch, count: frames)
        if got < frames {
            scratch.advanced(by: got).initialize(repeating: 0, count: frames - got)
            onUnderrun()
        }
        let list = UnsafeMutableAudioBufferListPointer(ioData)
        for buffer in list {
            guard let data = buffer.mData?.assumingMemoryBound(to: Float.self) else { continue }
            let capacity = Int(buffer.mDataByteSize) / MemoryLayout<Float>.size
            let n = min(frames, capacity)
            data.update(from: scratch, count: n)
            if capacity > n { data.advanced(by: n).initialize(repeating: 0, count: capacity - n) }
        }
        return noErr
    }
}

/// AUHAL input at the device's native rate, converted to 16 kHz mono.
final class AUHALCapture {
    private var unit: AudioUnit?
    private let deviceID: AudioDeviceID
    private let nativeRate: Double
    private let channels: Int
    private let ring: FloatRing
    private let maxFrames = 4096
    private var abl: UnsafeMutableAudioBufferListPointer
    private let mono: UnsafeMutablePointer<Float>
    private var converter: AVAudioConverter?
    private let inPCM: AVAudioPCMBuffer
    private let outPCM: AVAudioPCMBuffer
    private var started = false
    private var errorCount = 0

    private let inputCallback: AURenderCallback = { refCon, flags, ts, bus, frameCount, _ in
        let capture = Unmanaged<AUHALCapture>.fromOpaque(refCon).takeUnretainedValue()
        return capture.handleInput(flags: flags, timestamp: ts, bus: bus, frames: frameCount)
    }

    init(deviceID: AudioDeviceID, nativeRate: Double, channels: Int, ring: FloatRing) throws {
        self.deviceID = deviceID
        self.nativeRate = nativeRate > 0 ? nativeRate : 48000
        self.channels = max(1, min(2, channels))
        self.ring = ring
        self.mono = UnsafeMutablePointer<Float>.allocate(capacity: maxFrames)
        self.mono.initialize(repeating: 0, count: maxFrames)
        self.abl = AudioBufferList.allocate(maximumBuffers: max(1, min(2, channels)))
        for i in 0..<abl.count {
            let data = UnsafeMutableRawPointer.allocate(byteCount: maxFrames * 4, alignment: 16)
            data.initializeMemory(as: UInt8.self, repeating: 0, count: maxFrames * 4)
            abl[i] = AudioBuffer(mNumberChannels: 1, mDataByteSize: 0, mData: data)
        }
        let inFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: self.nativeRate,
                                     channels: 1, interleaved: false)!
        let outFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16000,
                                      channels: 1, interleaved: false)!
        guard let input = AVAudioPCMBuffer(pcmFormat: inFormat, frameCapacity: UInt32(maxFrames)),
              let output = AVAudioPCMBuffer(pcmFormat: outFormat,
                                            frameCapacity: AVAudioFrameCount(ceil(Double(maxFrames) * 16000.0 / self.nativeRate)) + 64),
              let conv = AVAudioConverter(from: inFormat, to: outFormat) else {
            throw AudioError("无法创建麦克风重采样器")
        }
        conv.sampleRateConverterQuality = AVAudioQuality.medium.rawValue
        conv.sampleRateConverterAlgorithm = AVSampleRateConverterAlgorithm_Normal
        self.inPCM = input
        self.outPCM = output
        self.converter = conv
    }

    deinit {
        stop()
        for i in 0..<abl.count { abl[i].mData?.deallocate() }
        free(abl.unsafeMutablePointer)
        mono.deallocate()
    }

    func start() throws {
        guard !started else { return }
        var desc = AudioComponentDescription(componentType: kAudioUnitType_Output,
                                             componentSubType: kAudioUnitSubType_HALOutput,
                                             componentManufacturer: kAudioUnitManufacturer_Apple,
                                             componentFlags: 0, componentFlagsMask: 0)
        guard let component = AudioComponentFindNext(nil, &desc) else { throw AudioError("找不到 HAL 输入组件") }
        var instance: AudioUnit?
        try check(AudioComponentInstanceNew(component, &instance), "创建输入单元")
        guard let unit = instance else { throw AudioError("输入单元为空") }
        self.unit = unit
        do {
            var enable: UInt32 = 1
            try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO,
                                           kAudioUnitScope_Input, 1, &enable, 4), "启用输入")
            var disable: UInt32 = 0
            try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO,
                                           kAudioUnitScope_Output, 0, &disable, 4), "禁用输出")
            var device = deviceID
            try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice,
                                           kAudioUnitScope_Global, 0, &device,
                                           UInt32(MemoryLayout<AudioDeviceID>.size)), "设置输入设备")
            var format = AudioStreamBasicDescription(
                mSampleRate: nativeRate,
                mFormatID: kAudioFormatLinearPCM,
                mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked | kAudioFormatFlagIsNonInterleaved,
                mBytesPerPacket: 4, mFramesPerPacket: 1, mBytesPerFrame: 4,
                mChannelsPerFrame: UInt32(channels), mBitsPerChannel: 32, mReserved: 0)
            try check(AudioUnitSetProperty(unit, kAudioUnitProperty_StreamFormat,
                                           kAudioUnitScope_Output, 1, &format,
                                           UInt32(MemoryLayout<AudioStreamBasicDescription>.size)), "设置输入格式")
            var maxFrames = UInt32(self.maxFrames)
            try check(AudioUnitSetProperty(unit, kAudioUnitProperty_MaximumFramesPerSlice,
                                           kAudioUnitScope_Global, 0, &maxFrames, 4), "设置输入帧数")
            var callback = AURenderCallbackStruct(inputProc: inputCallback,
                                                  inputProcRefCon: Unmanaged.passUnretained(self).toOpaque())
            try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_SetInputCallback,
                                           kAudioUnitScope_Global, 0, &callback,
                                           UInt32(MemoryLayout<AURenderCallbackStruct>.size)), "设置输入回调")
            try check(AudioUnitInitialize(unit), "初始化输入单元")
            try check(AudioOutputUnitStart(unit), "启动输入单元")
            started = true
            Log.info("mic started device=\(deviceID) nativeRate=\(nativeRate) channels=\(channels)")
        } catch {
            AudioOutputUnitStop(unit)
            AudioUnitUninitialize(unit)
            AudioComponentInstanceDispose(unit)
            self.unit = nil
            throw error
        }
    }

    func stop() {
        guard let unit else { return }
        if started { AudioOutputUnitStop(unit) }
        AudioUnitUninitialize(unit)
        AudioComponentInstanceDispose(unit)
        self.unit = nil
        started = false
    }

    private func handleInput(flags: UnsafeMutablePointer<AudioUnitRenderActionFlags>,
                             timestamp: UnsafePointer<AudioTimeStamp>,
                             bus: UInt32, frames: UInt32) -> OSStatus {
        guard let unit else { return noErr }
        let n = Int(frames)
        guard n > 0, n <= maxFrames else { return noErr }
        for i in 0..<abl.count { abl[i].mDataByteSize = UInt32(n * 4) }
        let status = AudioUnitRender(unit, flags, timestamp, bus, frames, abl.unsafeMutablePointer)
        if status != noErr {
            errorCount += 1
            if errorCount == 1 || errorCount % 200 == 0 {
                Log.warn("AudioUnitRender input failed: \(status) (count=\(errorCount))")
            }
            return noErr
        }
        // Downmix to mono.
        let channelCount = max(1, abl.count)
        let scale = 1.0 / Float(channelCount)
        for i in 0..<n { mono[i] = 0 }
        for ch in 0..<channelCount {
            guard let data = abl[ch].mData?.assumingMemoryBound(to: Float.self) else { continue }
            for i in 0..<n { mono[i] += data[i] * scale }
        }
        pushResampled(frames: n)
        return noErr
    }

    private func pushResampled(frames: Int) {
        guard let converter else { return }
        inPCM.frameLength = AVAudioFrameCount(frames)
        guard let inputChannel = inPCM.floatChannelData?[0] else { return }
        inputChannel.update(from: mono, count: frames)
        outPCM.frameLength = 0
        var consumed = false
        var error: NSError?
        let status = converter.convert(to: outPCM, error: &error) { _, outStatus in
            if consumed {
                outStatus.pointee = .noDataNow
                return nil
            }
            consumed = true
            outStatus.pointee = .haveData
            return self.inPCM
        }
        if status == .error {
            if errorCount == 0 || errorCount % 200 == 0 {
                Log.error("mic resample failed: \(error?.localizedDescription ?? "unknown")")
            }
            errorCount += 1
            return
        }
        guard outPCM.frameLength > 0, let output = outPCM.floatChannelData?[0] else { return }
        ring.write(output, count: Int(outPCM.frameLength))
    }
}

struct AudioError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

@discardableResult
func check(_ status: OSStatus, _ what: String) throws -> OSStatus {
    guard status == noErr else {
        throw AudioError("\(what)失败：OSStatus \(status)")
    }
    return status
}
