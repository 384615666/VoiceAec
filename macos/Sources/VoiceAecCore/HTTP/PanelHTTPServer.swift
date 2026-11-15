import Foundation
import Security
import Darwin

struct HTTPRequest {
    let method: String
    let path: String
    let headers: [String: String]
    let body: Data

    var text: String { String(decoding: body, as: UTF8.self) }
    var token: String? { headers["x-voiceaec-token"] }
}

struct HTTPResponse {
    var status: Int = 200
    var contentType: String = "application/json; charset=utf-8"
    var body: Data = Data()

    static func json(_ object: Any, status: Int = 200) -> HTTPResponse {
        let data = (try? JSONSerialization.data(withJSONObject: object, options: [])) ?? Data("{}".utf8)
        return HTTPResponse(status: status, contentType: "application/json; charset=utf-8", body: data)
    }

    static func text(_ string: String, contentType: String = "text/plain; charset=utf-8", status: Int = 200) -> HTTPResponse {
        HTTPResponse(status: status, contentType: contentType, body: Data(string.utf8))
    }
}

/// Minimal HTTP/1.1 server bound to 127.0.0.1. It only needs to serve the
/// single-page panel and a handful of JSON endpoints, so keep-alive is not used.
public final class PanelHTTPServer {
    public let token: String
    public private(set) var port: UInt16 = 0
    public var baseURL: URL { URL(string: "http://127.0.0.1:\(port)/")! }

    private let api: PanelAPI
    private let lock = NSLock()
    private var listenFD: Int32 = -1
    private var acceptThread: Thread?
    private var running = false
    private var htmlTemplate: String?
    private var css: String?
    private var js: String?

    init(api: PanelAPI) {
        self.api = api
        self.token = PanelHTTPServer.makeToken()
        loadAssets()
    }

    public func start() throws {
        lock.lock(); defer { lock.unlock() }
        guard !running else { return }
        for candidate in [8765, 8766, 8767, 8768, 8769, 8770, 8771, 8772, 0] {
            if let fd = bind(port: UInt16(candidate)) {
                listenFD = fd
                port = localPort(fd) ?? UInt16(candidate)
                running = true
                let thread = Thread { [weak self] in self?.acceptLoop() }
                thread.name = "voiceaec.http"
                thread.qualityOfService = .userInitiated
                acceptThread = thread
                thread.start()
                Log.info("panel listening on \(baseURL.absoluteString)")
                return
            }
        }
        throw NSError(domain: "VoiceAec.HTTP", code: 1,
                      userInfo: [NSLocalizedDescriptionKey: "无法绑定本地端口"])
    }

    public func stop() {
        lock.lock()
        let fd = listenFD
        listenFD = -1
        running = false
        lock.unlock()
        if fd >= 0 { close(fd) }
    }

    private static func makeToken() -> String {
        var bytes = [UInt8](repeating: 0, count: 24)
        let result = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        if result != 0 {
            return UUID().uuidString.replacingOccurrences(of: "-", with: "")
        }
        return bytes.map { String(format: "%02x", $0) }.joined()
    }

    private func loadAssets() {
        guard let dir = AppPaths.resourcesDir else {
            Log.error("resources not found (wwwroot/index.html)")
            return
        }
        htmlTemplate = try? String(contentsOf: dir.appendingPathComponent("wwwroot/index.html"), encoding: .utf8)
        css = try? String(contentsOf: dir.appendingPathComponent("macos.css"), encoding: .utf8)
        js = try? String(contentsOf: dir.appendingPathComponent("macos.js"), encoding: .utf8)
    }

