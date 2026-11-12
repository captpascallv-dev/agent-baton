// Dedicated localhost-only Studio preview for application-only QA screenshots.
// No user browser profile, private tabs, or screen-control APIs are used.
import AppKit
import WebKit

final class Preview: NSObject, NSApplicationDelegate, WKNavigationDelegate {
    var window: NSWindow!
    var page: WKWebView!
    let address: URL
    init(_ address: URL) { self.address = address }
    func applicationDidFinishLaunching(_ notification: Notification) {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        page = WKWebView(frame: .zero, configuration: configuration)
        page.navigationDelegate = self
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1220, height: 760), styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        window.title = "研究室 · 本机假执行者验证"
        window.contentView = page
        window.center(); window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        page.load(URLRequest(url: address))
    }
    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        guard let url = action.request.url, url.scheme == "http", url.host == "127.0.0.1", url.port == address.port else { decisionHandler(.cancel); return }
        decisionHandler(.allow)
    }
    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) {
            print("STUDIO_WINDOW_ID=\(self.window.windowNumber)")
            fflush(stdout)
        }
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}
let values = CommandLine.arguments
 guard values.count == 2, let url = URL(string: values[1]), url.scheme == "http", url.host == "127.0.0.1", url.port != nil else {
    fputs("Usage: studio-mac-preview http://127.0.0.1:PORT\n", stderr); exit(2)
}
let app = NSApplication.shared
app.setActivationPolicy(.regular)
let delegate = Preview(url)
app.delegate = delegate
app.run()
