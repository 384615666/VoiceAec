import AppKit
import WebKit
import VoiceAecCore

final class PanelWindowController: NSObject, NSWindowDelegate, WKScriptMessageHandler,
    WKNavigationDelegate, WKUIDelegate {
    private let controller: AppController
    private var window: NSWindow!
    private var webView: WKWebView!
    private var didLoad = false
    private var retries = 0
    private var dragMonitor: Any?
    private var dragStartMouse = NSPoint.zero
    private var dragStartOrigin = NSPoint.zero
    private var resizeNotify: DispatchWorkItem?

    init(controller: AppController) {
        self.controller = controller
        super.init()
        buildWindow()
    }

    private func buildWindow() {
        var width: CGFloat = 1248
        var height: CGFloat = 860
        if let visible = NSScreen.main?.visibleFrame {
            width = min(width, max(980, visible.width - 40))
            height = min(height, max(680, visible.height - 40))
        }
        let frame = NSRect(x: 0, y: 0, width: width, height: height)
        window = NSWindow(contentRect: frame,
                          styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                          backing: .buffered, defer: false)
        window.title = "VoiceAec"
        window.titleVisibility = .hidden
        window.titlebarAppearsTransparent = true
        window.isMovableByWindowBackground = false
        window.isReleasedWhenClosed = false
        window.collectionBehavior = [.fullScreenPrimary]
        window.minSize = NSSize(width: 980, height: 680)
        window.tabbingMode = .disallowed
        window.center()
        window.setFrameAutosaveName("VoiceAecPanel")
        window.delegate = self
        for button in [NSWindow.ButtonType.closeButton, .miniaturizeButton, .zoomButton] {
            window.standardWindowButton(button)?.isHidden = true
        }

        let configuration = WKWebViewConfiguration()
        configuration.defaultWebpagePreferences.allowsContentJavaScript = true
        configuration.preferences.setValue(true, forKey: "developerExtrasEnabled")
        let userContent = WKUserContentController()
        userContent.addUserScript(WKUserScript(source: PanelWindowController.bridgeScript,
                                               injectionTime: .atDocumentStart,
                                               forMainFrameOnly: true))
        userContent.add(self, name: "voiceaec")
        configuration.userContentController = userContent

        webView = WKWebView(frame: window.contentView?.bounds ?? frame, configuration: configuration)
        webView.autoresizingMask = [.width, .height]
        webView.navigationDelegate = self
        webView.uiDelegate = self
        webView.allowsBackForwardNavigationGestures = false
        webView.allowsMagnification = false
        webView.setValue(false, forKey: "drawsBackground")
        window.contentView = webView
        loadPanel()
        maybeTakeSnapshot()
    }

    private func maybeTakeSnapshot() {
        guard let path = ProcessInfo.processInfo.environment["VOICEAEC_SNAPSHOT"], !path.isEmpty else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 4.0) { [weak self] in
            self?.webView.takeSnapshot(with: nil) { image, error in
                guard let image, let tiff = image.tiffRepresentation,
                      let rep = NSBitmapImageRep(data: tiff),
                      let png = rep.representation(using: .png, properties: [:]) else {
                    Log.error("snapshot failed: \(error?.localizedDescription ?? "no image")")
                    return
                }
                try? png.write(to: URL(fileURLWithPath: path))
                Log.info("snapshot written to \(path)")
            }
        }
    }

    private func loadPanel() {
        retries += 1
        webView.load(URLRequest(url: controller.panelURL, cachePolicy: .reloadIgnoringLocalCacheData))
    }

    func showWindow() {
        window.orderFrontRegardless()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.08) { [weak self] in
            self?.notifyPanelVisible(true)
            self?.notifyMaximized()
        }
    }

    func hideWindow() {
        window.orderOut(nil)
        notifyPanelVisible(false)
    }

    func showAbout() {
        showWindow()
        evaluate("window.dispatchEvent(new CustomEvent('voiceaec:about'))")
    }

    func notifyDeviceChanged() {
        evaluate("window.__voiceaecDispatch && window.__voiceaecDispatch({deviceChanged:true})")
    }

    private func notifyPanelVisible(_ visible: Bool) {
        evaluate("window.__voiceaecDispatch && window.__voiceaecDispatch({panelVisible:\(visible)})")
    }

    private func notifyMaximized() {
        let value = window.isZoomed ? "true" : "false"
        evaluate("window.__voiceaecDispatch && window.__voiceaecDispatch({windowMaximized:\(value)})")
    }

    private func evaluate(_ script: String) {
        guard didLoad else { return }
        webView.evaluateJavaScript(script, completionHandler: nil)
    }

    private func scheduleMaximizedNotify() {
        resizeNotify?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.notifyMaximized() }
        resizeNotify = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15, execute: work)
    }

    // MARK: - Drag

    private func beginDrag() {
        guard dragMonitor == nil else { return }
        dragStartMouse = NSEvent.mouseLocation
        dragStartOrigin = window.frame.origin
        dragMonitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDragged, .leftMouseUp]) { [weak self] event in
            guard let self else { return event }
            if event.type == .leftMouseUp {
                self.endDrag()
                return event
            }
            let mouse = NSEvent.mouseLocation
            let origin = NSPoint(x: self.dragStartOrigin.x + mouse.x - self.dragStartMouse.x,
                                 y: self.dragStartOrigin.y + mouse.y - self.dragStartMouse.y)
            self.window.setFrameOrigin(origin)
            return event
        }
    }

    private func endDrag() {
        if let monitor = dragMonitor {
            NSEvent.removeMonitor(monitor)
            dragMonitor = nil
        }
    }

    // MARK: - WKScriptMessageHandler

    func userContentController(_ userContentController: WKUserContentController,
                               didReceive message: WKScriptMessage) {
        guard let command = message.body as? String else { return }
        switch command {
        case "window-minimize":
            window.miniaturize(nil)
        case "window-maximize":
            window.zoom(nil)
            scheduleMaximizedNotify()
        case "window-close":
            hideWindow()
        case "window-drag-start":
            beginDrag()
        case "copy-lan-url":
            let pasteboard = NSPasteboard.general
            pasteboard.clearContents()
            pasteboard.setString(controller.panelURL.absoluteString, forType: .string)
        default:
            break
        }
    }

    // MARK: - WKNavigationDelegate

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        didLoad = true
        retries = 0
        notifyPanelVisible(window.isVisible)
        notifyMaximized()
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!,
                 withError error: Error) {
        didLoad = false
        guard retries < 40 else {
            Log.error("panel load failed: \(error.localizedDescription)")
            return
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) { [weak self] in
            self?.loadPanel()
        }
    }

    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        guard let url = navigationAction.request.url else {
            decisionHandler(.allow); return
        }
        let local = url.host == "127.0.0.1" || url.host == "localhost"
        if !local && (url.scheme == "http" || url.scheme == "https") {
            NSWorkspace.shared.open(url)
            decisionHandler(.cancel)
            return
        }
        decisionHandler(.allow)
    }

    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                 for navigationAction: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        if let url = navigationAction.request.url { NSWorkspace.shared.open(url) }
        return nil
    }

    // MARK: - NSWindowDelegate

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        hideWindow()
        return false
    }

    func windowDidMiniaturize(_ notification: Notification) {
        notifyPanelVisible(false)
    }

    func windowDidDeminiaturize(_ notification: Notification) {
        notifyPanelVisible(true)
    }

    func windowDidResize(_ notification: Notification) {
        scheduleMaximizedNotify()
    }

    func windowDidBecomeKey(_ notification: Notification) {
        notifyPanelVisible(true)
    }

    func windowDidResignKey(_ notification: Notification) {
        endDrag()
    }

    private static let bridgeScript = """
    (function () {
      const listeners = [];
      window.chrome = window.chrome || {};
      if (!window.chrome.webview) {
        window.chrome.webview = {
          postMessage: function (message) {
            try { window.webkit.messageHandlers.voiceaec.postMessage(message); } catch (e) {}
          },
          addEventListener: function (type, listener) {
            if (type === 'message' && typeof listener === 'function') listeners.push(listener);
          }
        };
      }
      window.__voiceaecDispatch = function (data) {
        for (const listener of listeners) {
          try { listener({ data: data }); } catch (e) {}
        }
      };
    })();
    """
}
