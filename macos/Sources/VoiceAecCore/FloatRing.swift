import Foundation

/// Bounded float ring buffer. When full, the oldest samples are dropped, which
/// keeps the audio path low-latency instead of growing an unbounded backlog.
public final class FloatRing {
    private var storage: [Float]
    private var readIndex = 0
    private var writeIndex = 0
    private var count = 0
    private let lock = NSLock()

    public init(capacity: Int) {
        storage = [Float](repeating: 0, count: max(1024, capacity))
    }

    public var capacity: Int { storage.count }

    public var available: Int {
        lock.lock(); defer { lock.unlock() }
        return count
    }

    public func clear() {
        lock.lock(); defer { lock.unlock() }
        readIndex = 0; writeIndex = 0; count = 0
    }

    /// Returns the number of dropped (oldest) samples.
    @discardableResult
    public func write(_ source: UnsafePointer<Float>, count incoming: Int) -> Int {
        guard incoming > 0 else { return 0 }
        lock.lock(); defer { lock.unlock() }
        let capacity = storage.count
        let overflow = max(0, count + incoming - capacity)
        if overflow > 0 {
            readIndex = (readIndex + overflow) % capacity
            count -= overflow
        }
        let toWrite = min(incoming, capacity)
        let sourceStart = incoming - toWrite
        let first = min(toWrite, capacity - writeIndex)
        storage.withUnsafeMutableBufferPointer { dst in
            dst.baseAddress!.advanced(by: writeIndex).update(from: source.advanced(by: sourceStart), count: first)
            if toWrite > first {
                dst.baseAddress!.update(from: source.advanced(by: sourceStart + first), count: toWrite - first)
            }
        }
        writeIndex = (writeIndex + toWrite) % capacity
        count += toWrite
        return overflow
    }

    /// Reads up to `count` samples; returns how many were written to `dest`.
    @discardableResult
    public func read(into dest: UnsafeMutablePointer<Float>, count requested: Int) -> Int {
        guard requested > 0 else { return 0 }
        lock.lock(); defer { lock.unlock() }
        let capacity = storage.count
        let actual = min(requested, count)
        let first = min(actual, capacity - readIndex)
        storage.withUnsafeBufferPointer { src in
            dest.update(from: src.baseAddress!.advanced(by: readIndex), count: first)
            if actual > first {
                dest.advanced(by: first).update(from: src.baseAddress!, count: actual - first)
            }
        }
        readIndex = (readIndex + actual) % capacity
        count -= actual
        return actual
    }

    /// Drops up to `n` samples from the front.
    @discardableResult
    public func drop(_ n: Int) -> Int {
        guard n > 0 else { return 0 }
        lock.lock(); defer { lock.unlock() }
        let dropped = min(n, count)
        readIndex = (readIndex + dropped) % storage.count
        count -= dropped
        return dropped
    }
}
