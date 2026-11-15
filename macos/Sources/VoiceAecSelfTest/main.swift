import Foundation
import VoiceAecCore

var failures = 0
func check(_ condition: @autoclosure () -> Bool, _ name: String) {
    if condition() {
        print("PASS  \(name)")
    } else {
        print("FAIL  \(name)")
        failures += 1
    }
}

// Ring buffer
let ring = FloatRing(capacity: 8)
var input: [Float] = [1, 2, 3, 4, 5]
input.withUnsafeBufferPointer { _ = ring.write($0.baseAddress!, count: 5) }
var output = [Float](repeating: 0, count: 3)
output.withUnsafeMutableBufferPointer { _ = ring.read(into: $0.baseAddress!, count: 3) }
check(output == [1, 2, 3], "ring read")
check(ring.available == 2, "ring available")

var overflow: [Float] = [1, 2, 3, 4, 5, 6]
overflow.withUnsafeBufferPointer { _ = ring.write($0.baseAddress!, count: 6) }
var tail = [Float](repeating: 0, count: 8)
tail.withUnsafeMutableBufferPointer { _ = ring.read(into: $0.baseAddress!, count: 8) }
check(Array(tail.suffix(2)) == [5, 6], "ring overflow drops oldest")

// Config migration
let oldJSON = #"{"micUID":"mic","outUID":"out","mode":"v1.3","lang":"en"}"#
let old = try JSONDecoder().decode(AppConfig.self, from: Data(oldJSON.utf8))
check(old.mode == "v1.3" && old.lang == "en" && !old.outUserChosen, "config migration")

// Catalog
check(ModelCatalog.isKnown("v1.4"), "catalog mode")
check(ModelCatalog.label(for: "v1.2", lang: "en") == "Balanced mode", "catalog label")

// Inference
if let path = ModelCatalog.path(for: "v1.2") {
    do {
        let context = try LocalVQEContext(modelPath: path, threads: 2)
        check(context.sampleRate == 16000 && context.hop == 256, "model geometry")
        var mic = [Float](repeating: 0, count: context.hop)
        var ref = [Float](repeating: 0, count: context.hop)
        var out = [Float](repeating: 0, count: context.hop)
        for i in 0..<context.hop { mic[i] = 0.01 * sinf(2 * .pi * 440 * Float(i) / 16000) }
        let rc = mic.withUnsafeBufferPointer { m in
            ref.withUnsafeBufferPointer { r in
                out.withUnsafeMutableBufferPointer { o in
                    context.process(mic: m.baseAddress!, reference: r.baseAddress!,
                                    output: o.baseAddress!, count: context.hop)
                }
            }
        }
        check(rc == 0, "LocalVQE inference")
        check(out.allSatisfy { $0.isFinite }, "LocalVQE finite output")
    } catch {
        print("FAIL  LocalVQE: \(error.localizedDescription)")
        failures += 1
    }
} else {
    print("FAIL  model not found (set VOICEAEC_MODELS)")
    failures += 1
}

print(failures == 0 ? "SELF TEST PASSED" : "SELF TEST FAILED (\(failures))")
exit(failures == 0 ? 0 : 1)