    private func bind(port: UInt16) -> Int32? {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { return nil }
        var yes: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &yes, socklen_t(MemoryLayout<Int32>.size))
        var addr = sockaddr_in()
        addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = port.bigEndian
        addr.sin_addr.s_addr = inet_addr("127.0.0.1")
        let bound = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bound == 0, listen(fd, 16) == 0 else {
            close(fd)
            return nil
        }
        return fd
    }

    private func localPort(_ fd: Int32) -> UInt16? {
        var addr = sockaddr_in()
        var len = socklen_t(MemoryLayout<sockaddr_in>.size)
        let result = withUnsafeMutablePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                getsockname(fd, $0, &len)
            }
        }
        guard result == 0 else { return nil }
        return UInt16(bigEndian: addr.sin_port)
    }

    private func acceptLoop() {
        while true {
            lock.lock()
            let fd = listenFD
            let active = running
            lock.unlock()
            guard active, fd >= 0 else { break }
            let client = accept(fd, nil, nil)
            if client < 0 {
                if errno == EINTR { continue }
                break
            }
            Thread.detachNewThread { [weak self] in
                self?.handle(client: client)
            }
        }
    }

    private func handle(client: Int32) {
        defer {
            shutdown(client, SHUT_RDWR)
            close(client)
        }
        var noSigPipe: Int32 = 1
        setsockopt(client, SOL_SOCKET, SO_NOSIGPIPE, &noSigPipe, socklen_t(MemoryLayout<Int32>.size))
        var timeout = timeval(tv_sec: 5, tv_usec: 0)
        setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        guard let request = readRequest(client) else { return }
        let response = route(request)
        writeResponse(client, response)
    }

    private func readRequest(_ client: Int32) -> HTTPRequest? {
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 8192)
        var headerEnd: Range<Data.Index>?
        while data.count < 2 * 1024 * 1024 {
            let n = recv(client, &buffer, buffer.count, 0)
            if n <= 0 { break }
            data.append(buffer, count: n)
            if headerEnd == nil, let range = data.range(of: Data("\r\n\r\n".utf8)) {
                headerEnd = range
            }
            if let range = headerEnd {
                let headerText = String(decoding: data[..<range.lowerBound], as: UTF8.self)
                let contentLength = headerText
                    .components(separatedBy: "\r\n")
                    .first { $0.lowercased().hasPrefix("content-length:") }
                    .flatMap { Int($0.split(separator: ":", maxSplits: 1).last?.trimmingCharacters(in: .whitespaces) ?? "") } ?? 0
                if data.count >= range.upperBound + contentLength { break }
            }
        }
        guard let range = headerEnd else { return nil }
        let headerText = String(decoding: data[..<range.lowerBound], as: UTF8.self)
        let lines = headerText.components(separatedBy: "\r\n")
        guard let requestLine = lines.first else { return nil }
        let parts = requestLine.split(separator: " ")
        guard parts.count >= 2 else { return nil }
        let method = String(parts[0]).uppercased()
        let target = String(parts[1])
        let path = target.split(separator: "?", maxSplits: 1).first.map(String.init) ?? target
        var headers: [String: String] = [:]
        for line in lines.dropFirst() {
            guard let idx = line.firstIndex(of: ":") else { continue }
            let key = line[..<idx].trimmingCharacters(in: .whitespaces).lowercased()
            let value = line[line.index(after: idx)...].trimmingCharacters(in: .whitespaces)
            headers[key] = value
        }
        let contentLength = Int(headers["content-length"] ?? "0") ?? 0
        let bodyStart = range.upperBound
        let bodyEnd = min(data.count, bodyStart + max(0, contentLength))
        let body = data.subdata(in: bodyStart..<bodyEnd)
        return HTTPRequest(method: method, path: path, headers: headers, body: body)
    }

    private func route(_ request: HTTPRequest) -> HTTPResponse {
        if request.path.hasPrefix("/api/") {
            guard request.token == token else {
                return .json(["ok": false, "error": "invalid token"], status: 403)
            }
        }
        switch request.path {
        case "/", "/index.html":
            return panelHTML()
        case "/macos.css":
            return .text(css ?? "", contentType: "text/css; charset=utf-8")
        case "/macos.js":
            return .text(js ?? "", contentType: "application/javascript; charset=utf-8")
        case "/favicon.ico":
            return HTTPResponse(status: 404, contentType: "text/plain", body: Data())
        default:
            return api.handle(request)
        }
    }

    private func panelHTML() -> HTTPResponse {
        guard var html = htmlTemplate else {
            return .text("<meta charset='utf-8'><body style='font-family:sans-serif;padding:40px'>前端文件缺失：wwwroot/index.html</body>",
                         contentType: "text/html; charset=utf-8", status: 500)
        }
        let lang = api.currentLanguage
        html = html.replacingOccurrences(of: "__VOICEAEC_TOKEN__", with: token)
        html = html.replacingOccurrences(of: "__VOICEAEC_LANG__", with: lang)
        html = html.replacingOccurrences(of: "</head>",
                                          with: "<link rel=\"stylesheet\" href=\"/macos.css\">\n</head>")
        html = html.replacingOccurrences(of: "</body>",
                                          with: "<script src=\"/macos.js\"></script>\n</body>")
        return .text(html, contentType: "text/html; charset=utf-8")
    }

    private func writeResponse(_ client: Int32, _ response: HTTPResponse) {
        let reason: String
        switch response.status {
        case 200: reason = "OK"
        case 202: reason = "Accepted"
        case 400: reason = "Bad Request"
        case 403: reason = "Forbidden"
        case 404: reason = "Not Found"
        case 405: reason = "Method Not Allowed"
        case 500: reason = "Internal Server Error"
        default: reason = "OK"
        }
        let header = "HTTP/1.1 \(response.status) \(reason)\r\n" +
            "Content-Type: \(response.contentType)\r\n" +
            "Content-Length: \(response.body.count)\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n"
        var data = Data(header.utf8)
        data.append(response.body)
        data.withUnsafeBytes { raw in
            guard let base = raw.baseAddress else { return }
            var sent = 0
            while sent < data.count {
                let n = send(client, base.advanced(by: sent), data.count - sent, 0)
                if n <= 0 { break }
                sent += n
            }
        }
    }
}
