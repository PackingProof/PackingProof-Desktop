// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 PackingProof contributors

import AppKit
import Foundation

/// 菜单栏壳：显示保存主机是否在跑，并把菜单动作转成主机进程的命令行调用。
/// 业务逻辑一律不在这里实现，避免和 .NET 核心出现第二套实现。
final class HostShell: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem?
    private var refreshTimer: Timer?
    private var status = "正在检查保存主机…"
    private var onlineNodeName: String?
    private var lastLaunchAttempt = Date.distantPast

    private let port = Int(ProcessInfo.processInfo.environment["PACKINGPROOF_HOST_PORT"] ?? "") ?? 5280

    func applicationDidFinishLaunching(_ notification: Notification) {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.title = "PP"
        statusItem = item

        refreshTimer = Timer.scheduledTimer(withTimeInterval: 10, repeats: true) { [weak self] _ in
            self?.refreshStatus()
        }
        refreshStatus()
    }

    // MARK: - 状态

    private func refreshStatus() {
        guard let url = URL(string: "http://127.0.0.1:\(port)/api/node-info") else { return }
        var request = URLRequest(url: url)
        request.timeoutInterval = 3
        URLSession.shared.dataTask(with: request) { [weak self] data, response, _ in
            var name: String?
            if let data, let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                name = json["nodeName"] as? String
            }

            let reachable = (response as? HTTPURLResponse)?.statusCode == 200
            DispatchQueue.main.async {
                self?.apply(reachable: reachable, nodeName: name)
            }
        }.resume()
    }

    private func apply(reachable: Bool, nodeName: String?) {
        onlineNodeName = reachable ? nodeName : nil
        status = reachable ? "保存主机运行中（\(nodeName ?? "未命名")）" : "保存主机未运行"
        statusItem?.button?.title = reachable ? "PP ●" : "PP ○"
        rebuildMenu()
        if !reachable { ensureHostRunning() }
    }

    /// 主机没在跑就把它拉起来：菜单栏壳是用户双击的入口，不能只显示状态不干活。
    /// 失败重试间隔 30 秒，避免配置有问题时反复拉起。
    private func ensureHostRunning() {
        guard Date().timeIntervalSince(lastLaunchAttempt) > 30 else { return }
        lastLaunchAttempt = Date()
        runHost(arguments: ["--no-browser"])
    }

    // MARK: - 菜单

    private func rebuildMenu() {
        let menu = NSMenu()
        menu.addItem(disabledItem(status))
        menu.addItem(.separator())

        menu.addItem(actionItem("打开网页回放", #selector(openPlayback)))
        menu.addItem(actionItem("切换用途…", #selector(switchPurpose)))
        menu.addItem(.separator())
        menu.addItem(actionItem("注册开机自启", #selector(installAutostart)))
        menu.addItem(actionItem("取消开机自启", #selector(uninstallAutostart)))
        menu.addItem(actionItem("打开日志目录", #selector(openLogs)))
        menu.addItem(.separator())
        menu.addItem(actionItem("退出菜单", #selector(quit)))

        statusItem?.menu = menu
    }

    private func disabledItem(_ title: String) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        item.isEnabled = false
        return item
    }

    private func actionItem(_ title: String, _ selector: Selector) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: selector, keyEquivalent: "")
        item.target = self
        return item
    }

    // MARK: - 动作

    @objc private func openPlayback() {
        var url = "http://127.0.0.1:\(port)/"
        if let key = readAccessKey() { url += "?key=\(key)" }
        NSWorkspace.shared.open(URL(string: url)!)
    }

    @objc private func switchPurpose() {
        // 主机进程自己在交互模式下会问用途；这里只负责把它拉起来
        runHost(arguments: ["--switch-purpose"])
    }

    @objc private func installAutostart() {
        runHost(arguments: ["--install-autostart"]) { [weak self] output in
            self?.notify(output)
        }
    }

    @objc private func uninstallAutostart() {
        runHost(arguments: ["--uninstall-autostart"]) { [weak self] output in
            self?.notify(output)
        }
    }

    @objc private func openLogs() {
        let logDirectory = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("ExpressPackingMonitoring/log", isDirectory: true)
        NSWorkspace.shared.open(logDirectory)
    }

    @objc private func quit() {
        NSApp.terminate(nil)
    }

    private func notify(_ text: String) {
        let alert = NSAlert()
        alert.messageText = "PackingProof 保存主机"
        alert.informativeText = text
        alert.runModal()
    }

    // MARK: - 主机进程

    /// 主机可执行文件：优先用同一个 .app 内的副本，其次用环境变量指向的路径（开发时用）。
    private func hostExecutable() -> URL? {
        let fileManager = FileManager.default
        if let override = ProcessInfo.processInfo.environment["PACKINGPROOF_HOST_BIN"],
           fileManager.isExecutableFile(atPath: override) {
            return URL(fileURLWithPath: override)
        }

        let bundled = Bundle.main.bundleURL
            .appendingPathComponent("Contents/MacOS/Host/ExpressPackingMonitoring.Host")
        return fileManager.isExecutableFile(atPath: bundled.path) ? bundled : nil
    }

    private func runHost(arguments: [String], completion: ((String) -> Void)? = nil) {
        guard let executable = hostExecutable() else {
            notify("未找到主机程序。请把菜单栏壳与保存主机放在同一个 .app 里。")
            return
        }

        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe

        do {
            try process.run()
        } catch {
            notify("启动主机失败：\(error.localizedDescription)")
            return
        }

        guard let completion else {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in self?.refreshStatus() }
            return
        }

        DispatchQueue.global().async {
            process.waitUntilExit()
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            let output = String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            DispatchQueue.main.async {
                completion(output.isEmpty ? (process.terminationStatus == 0 ? "操作完成" : "操作失败（退出码 \(process.terminationStatus)）") : output)
                self.refreshStatus()
            }
        }
    }

    /// 网页访问密钥来自主机自己的配置，菜单栏只是照抄它去打开页面。
    private func readAccessKey() -> String? {
        let configURL = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("ExpressPackingMonitoring/config.json")
        guard let data = try? Data(contentsOf: configURL),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let key = json["WebAccessKey"] as? String,
              !key.isEmpty else { return nil }
        return key
    }
}

let application = NSApplication.shared
let shell = HostShell()
application.delegate = shell
application.setActivationPolicy(.accessory)
application.run()
