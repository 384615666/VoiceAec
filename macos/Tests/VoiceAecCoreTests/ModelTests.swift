import XCTest
@testable import VoiceAecCore

final class ModelTests: XCTestCase {
    func testModeLabels() {
        XCTAssertEqual(ModelCatalog.label(for: "v1.2", lang: "zh"), "均衡模式")
        XCTAssertEqual(ModelCatalog.label(for: "v1.2", lang: "en"), "Balanced mode")
        XCTAssertTrue(ModelCatalog.isKnown("off"))
        XCTAssertFalse(ModelCatalog.isKnown("v9"))
    }

    func testConfigDecodesOldFiles() throws {
        let json = #"{"micUID":"mic","outUID":"out","mode":"v1.3","lang":"en","paused":true,"refDelayMs":0,"autoStart":false}"#
        let config = try JSONDecoder().decode(AppConfig.self, from: Data(json.utf8))
        XCTAssertEqual(config.mode, "v1.3")
        XCTAssertEqual(config.lang, "en")
        XCTAssertTrue(config.paused)
        XCTAssertFalse(config.outUserChosen)
    }

    func testInferenceIfModelAvailable() throws {
        guard let path = ModelCatalog.path(for: "v1.2") else {
            throw XCTSkip("v1.2 GGUF not present; set VOICEAEC_MODELS")
        }
        let context = try LocalVQEContext(modelPath: path, threads: 2)
        XCTAssertEqual(context.sampleRate, 16000)
        XCTAssertEqual(context.hop, 256)
        var mic = [Float](repeating: 0, count: context.hop)
        var ref = [Float](repeating: 0, count: context.hop)
        var out = [Float](repeating: 0, count: context.hop)
        for i in 0..<context.hop {
            mic[i] = 0.01 * sinf(2 * .pi * 440 * Float(i) / 16000)
        }
        let rc = mic.withUnsafeBufferPointer { m in
            ref.withUnsafeBufferPointer { r in
                out.withUnsafeMutableBufferPointer { o in
                    context.process(mic: m.baseAddress!, reference: r.baseAddress!,
                                    output: o.baseAddress!, count: context.hop)
                }
            }
        }
        XCTAssertEqual(rc, 0)
        XCTAssertTrue(out.allSatisfy { $0.isFinite })
    }
}
