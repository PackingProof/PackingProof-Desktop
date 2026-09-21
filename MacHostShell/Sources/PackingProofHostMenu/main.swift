// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 PackingProof contributors

import AppKit
import Foundation

/// 菜单栏壳：菜单里直接设置用途、保存位置与开机自启；
/// 业务逻辑一律交给 .NET 主机，壳只做展示与转发。
final class HostShell: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem?
    private var refreshTimer: Timer?
    private var launchedHosts: [Process] = []
    private var lastLaunchAttempt = Date.distantPast

    private var purpose = ""
    private var storagePath = ""
    private var autostartInstalled = false
    private var hostAddress = ""
    private var hostKey = ""
    private var storagePaths: [String] = []
    private var hostProblem = ""
    private var viewerConnected = false
    private var viewerHint = "未连接主机"
    private var hostServing = false
    private var viewerRunning = false

    private let port = Int(ProcessInfo.processInfo.environment["PACKINGPROOF_HOST_PORT"] ?? "") ?? 5280

    // MARK: - 生命周期

    func applicationDidFinishLaunching(_ notification: Notification) {
        if CommandLine.arguments.contains("--status") {
            refreshSettings()
            refreshAutostart()
            let serving = probeStatus(URL(string: "http://127.0.0.1:\(port)/api/node-info")!) == 200
            hostServing = serving
            if purpose == "MobileBackupHost" && !serving { hostProblem = readHostFailure() ?? "" }
            refreshViewerConnection()
            print("第一行: \(statusText)")
            print("本机主机服务: \(serving ? "运行中" : "未运行")")
            print("查看端连接: \(viewerConnected ? "已连接" : viewerHint)")
            print("回放项: \(purpose == "MobileBackupHost" ? (serving ? "可用" : "禁用") : (viewerConnected ? "可用" : "禁用"))")
            exit(0)
        }

        if CommandLine.arguments.contains("--dump-menu") {
            refreshSettings()
            refreshAutostart()
            print(dumpMenu())
            exit(0)
        }

        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        applyIcon(to: item)
        statusItem = item

        refreshTimer = Timer.scheduledTimer(withTimeInterval: 10, repeats: true) { [weak self] _ in
            self?.refresh()
        }
        refresh()
    }

    /// 退出即停止：关掉程序就把自己拉起的主机一起停掉，不留没人管的进程
    func applicationWillTerminate(_ notification: Notification) { stopHost() }

    // MARK: - 状态

    private func refresh() {
        refreshSettings()
        refreshAutostart()
        guard let url = URL(string: "http://127.0.0.1:\(port)/api/node-info") else { return }
        var request = URLRequest(url: url)
        request.timeoutInterval = 3
        URLSession.shared.dataTask(with: request) { [weak self] _, response, _ in
            let serving = (response as? HTTPURLResponse)?.statusCode == 200
            DispatchQueue.main.async {
                guard let self else { return }
                self.hostServing = serving
                self.viewerRunning = self.launchedHosts.contains { $0.isRunning } && !serving
                if self.purpose == "MobileBackupHost" && !serving {
                    self.hostProblem = self.readHostFailure() ?? self.hostProblem
                    // 只有保存主机才需要在后台常驻；查看端由用户显式点开回放
                    self.ensureHostRunning()
                } else if serving {
                    self.hostProblem = ""
                }
                self.refreshViewerConnection()
                self.rebuildMenu()
            }
        }.resume()
    }

    private var statusText: String {
        if purpose == "ViewerClient" { return "查看端（\(viewerHint)）" }
        if purpose.isEmpty { return "未启动" }
        // 起不来时直接把原因摆在菜单第一行，用户不必去翻日志
        return hostProblem.isEmpty ? "保存主机" : "保存主机未启动：\(hostProblem)"
    }

    /// 主机没在跑就把它拉起来；失败重试间隔 30 秒，避免配置有问题时反复拉起
    private func ensureHostRunning() {
        guard Date().timeIntervalSince(lastLaunchAttempt) > 30 else { return }
        lastLaunchAttempt = Date()
        runHost(arguments: ["--no-browser", "--service"])
    }

    // MARK: - 菜单

    private func rebuildMenu() {
        let menu = NSMenu()
        menu.addItem(disabledItem(statusText))
        menu.addItem(.separator())

        let hostItem = actionItem("保存主机", #selector(useHostPurpose))
        hostItem.state = purpose == "MobileBackupHost" ? .on : .off
        menu.addItem(hostItem)

        let viewerItem = actionItem("查看端", #selector(useViewerPurpose))
        viewerItem.state = purpose == "ViewerClient" ? .on : .off
        menu.addItem(viewerItem)
        menu.addItem(.separator())

        if storagePaths.isEmpty {
            menu.addItem(disabledItem("保存位置：未设置"))
        } else {
            for (index, path) in storagePaths.enumerated() {
                menu.addItem(disabledItem("保存位置\(index + 1)：\(path)"))
            }
        }

        let diskMenu = NSMenu()
        for volume in mountedVolumes() {
            let item = NSMenuItem(title: "添加 \(volume.lastPathComponent)", action: #selector(addVolume(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = volume.path
            diskMenu.addItem(item)
        }
        if diskMenu.items.isEmpty { diskMenu.addItem(disabledItem("没有可用磁盘")) }
        let diskItem = NSMenuItem(title: "添加磁盘…", action: nil, keyEquivalent: "")
        diskItem.submenu = diskMenu
        menu.addItem(diskItem)
        let autostartItem = actionItem("开机自启", #selector(toggleAutostart))
        autostartItem.state = autostartInstalled ? .on : .off
        menu.addItem(autostartItem)
        menu.addItem(.separator())

        let playbackItem = actionItem("打开网页回放", #selector(openPlayback))
        playbackItem.isEnabled = purpose == "MobileBackupHost" ? hostServing : viewerConnected
        menu.addItem(playbackItem)
        menu.addItem(actionItem("打开日志目录", #selector(openLogs)))
        menu.addItem(.separator())
        menu.addItem(actionItem("退出", #selector(quit)))

        statusItem?.menu = menu
    }

    private func dumpMenu() -> String {
        [
            "第一行: \(statusText)",
            "· 保存主机 \(purpose == "MobileBackupHost" ? "✓" : "")",
            "· 查看端 \(purpose == "ViewerClient" ? "✓" : "")",
            "· 保存位置：\(storagePaths.isEmpty ? "未设置" : storagePaths.joined(separator: " > "))",
            "· 添加磁盘…（选磁盘后用默认子目录）",
            "· 开机自启 \(autostartInstalled ? "✓" : "")",
            "· 打开网页回放（\(purpose == "ViewerClient" ? (hostAddress.isEmpty ? "未连接主机，禁用" : "已连接 \(hostAddress)") : (hostServing ? "本机" : "未启动，禁用"))）",
            "· 打开日志目录",
            "· 退出"
        ].joined(separator: "\n")
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

    /// 菜单栏图标用应用自己的图标（打包时由 app.ico 转出），没有时退回文字
    private func applyIcon(to item: NSStatusItem) {
        guard let path = Bundle.main.resourceURL?.appendingPathComponent("MenuIcon.png"),
              let image = NSImage(contentsOf: path) else {
            item.button?.title = "PP"
            return
        }

        image.size = NSSize(width: 18, height: 18)
        item.button?.image = image
        item.button?.imagePosition = .imageOnly
    }

    // MARK: - 设置动作

    @objc private func useHostPurpose() {
        guard purpose != "MobileBackupHost" else { return }
        applySettings(purpose: "host", storagePath: nil, autostart: nil,
                      success: "已切换为保存主机")
    }

    @objc private func useViewerPurpose() {
        guard purpose != "ViewerClient" else { return }
        applySettings(purpose: "viewer", storagePath: nil, autostart: nil,
                      success: "已切换为查看端")
    }

    @objc private func toggleAutostart() {
        applySettings(purpose: nil, storagePath: nil, autostart: !autostartInstalled,
                      success: autostartInstalled ? "已取消开机自启" : "已注册开机自启",
                      askRestart: false)
    }

    /// 已挂载的磁盘：与桌面端一致，选磁盘分区，路径用默认子目录
    private func mountedVolumes() -> [URL] {
        let keys: [URLResourceKey] = [.volumeNameKey, .volumeIsBrowsableKey, .volumeIsInternalKey]
        let volumes = FileManager.default.mountedVolumeURLs(
            includingResourceValuesForKeys: keys,
            options: [.skipHiddenVolumes]) ?? []
        return volumes.filter { $0.path != "/" }
    }

    @objc private func addVolume(_ sender: NSMenuItem) {
        guard let root = sender.representedObject as? String else { return }
        // 桌面端也是这样：磁盘根 + 固定子目录名
        let path = (root as NSString).appendingPathComponent("快递打包视频")
        applySettings(purpose: nil, storagePath: nil, autostart: nil,
                      success: "已添加保存位置 \(path)", addStoragePath: path)
    }

    /// 通过本机设置接口改配置；改完问一次是否立即重启主机
    private func applySettings(
        purpose newPurpose: String?,
        storagePath newStorage: String?,
        autostart: Bool?,
        success: String,
        askRestart: Bool = true,
        addStoragePath: String? = nil) {
        var payload: [String: Any] = [:]
        if let newPurpose { payload["purpose"] = newPurpose }
        if let newStorage { payload["storagePath"] = newStorage }
        if let addStoragePath { payload["addStoragePath"] = addStoragePath }
        if let autostart { payload["autostart"] = autostart }

        guard let url = URL(string: "http://127.0.0.1:\(port)/api/local-settings") else { return }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try? JSONSerialization.data(withJSONObject: payload)

        URLSession.shared.dataTask(with: request) { [weak self] data, response, error in
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            let body = data.flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
            DispatchQueue.main.async {
                guard let self else { return }
                guard error == nil, status == 200 else {
                    let message = (body?["error"] as? String) ?? error?.localizedDescription ?? "设置失败"
                    self.notify("设置未生效：\(message)")
                    return
                }

                self.refreshSettings()
                self.refreshAutostart()
                self.rebuildMenu()
                if askRestart {
                    self.confirmRestart(after: success)
                } else {
                    self.notify(success)
                }
            }
        }.resume()
    }

    /// 改完设置问一次：是否立即重启，让新用途/新位置马上生效
    private func confirmRestart(after message: String) {
        let alert = NSAlert()
        alert.messageText = message
        alert.informativeText = "是否立即重启主机让设置生效？"
        alert.addButton(withTitle: "立即重启")
        alert.addButton(withTitle: "稍后")
        if alert.runModal() == .alertFirstButtonReturn {
            restartHost()
        }
    }

    private func restartHost() {
        if autostartInstalled {
            // 装机自启时进程由 launchd 托管，必须让 launchd 重启它，否则会顶掉托管关系
            let uid = String(getuid())
            let kickstart = Process()
            kickstart.executableURL = URL(fileURLWithPath: "/bin/launchctl")
            kickstart.arguments = ["kickstart", "-k", "gui/\(uid)/com.packingproof.host"]
            try? kickstart.run()
            kickstart.waitUntilExit()
        } else {
            stopHost()
        }

        lastLaunchAttempt = Date()
        hostProblem = ""
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in
            guard let self else { return }
            if !self.autostartInstalled { self.runHost(arguments: ["--no-browser", "--service"]) }
            DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in
                guard let self else { return }
                self.refresh()
                if self.purpose == "MobileBackupHost" && !self.hostServing, let reason = self.readHostFailure() {
                    self.notify("主机没有起来：\(reason)")
                }
            }
        }
    }

    // MARK: - 其它菜单动作

    @objc private func openPlayback() {
        if purpose == "ViewerClient" {
            // 查看端：打开已连接的主机，而不是本机地址
            guard !hostAddress.isEmpty else { return }
            var hostUrl = hostAddress
            if !hostKey.isEmpty {
                hostUrl += (hostAddress.contains("?") ? "&" : "?") + "key=\(hostKey)"
            }
            if let url = URL(string: hostUrl) { NSWorkspace.shared.open(url) }
            return
        }

        var url = "http://127.0.0.1:\(port)/"
        if let key = readAccessKey() { url += "?key=\(key)" }
        NSWorkspace.shared.open(URL(string: url)!)
    }

    @objc private func openLogs() {
        NSWorkspace.shared.open(Self.logDirectory)
    }

    @objc private func quit() {
        stopHost()
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { NSApp.terminate(nil) }
    }

    private func notify(_ text: String) {
        let alert = NSAlert()
        alert.messageText = "PackingProof 保存主机"
        alert.informativeText = text
        alert.runModal()
    }

    // MARK: - 配置与进程

    private static var configURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/ExpressPackingMonitoring/config.json")
    }

    private static var logDirectory: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/ExpressPackingMonitoring/log")
    }

    private func refreshSettings() {
        guard let data = try? Data(contentsOf: Self.configURL),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            purpose = ""
            storagePath = ""
            return
        }

        purpose = (json["DeploymentPreset"] as? String) ?? ""
        hostAddress = (json["LastKnownHostAddress"] as? String) ?? ""
        hostKey = (json["LastKnownHostWebAccessKey"] as? String) ?? ""
        let locations = json["StorageLocations"] as? [[String: Any]]
        let ordered = (locations ?? [])
            .filter { (($0["Path"] as? String) ?? "").isEmpty == false }
            .sorted { (($0["Priority"] as? Int) ?? 99) < (($1["Priority"] as? Int) ?? 99) }
        storagePaths = ordered.compactMap { $0["Path"] as? String }
        storagePath = storagePaths.first ?? ""
    }

    /// 查看端：只有主机真的能连上（或已有密钥）才算"已连接"，否则禁用回放项
    private func refreshViewerConnection() {
        guard purpose == "ViewerClient", !hostAddress.isEmpty else {
            viewerConnected = false
            viewerHint = hostAddress.isEmpty ? "未连接主机" : viewerHint
            return
        }

        let status = probeStatus(URL(string: hostAddress + "/")!)
        switch status {
        case 200, 302:
            viewerConnected = true
            viewerHint = "已连接 \(hostName)"
        case 401:
            // 主机开了网页保护：拿到密钥才算连上
            viewerConnected = !hostKey.isEmpty
            viewerHint = viewerConnected ? "已连接 \(hostName)" : "等待主机确认接入"
        default:
            viewerConnected = false
            viewerHint = "未连接主机"
        }
    }

    private var hostName: String {
        hostAddress.isEmpty ? "主机" : hostAddress
    }

    /// 主机启动失败时，日志最后一行就是原因
    private func readHostFailure() -> String? {
        let logURL = Self.logDirectory.appendingPathComponent("host.log")
        guard let text = try? String(contentsOf: logURL, encoding: .utf8) else { return nil }
        let lines = text.split(separator: "\n").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        guard let last = lines.last else { return nil }
        guard last.contains("失败") else { return nil }
        return last.replacingOccurrences(of: "保存主机启动失败：", with: "")
    }

    private func probeStatus(_ url: URL, timeout: TimeInterval = 4) -> Int {
        var status = 0
        let semaphore = DispatchSemaphore(value: 0)
        var request = URLRequest(url: url)
        request.timeoutInterval = timeout
        URLSession.shared.dataTask(with: request) { _, response, _ in
            status = (response as? HTTPURLResponse)?.statusCode ?? 0
            semaphore.signal()
        }.resume()
        _ = semaphore.wait(timeout: .now() + timeout + 1)
        return status
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

    private func stopHost() {
        for pid in runningHostPIDs() { kill(pid, SIGTERM) }
        hostServing = false
        viewerRunning = false
        rebuildMenu()
    }

    /// 主机可执行文件：优先用同一个 .app 内的副本，其次用环境变量指向的路径（开发时用）
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

    private func runHost(arguments: [String]) {
        guard let executable = hostExecutable() else {
            notify("未找到主机程序。请把菜单栏壳与保存主机放在同一个 .app 里。")
            return
        }

        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        // 主机输出写进日志，出问题时能查；不再丢掉
        try? FileManager.default.createDirectory(at: Self.logDirectory, withIntermediateDirectories: true)
        let logURL = Self.logDirectory.appendingPathComponent("host.log")
        if !FileManager.default.fileExists(atPath: logURL.path) {
            FileManager.default.createFile(atPath: logURL.path, contents: nil)
        }
        if let handle = try? FileHandle(forWritingTo: logURL) {
            handle.seekToEndOfFile()
            process.standardOutput = handle
            process.standardError = handle
        }

        do {
            try process.run()
            launchedHosts.append(process)
        } catch {
            notify("启动主机失败：\(error.localizedDescription)")
        }
    }

    /// 网页访问密钥来自主机自己的配置
    private func readAccessKey() -> String? {
        guard let data = try? Data(contentsOf: Self.configURL),
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
