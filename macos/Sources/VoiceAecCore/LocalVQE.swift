import Foundation
import CLocalVQE

/// Swift wrapper around the LocalVQE C API. One instance owns one model.
public final class LocalVQEContext {
    private var handle: UInt
    public let sampleRate: Int
    public let hop: Int

    public init(modelPath: String, threads: Int32) throws {
        let opts = localvqe_options_new()
        guard opts != 0 else {
            throw NSError(domain: "VoiceAec.LocalVQE", code: 1,
                          userInfo: [NSLocalizedDescriptionKey: "无法初始化本地推理引擎。"])
        }
        defer { localvqe_options_free(opts) }
        guard localvqe_options_set_model_path(opts, modelPath) == 0 else {
            throw NSError(domain: "VoiceAec.LocalVQE", code: 2,
                          userInfo: [NSLocalizedDescriptionKey: "无效的模型路径：\(modelPath)"])
        }
        guard localvqe_options_set_threads(opts, threads) == 0 else {
            throw NSError(domain: "VoiceAec.LocalVQE", code: 3,
                          userInfo: [NSLocalizedDescriptionKey: "无效的线程数：\(threads)"])
        }
        let created = localvqe_new_with_options(opts)
        guard created != 0 else {
            throw NSError(domain: "VoiceAec.LocalVQE", code: 4,
                          userInfo: [NSLocalizedDescriptionKey: "无法加载模型：\(modelPath)"])
        }
        handle = created
        sampleRate = Int(localvqe_sample_rate(created))
        hop = Int(localvqe_hop_length(created))
        Log.info("LocalVQE loaded \(URL(fileURLWithPath: modelPath).lastPathComponent) sr=\(sampleRate) hop=\(hop) threads=\(threads)")
    }

    deinit {
        let old = handle
        handle = 0
        if old != 0 { localvqe_free(old) }
    }

    /// Returns 0 on success. Non-zero means the caller should fall back to the
    /// microphone signal for this hop.
    public func process(mic: UnsafePointer<Float>, reference: UnsafePointer<Float>,
                        output: UnsafeMutablePointer<Float>, count: Int) -> Int32 {
        guard handle != 0 else { return -1 }
        let rc = localvqe_process_frame_f32(handle, mic, reference, Int32(count), output)
        if rc != 0 {
            if let err = localvqe_last_error(handle) {
                Log.error("LocalVQE process failed rc=\(rc): \(String(cString: err))")
            }
        }
        return rc
    }

    public func reset() {
        guard handle != 0 else { return }
        localvqe_reset(handle)
    }

    public func setNoiseGate(enabled: Bool, thresholdDbfs: Float) {
        guard handle != 0 else { return }
        _ = localvqe_set_noise_gate(handle, enabled ? 1 : 0, thresholdDbfs)
    }

    public var lastError: String? {
        guard handle != 0, let err = localvqe_last_error(handle) else { return nil }
        return String(cString: err)
    }
}
