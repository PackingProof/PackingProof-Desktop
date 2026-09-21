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
    private var launchedHosts: [Process] = []
    private var autostartInstalled = false
    private var purposeName = "保存主机"
    private var isViewer = false

    private let port = Int(ProcessInfo.processInfo.environment["PACKINGPROOF_HOST_PORT"] ?? "") ?? 5280

    func applicationDidFinishLaunching(_ notification: Notification) {
        if CommandLine.arguments.contains("--dump-menu") {
            refreshAutostart()
            print(dumpMenu())
            exit(0)
        }

        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.title = "PP"
        statusItem = item

        refreshTimer = Timer.scheduledTimer(withTimeInterval: 10, repeats: true) { [weak self] _ in
            self?.refreshStatus()
        }
        refreshStatus()
    }

    /// 注销或关机时也要把自己拉起的主机带走
    func applicationWillTerminate(_ notification: Notification) {
        stopHost()
    }

    // MARK: - 状态

    private func refreshStatus() {
        refreshAutostart()
        guard let url = URL(string: "http://127.0.0.1:\(port)/api/node-info") else { return }
        var request = URLRequest(url: url)
        request.timeoutInterval = 3
        URLSession.shared.dataTask(with: request) { [weak self] data, response, _ in
            var name: String?
            var preset = ""
            if let data, let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                name = json["nodeName"] as? String
                preset = (json["preset"] as? String) ?? ""
            }

            let reachable = (response as? HTTPURLResponse)?.statusCode == 200
            DispatchQueue.main.async {
                self?.apply(reachable: reachable, nodeName: name, preset: preset)
            }
        }.resume()
    }

    private func apply(reachable: Bool, nodeName: String?, preset: String) {
        onlineNodeName = reachable ? nodeName : nil
        isViewer = reachable && preset == "ViewerClient"
        purposeName = isViewer ? "查看端" : "保存主机"
        // 第一行只说用途，不带电脑名与括号
        status = reachable ? purposeName : "未启动"
        // 不加指示灯，标题保持固定
        statusItem?.button?.title = "PP"
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

        // 打开就是使用，不提供"启动/停止主机"；只想看录像时切成查看端
        menu.addItem(actionItem("切换为只查看", #selector(switchToViewer)))
        menu.addItem(actionItem("打开设置", #selector(openSettings)))
        menu.addItem(actionItem("打开网页回放", #selector(openPlayback)))
        menu.addItem(.separator())
        // 开机自启同样按状态只显示一项
        menu.addItem(actionItem(
            autostartInstalled ? "取消开机自启" : "注册开机自启",
            #selector(toggleAutostart)))
        menu.addItem(actionItem("打开日志目录", #selector(openLogs)))
        menu.addItem(.separator())
        menu.addItem(actionItem("退出", #selector(quit)))

        statusItem?.menu = menu
    }

    /// 菜单内容的自检输出：不依赖人工点开，直接打印第一行与各项
    private func dumpMenu() -> String {
        var lines = ["第一行: \(status)"]
        if !isViewer { lines.append("· 切换为只查看") }
        lines.append("· 打开设置")
        lines.append("· 打开网页回放")
        lines.append("· \(autostartInstalled ? "取消开机自启" : "注册开机自启")")
        lines.append("· 打开日志目录")
        lines.append("· 退出")
        return lines.joined(separator: "\n")
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

    /// 打开设置：网页里的本机设置面板，带上锚点直接滚到那里
    @objc private func openSettings() {
        var url = "http://127.0.0.1:\(port)/"
        if let key = readAccessKey() { url += "?key=\(key)" }
        url += "#localSettings"
        NSWorkspace.shared.open(URL(string: url)!)
    }

    /// 切换为只查看：把用途改成查看端，弹窗告知，并停止再作为主机服务
    @objc private func switchToViewer() {
        notify("只查看\n\n这台电脑已切换为查看端，不再作为保存主机。\n网页回放仍可正常使用。")
        setPurpose("viewer")
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in
            self?.stopHost()
            self?.refreshStatus()
        }
    }

    /// 通过本机设置接口改用途；服务端只接受本机请求
    private func setPurpose(_ purpose: String) {
        guard let url = URL(string: "http://127.0.0.1:\(port)/api/local-settings") else { return }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try? JSONSerialization.data(withJSONObject: ["purpose": purpose])
        URLSession.shared.dataTask(with: request).resume()
    }

    @objc private func toggleAutostart() {
        let arguments = autostartInstalled ? ["--uninstall-autostart"] : ["--install-autostart"]
        runHost(arguments: arguments) { [weak self] output in
            self?.notify(output)
        }
    }

    private func stopHost() {
        for pid in runningHostPIDs() {
            kill(pid, SIGTERM)
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in self?.refreshStatus() }
    }

    @objc private func openLogs() {
        let logDirectory = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("ExpressPackingMonitoring/log", isDirectory: true)
        NSWorkspace.shared.open(logDirectory)
    }

    @objc private func quit() {
        // 退出即停止：关掉程序就把自己拉起的主机一起停掉，不留没人管的进程
        stopHost()
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { NSApp.terminate(nil) }
    }

    private func refreshAutostart() {
        let path = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/com.packingproof.host.plist")
        autostartInstalled = FileManager.default.fileExists(atPath: path.path)
    }

    private func runningHostPIDs() -> [Int32] {
        launchedHosts = launchedHosts.filter { $0.isRunning }
        return launchedHosts.map { $0.processIdentifier }
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
            launchedHosts.append(process)
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
