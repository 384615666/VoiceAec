import Foundation
import AVFoundation
import CoreMedia
import CoreGraphics
import ScreenCaptureKit

/// Captures system audio with ScreenCaptureKit. Requires the Screen Recording
/// permission; the first `start()` triggers the system prompt.
@available(macOS 13.0, *)
final class SystemAudioCapture: NSObject, SCStreamOutput, SCStreamDelegate {
    private let ring: FloatRing
    private let queue = DispatchQueue(label: "voiceaec.sck.audio", qos: .userInteractive)
    private var stream: SCStream?
    private var converter: AVAudioConverter?
    private var inputFormat: AVAudioFormat?
    private var running = false
    private var starting = false
    private(set) var lastError: String?
    var onError: ((String) -> Void)?

    init(ring: FloatRing) {
        self.ring = ring
    }

    var isRunning: Bool { running }
    var errorDescription: String? { lastError }

    func start() async throws {
        if running || starting { return }
        starting = true
        defer { starting = false }
        lastError = nil

        if !CGPreflightScreenCaptureAccess() {
            Log.info("requesting screen recording permission")
            _ = CGRequestScreenCaptureAccess()
        }

        let content: SCShareableContent
        do {
            content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        } catch {
            lastError = "无法读取屏幕内容（屏幕录制权限未开启）：\(error.localizedDescription)"
            Log.error(lastError!)
            throw AudioError(lastError!)
        }
        guard let display = content.displays.first else {
            lastError = "没有找到显示器，无法回采系统声音。"
            Log.error(lastError!)
            throw AudioError(lastError!)
        }

        let filter = SCContentFilter(display: display, excludingWindows: [])
        let config = SCStreamConfiguration()
        config.capturesAudio = true
        config.sampleRate = 48000
        config.channelCount = 2
        config.excludesCurrentProcessAudio = true
        config.width = 16
        config.height = 16
        config.minimumFrameInterval = CMTime(value: 1, timescale: 1)
        config.showsCursor = false
        config.queueDepth = 3

        let stream = SCStream(filter: filter, configuration: config, delegate: self)
        do {
            try stream.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue)
            try await stream.startCapture()
        } catch {
            lastError = "系统声音回采启动失败：\(error.localizedDescription)"
            Log.error(lastError!)
            throw AudioError(lastError!)
        }
        self.stream = stream
        running = true
        Log.info("system audio capture started (ScreenCaptureKit)")
    }

    func stop() {
        guard let stream else { return }
        running = false
        self.stream = nil
        Task {
            try? await stream.stopCapture()
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        running = false
        lastError = "系统声音回采已停止：\(error.localizedDescription)"
        Log.error(lastError!)
        onError?(lastError!)
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer,
                of outputType: SCStreamOutputType) {
        guard outputType == .audio, sampleBuffer.isValid, running else { return }
        guard let description = CMSampleBufferGetFormatDescription(sampleBuffer),
              let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(description) else { return }
        let frames = CMSampleBufferGetNumSamples(sampleBuffer)
        guard frames > 0 else { return }

        if inputFormat == nil || inputFormat!.streamDescription.pointee.mSampleRate != asbd.pointee.mSampleRate
            || inputFormat!.streamDescription.pointee.mChannelsPerFrame != asbd.pointee.mChannelsPerFrame {
            guard let format = AVAudioFormat(streamDescription: asbd),
                  let output = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16000,
                                             channels: 1, interleaved: false),
                  let conv = AVAudioConverter(from: format, to: output) else {
                lastError = "无法创建系统声音重采样器"
                return
            }
            conv.sampleRateConverterQuality = AVAudioQuality.medium.rawValue
            inputFormat = format
            converter = conv
            Log.info("SCK audio format: \(format)")
        }
        guard let converter, let inputFormat else { return }

        var needed = 0
        CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer, bufferListSizeNeededOut: &needed, bufferListOut: nil, bufferListSize: 0,
            blockBufferAllocator: nil, blockBufferMemoryAllocator: nil, flags: 0, blockBufferOut: nil)
        guard needed > 0 else { return }
        let raw = UnsafeMutableRawPointer.allocate(byteCount: needed, alignment: 16)
        defer { raw.deallocate() }
        var block: CMBlockBuffer?
        let status = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer, bufferListSizeNeededOut: nil,
            bufferListOut: raw.assumingMemoryBound(to: AudioBufferList.self), bufferListSize: needed,
            blockBufferAllocator: nil, blockBufferMemoryAllocator: nil, flags: 0, blockBufferOut: &block)
        guard status == noErr else { return }
        guard let pcm = AVAudioPCMBuffer(pcmFormat: inputFormat,
                                         bufferListNoCopy: raw.assumingMemoryBound(to: AudioBufferList.self),
                                         deallocator: nil) else { return }
        pcm.frameLength = AVAudioFrameCount(frames)

        let outCapacity = AVAudioFrameCount(Double(frames) * 16000.0 / inputFormat.sampleRate) + 64
        guard let output = AVAudioPCMBuffer(pcmFormat: converter.outputFormat, frameCapacity: outCapacity) else { return }
        var consumed = false
        var error: NSError?
        let conversion = converter.convert(to: output, error: &error) { _, outStatus in
            if consumed {
                outStatus.pointee = .noDataNow
                return nil
            }
            consumed = true
            outStatus.pointee = .haveData
            return pcm
        }
        if conversion == .error {
            lastError = "系统声音重采样失败：\(error?.localizedDescription ?? "unknown")"
            return
        }
        guard output.frameLength > 0, let samples = output.floatChannelData?[0] else { return }
        ring.write(samples, count: Int(output.frameLength))
        _ = block
    }
}
