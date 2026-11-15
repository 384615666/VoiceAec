import XCTest
@testable import VoiceAecCore

final class RingBufferTests: XCTestCase {
    func testWriteRead() {
        let ring = FloatRing(capacity: 8)
        var input: [Float] = [1, 2, 3, 4, 5]
        input.withUnsafeBufferPointer { XCTAssertEqual(ring.write($0.baseAddress!, count: 5), 0) }
        XCTAssertEqual(ring.available, 5)
        var output = [Float](repeating: 0, count: 3)
        output.withUnsafeMutableBufferPointer { XCTAssertEqual(ring.read(into: $0.baseAddress!, count: 3), 3) }
        XCTAssertEqual(output, [1, 2, 3])
        XCTAssertEqual(ring.available, 2)
    }

    func testOverflowDropsOldest() {
        let ring = FloatRing(capacity: 4)
        var input: [Float] = [1, 2, 3, 4, 5, 6]
        input.withUnsafeBufferPointer { XCTAssertEqual(ring.write($0.baseAddress!, count: 6), 2) }
        var output = [Float](repeating: 0, count: 4)
        output.withUnsafeMutableBufferPointer { XCTAssertEqual(ring.read(into: $0.baseAddress!, count: 4), 4) }
        XCTAssertEqual(output, [3, 4, 5, 6])
    }

    func testDrop() {
        let ring = FloatRing(capacity: 8)
        var input: [Float] = [1, 2, 3, 4]
        input.withUnsafeBufferPointer { ring.write($0.baseAddress!, count: 4) }
        XCTAssertEqual(ring.drop(2), 2)
        var output = [Float](repeating: 0, count: 2)
        output.withUnsafeMutableBufferPointer { XCTAssertEqual(ring.read(into: $0.baseAddress!, count: 2), 2) }
        XCTAssertEqual(output, [3, 4])
    }
}
