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
    private var hostNodeId = ""
    private var storagePaths: [String] = []
    private var hostProblem = ""
    private var hostServing = false
    private var viewerRunning = false

    /// 查看端状态与文案全部取自主机命令行的同一份口径（与 Windows 查看窗口一致），
    /// 壳不认识任何一个状态词，也不自己做网络预检。
    private var viewerState = ""
    private var viewerStatusText = ""
    private var viewerStatusWords: [String: String] = [:]
    private var viewerStatusGeneration = 0

    /// 存储位置容量现状与已发现主机：都由主机命令行返回，壳只负责显示
    private var storageLocations: [[String: Any]] = []
    private var lastStorageSummary = Date.distantPast
    private var discoveredHosts: [[String: Any]] = []
    private var hostSearchInFlight = false
    private var lastHostSearch = Date.distantPast

    private var viewerConnected: Bool { viewerState == "online" }

    private let port = Int(ProcessInfo.processInfo.environment["PACKINGPROOF_HOST_PORT"] ?? "") ?? 5280

    // MARK: - 生命周期

    func applicationDidFinishLaunching(_ notification: Notification) {
        if CommandLine.arguments.contains("--status") {
            refreshSettings()
            refreshAutostart()
            let serving = probeStatus(URL(string: "http://127.0.0.1:\(port)/api/node-info")!) == 200
            hostServing = serving
            if purpose == "MobileBackupHost" && !serving { hostProblem = readHostFailure() ?? "" }
            loadStorageSummarySync()
            if purpose == "ViewerClient" {
                loadViewerStatusSync()
                loadDiscoveredHostsSync()
            }
            print("第一行: \(statusText)")
            print("本机主机服务: \(serving ? "运行中" : "未运行")")
            if purpose == "ViewerClient" {
                print("查看端状态: \(viewerState.isEmpty ? "—" : viewerState) \(viewerStatusText)")
            }
            for line in storageSummaryLines() { print(line) }
            if CommandLine.arguments.contains("--hosts") && purpose == "ViewerClient" {
                print("发现主机: \(discoveredHosts.isEmpty ? statusWord("notFound") : discoveredHosts.map { describeHost($0) }.joined(separator: "、"))")
            }
            print("回放项: \(purpose == "MobileBackupHost" ? (serving ? "可用" : "禁用") : (viewerConnected ? "可用" : "禁用"))")
            exit(0)
        }

        if CommandLine.arguments.contains("--dump-menu") {
            refreshSettings()
            refreshAutostart()
            loadStorageSummarySync()
            if purpose == "ViewerClient" {
                loadViewerStatusSync()
                if CommandLine.arguments.contains("--hosts") { loadDiscoveredHostsSync() }
            }
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
        refreshStorageSummary()
        refreshViewerStatus()
        refreshDiscoveredHosts()
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
                self.rebuildMenu()
            }
        }.resume()
    }

    private var statusText: String {
        if purpose == "ViewerClient" {
            return viewerStatusText.isEmpty ? "查看端" : "查看端（\(viewerStatusText)）"
        }
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
                // 点一下就在 Finder 里打开该目录（以前这里是禁用项，点不动）
                let item = NSMenuItem(
                    title: "保存位置\(index + 1)：\(path)",
                    action: #selector(openStorageLocation(_:)),
                    keyEquivalent: "")
                item.target = self
                item.representedObject = path
                menu.addItem(item)
            }
        }

        let capacityItem = NSMenuItem(title: "存储空间上限…", action: nil, keyEquivalent: "")
        capacityItem.submenu = buildStorageLimitMenu()
        menu.addItem(capacityItem)

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

        if purpose == "ViewerClient" {
            let hostsItem = NSMenuItem(title: "保存主机…", action: nil, keyEquivalent: "")
            hostsItem.submenu = buildHostMenu()
            menu.addItem(hostsItem)
        }

        let playbackItem = actionItem("打开网页回放", #selector(openPlayback))
        playbackItem.isEnabled = purpose == "MobileBackupHost" ? hostServing : viewerConnected
        menu.addItem(playbackItem)
        menu.addItem(actionItem("打开日志目录", #selector(openLogs)))
        menu.addItem(.separator())
        menu.addItem(actionItem("退出", #selector(quit)))

        statusItem?.menu = menu
    }

    /// 每个保存位置一个子菜单：先摆现状，再给"设置容量上限 / 设置预留空间"两个入口
    private func buildStorageLimitMenu() -> NSMenu {
        let menu = NSMenu()
        guard !storageLocations.isEmpty else {
            menu.addItem(disabledItem("还没有可用的保存位置"))
            return menu
        }

        for location in storageLocations {
            guard let path = location["path"] as? String else { continue }
            let name = (location["displayName"] as? String) ?? path
            let available = (location["available"] as? Bool) ?? false
            let capacityKnown = (location["capacityKnown"] as? Bool) ?? false
            let submenu = NSMenu()

            if !available {
                submenu.addItem(disabledItem("磁盘未接入，暂时读不到容量"))
            } else if capacityKnown {
                let capacity = formatNumber(number(location["capacityGB"]))
                let reserve = formatNumber(number(location["reserveGB"]))
                submenu.addItem(disabledItem("容量上限 \(capacity) GB，预留 \(reserve) GB"))
                let recommended = number(location["recommendedReserveGB"])
                if number(location["reserveGB"]) < recommended {
                    submenu.addItem(disabledItem("预留偏低，建议至少 \(formatNumber(recommended)) GB"))
                }
            } else {
                submenu.addItem(disabledItem("磁盘太小，放不下最低预留"))
            }

            let capacityAction = NSMenuItem(
                title: "设置容量上限…",
                action: #selector(promptCapacity(_:)),
                keyEquivalent: "")
            capacityAction.target = self
            capacityAction.representedObject = path
            capacityAction.isEnabled = capacityKnown
            submenu.addItem(capacityAction)

            let reserveAction = NSMenuItem(
                title: "设置预留空间…",
                action: #selector(promptReserve(_:)),
                keyEquivalent: "")
            reserveAction.target = self
            reserveAction.representedObject = path
            reserveAction.isEnabled = capacityKnown
            submenu.addItem(reserveAction)

            let openAction = NSMenuItem(
                title: "在 Finder 中打开",
                action: #selector(openStorageLocation(_:)),
                keyEquivalent: "")
            openAction.target = self
            openAction.representedObject = path
            submenu.addItem(openAction)

            let entry = NSMenuItem(title: name, action: nil, keyEquivalent: "")
            entry.submenu = submenu
            menu.addItem(entry)
        }

        return menu
    }

    /// 查看端：把发现到的主机做成子菜单，标出当前那台，点选即切换，可移除
    private func buildHostMenu() -> NSMenu {
        let menu = NSMenu()
        if hostSearchInFlight {
            let searching = statusWord("searching")
            menu.addItem(disabledItem(searching.isEmpty ? statusText : searching))
        } else if discoveredHosts.isEmpty {
            let notFound = statusWord("notFound")
            if !notFound.isEmpty { menu.addItem(disabledItem(notFound)) }
        } else {
            for host in discoveredHosts {
                let nodeId = (host["nodeId"] as? String) ?? ""
                let item = NSMenuItem(
                    title: describeHost(host),
                    action: #selector(selectHost(_:)),
                    keyEquivalent: "")
                item.target = self
                item.representedObject = host
                item.state = (!nodeId.isEmpty && nodeId == hostNodeId) ? .on : .off
                menu.addItem(item)
            }
        }

        menu.addItem(.separator())
        menu.addItem(actionItem("重新搜索", #selector(rescanHosts)))
        let forgetItem = actionItem("移除当前主机", #selector(forgetHost))
        forgetItem.isEnabled = !hostNodeId.isEmpty || !hostAddress.isEmpty
        menu.addItem(forgetItem)
        return menu
    }

    private func dumpMenu() -> String {
        var lines: [String] = []
        lines.append("第一行: \(statusText)")
        lines.append("· 保存主机 \(purpose == "MobileBackupHost" ? "✓" : "")")
        lines.append("· 查看端 \(purpose == "ViewerClient" ? "✓" : "")")
        if storagePaths.isEmpty {
            lines.append("· 保存位置：未设置")
        } else {
            for (index, path) in storagePaths.enumerated() {
                lines.append("· 保存位置\(index + 1)：\(path)（点击在 Finder 中打开）")
            }
        }
        lines.append(contentsOf: storageSummaryLines())
        lines.append("· 添加磁盘…（选磁盘后用默认子目录）")
        lines.append("· 开机自启 \(autostartInstalled ? "✓" : "")")
        if purpose == "ViewerClient" {
            lines.append("· 保存主机…：\(hostMenuSummary())")
        }
        let playback = purpose == "ViewerClient"
            ? (viewerConnected ? "已连接 \(hostAddress)" : "禁用（\(viewerStatusText)）")
            : (hostServing ? "本机" : "未启动，禁用")
        lines.append("· 打开网页回放（\(playback)）")
        lines.append("· 打开日志目录")
        lines.append("· 退出")
        return lines.joined(separator: "\n")
    }

    private func storageSummaryLines() -> [String] {
        guard !storageLocations.isEmpty else { return ["· 存储空间上限：暂不可用"] }
        return storageLocations.map { location in
            let name = (location["displayName"] as? String) ?? ""
            guard (location["available"] as? Bool) ?? false else {
                return "· 存储空间上限 \(name)：磁盘未接入"
            }
            let capacity = formatNumber(number(location["capacityGB"]))
            let reserve = formatNumber(number(location["reserveGB"]))
            return "· 存储空间上限 \(name)：\(capacity) GB，预留 \(reserve) GB"
        }
    }

    private func hostMenuSummary() -> String {
        if hostSearchInFlight { return statusWord("searching") }
        if discoveredHosts.isEmpty { return statusWord("notFound") }
        return discoveredHosts.map { host in
            let nodeId = (host["nodeId"] as? String) ?? ""
            return (nodeId == hostNodeId && !nodeId.isEmpty ? "✓ " : "") + describeHost(host)
        }.joined(separator: "、")
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
                self.refreshStorageSummary(force: true)
                if self.purpose == "ViewerClient" {
                    self.refreshViewerStatus()
                    self.refreshDiscoveredHosts(force: true)
                }
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
        statusItem?.menu?.cancelTracking()
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

    // MARK: - 保存位置与容量上限

    /// 在 Finder 中打开保存位置；目录还没建出来时退到最近的已有上级目录，避免点了没反应
    @objc private func openStorageLocation(_ sender: NSMenuItem) {
        guard let path = sender.representedObject as? String, !path.isEmpty else { return }
        var isDirectory: ObjCBool = false
        if FileManager.default.fileExists(atPath: path, isDirectory: &isDirectory), isDirectory.boolValue {
            NSWorkspace.shared.open(URL(fileURLWithPath: path))
            return
        }

        var parent = (path as NSString).deletingLastPathComponent
        while !parent.isEmpty && parent != "/" && !FileManager.default.fileExists(atPath: parent) {
            parent = (parent as NSString).deletingLastPathComponent
        }
        NSWorkspace.shared.open(URL(fileURLWithPath: parent.isEmpty ? "/" : parent))
    }

    @objc private func promptCapacity(_ sender: NSMenuItem) {
        guard let path = sender.representedObject as? String else { return }
        let location = storageLocation(for: path) ?? [:]
        let maximum = number(location["maximumCapacityGB"])
        let message = maximum > 0
            ? "这片磁盘最多留给录像 \(formatNumber(maximum)) GB"
            : "这片磁盘留给录像多少 GB"
        guard let gigabytes = promptForNumber(
            title: "设置容量上限（GB）",
            message: message,
            current: number(location["capacityGB"])) else { return }
        applyStorageChange(
            ["--set-storage-capacity", formatNumber(gigabytes), "--storage-path", path],
            success: "已设置容量上限")
    }

    @objc private func promptReserve(_ sender: NSMenuItem) {
        guard let path = sender.representedObject as? String else { return }
        let location = storageLocation(for: path) ?? [:]
        let recommended = number(location["recommendedReserveGB"])
        let message = recommended > 0
            ? "磁盘写满前始终留出的空闲空间；建议至少 \(formatNumber(recommended)) GB"
            : "磁盘写满前始终留出的空闲空间（GB）"
        guard let gigabytes = promptForNumber(
            title: "设置预留空间（GB）",
            message: message,
            current: number(location["reserveGB"])) else { return }
        applyStorageChange(
            ["--set-storage-reserve", formatNumber(gigabytes), "--storage-path", path],
            success: "已设置预留空间")
    }

    private func promptForNumber(title: String, message: String, current: Double) -> Double? {
        statusItem?.menu?.cancelTracking()
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.addButton(withTitle: "保存")
        alert.addButton(withTitle: "取消")
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 220, height: 24))
        field.stringValue = current > 0 ? formatNumber(current) : ""
        alert.accessoryView = field
        alert.window.initialFirstResponder = field
        guard alert.runModal() == .alertFirstButtonReturn else { return nil }

        let raw = field.stringValue.trimmingCharacters(in: .whitespaces)
        guard let value = Double(raw), value.isFinite, value > 0 else {
            notify("请输入大于 0 的数字（单位 GB）")
            return nil
        }
        return value
    }

    /// 容量与预留都写进配置里的同一个预留值，换算规则由核心负责；改完不需要重启主机
    private func applyStorageChange(_ arguments: [String], success: String) {
        runHostCommand(arguments) { [weak self] json in
            guard let self else { return }
            guard let json, (json["ok"] as? Bool) == true else {
                self.notify((json?["error"] as? String) ?? "设置未生效")
                return
            }

            self.refreshStorageSummary(force: true)
            let capacity = self.formatNumber(self.number(json["capacityGB"]))
            let reserve = self.formatNumber(self.number(json["reserveGB"]))
            self.notify("\(success)：容量上限 \(capacity) GB，预留 \(reserve) GB")
        }
    }

    // MARK: - 查看端主机

    /// 切换主机：先记住新主机，再走一次查看端接入流程（申请接入 → 记住密钥 → 打开网页）
    @objc private func selectHost(_ sender: NSMenuItem) {
        guard let host = sender.representedObject as? [String: Any],
              let address = host["address"] as? String else { return }
        let nodeId = (host["nodeId"] as? String) ?? ""
        let nodeName = (host["nodeName"] as? String) ?? ""

        runHostCommand(
            ["--select-host", address, "--host-node-id", nodeId, "--host-node-name", nodeName]
        ) { [weak self] json in
            guard let self else { return }
            guard let json, (json["ok"] as? Bool) == true else {
                self.notify((json?["error"] as? String) ?? "切换主机失败")
                return
            }

            self.refreshSettings()
            self.refreshViewerStatus()
            self.rebuildMenu()
            self.connectSelectedHost()
        }
    }

    /// 接入流程与桌面端一致：发现主机 → 需要时申请接入 → 记住地址与密钥 → 打开回放网页
    private func connectSelectedHost() {
        runHost(arguments: ["--service"])
    }

    @objc private func rescanHosts() {
        discoveredHosts = []
        hostSearchInFlight = false
        refreshDiscoveredHosts(force: true)
        rebuildMenu()
    }

    @objc private func forgetHost() {
        statusItem?.menu?.cancelTracking()
        let alert = NSAlert()
        alert.messageText = "移除当前保存主机"
        alert.informativeText = "移除后要重新搜索主机，并让主机允许接入"
        alert.addButton(withTitle: "移除")
        alert.addButton(withTitle: "取消")
        guard alert.runModal() == .alertFirstButtonReturn else { return }

        runHostCommand(["--forget-host"]) { [weak self] json in
            guard let self else { return }
            guard let json, (json["ok"] as? Bool) == true else {
                self.notify((json?["error"] as? String) ?? "移除失败")
                return
            }

            self.refreshSettings()
            self.viewerState = ""
            self.viewerStatusText = self.statusWord("notBound")
            self.rebuildMenu()
        }
    }

    // MARK: - 数值与主机取值

    private func number(_ value: Any?) -> Double {
        if let doubleValue = value as? Double { return doubleValue }
        if let intValue = value as? Int { return Double(intValue) }
        if let numberValue = value as? NSNumber { return numberValue.doubleValue }
        return 0
    }

    private func formatNumber(_ value: Double) -> String {
        value == value.rounded() ? String(Int(value)) : String(format: "%.1f", value)
    }

    private func storageLocation(for path: String) -> [String: Any]? {
        storageLocations.first { ($0["path"] as? String) == path }
    }

    @objc private func quit() {
        stopHost()
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { NSApp.terminate(nil) }
    }

    private func notify(_ text: String) {
        // 弹窗前收起菜单，否则菜单会卡在展开状态，看起来像点不动
        statusItem?.menu?.cancelTracking()
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
            hostNodeId = ""
            return
        }

        purpose = (json["DeploymentPreset"] as? String) ?? ""
        hostAddress = (json["LastKnownHostAddress"] as? String) ?? ""
        hostKey = (json["LastKnownHostWebAccessKey"] as? String) ?? ""
        hostNodeId = (json["LastKnownHostNodeId"] as? String) ?? ""
        let locations = json["StorageLocations"] as? [[String: Any]]
        let ordered = (locations ?? [])
            .filter { (($0["Path"] as? String) ?? "").isEmpty == false }
            .sorted { (($0["Priority"] as? Int) ?? 99) < (($1["Priority"] as? Int) ?? 99) }
        storagePaths = ordered.compactMap { $0["Path"] as? String }
        storagePath = storagePaths.first ?? ""
    }

    // MARK: - 主机命令行

    /// 运行主机自带命令并读回 JSON。容量、主机发现与状态词都在核心实现里，
    /// 壳只做展示，不再自己算容量或自己编状态词。
    private func runHostCommand(_ arguments: [String], completion: @escaping ([String: Any]?) -> Void) {
        guard let executable = hostExecutable() else {
            completion(nil)
            return
        }

        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        let output = Pipe()
        process.standardOutput = output
        process.standardError = Pipe()
        process.terminationHandler = { _ in
            let data = output.fileHandleForReading.readDataToEndOfFile()
            let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            DispatchQueue.main.async { completion(json) }
        }
        do {
            try process.run()
        } catch {
            DispatchQueue.main.async { completion(nil) }
        }
    }

    /// 同步版本，只给 --status 排查输出用；超时就放弃，避免命令行没按预期退出时卡住菜单
    private func runHostCommandSync(_ arguments: [String], timeout: TimeInterval = 30) -> [String: Any]? {
        guard let executable = hostExecutable() else { return nil }
        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        let output = Pipe()
        process.standardOutput = output
        process.standardError = Pipe()
        do {
            try process.run()
        } catch {
            return nil
        }
        let deadline = Date().addingTimeInterval(timeout)
        while process.isRunning && Date() < deadline {
            usleep(50_000)
        }
        if process.isRunning {
            process.terminate()
            return nil
        }
        let data = output.fileHandleForReading.readDataToEndOfFile()
        return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
    }

    private func refreshStorageSummary(force: Bool = false) {
        guard force || Date().timeIntervalSince(lastStorageSummary) > 30 else { return }
        lastStorageSummary = Date()
        runHostCommand(["--storage-summary"]) { [weak self] json in
            guard let self, let locations = json?["locations"] as? [[String: Any]] else { return }
            self.storageLocations = locations
            self.rebuildMenu()
        }
    }

    private func loadStorageSummarySync() {
        guard let json = runHostCommandSync(["--storage-summary"]),
              let locations = json["locations"] as? [[String: Any]] else { return }
        storageLocations = locations
    }

    /// 查看端状态：探测与措辞都由核心决定；state 非空时只取词表，不碰网络
    private func refreshViewerStatus(state: String? = nil) {
        guard purpose == "ViewerClient" else { return }
        viewerStatusGeneration += 1
        let generation = viewerStatusGeneration
        var arguments = ["--viewer-status"]
        if let state { arguments += ["--state", state] }
        runHostCommand(arguments) { [weak self] json in
            guard let self, generation == self.viewerStatusGeneration else { return }
            self.applyViewerStatus(json)
        }
    }

    private func loadViewerStatusSync() {
        applyViewerStatus(runHostCommandSync(["--viewer-status"]))
    }

    private func applyViewerStatus(_ json: [String: Any]?) {
        guard let json else { return }
        if let words = json["texts"] as? [String: String] { viewerStatusWords = words }
        guard let text = json["text"] as? String, !text.isEmpty else { return }
        viewerStatusText = text
        viewerState = (json["state"] as? String) ?? ""
        rebuildMenu()
    }

    private func statusWord(_ key: String) -> String { viewerStatusWords[key] ?? "" }

    /// 主机发现较慢（要扫整个网段），按需刷新并把结果缓存在壳里
    private func refreshDiscoveredHosts(force: Bool = false) {
        guard purpose == "ViewerClient", !hostSearchInFlight else { return }
        guard force || Date().timeIntervalSince(lastHostSearch) > 60 else { return }
        hostSearchInFlight = true
        lastHostSearch = Date()
        refreshViewerStatus(state: "searching")
        runHostCommand(["--list-hosts"]) { [weak self] json in
            guard let self else { return }
            self.hostSearchInFlight = false
            self.discoveredHosts = (json?["hosts"] as? [[String: Any]]) ?? []
            self.refreshViewerStatus()
            self.rebuildMenu()
        }
    }

    private func loadDiscoveredHostsSync() {
        guard let json = runHostCommandSync(["--list-hosts"]),
              let hosts = json["hosts"] as? [[String: Any]] else { return }
        discoveredHosts = hosts
    }

    private func describeHost(_ host: [String: Any]) -> String {
        let name = (host["nodeName"] as? String) ?? ""
        let address = (host["address"] as? String) ?? ""
        return name.isEmpty ? address : "\(name)（\(address)）"
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
