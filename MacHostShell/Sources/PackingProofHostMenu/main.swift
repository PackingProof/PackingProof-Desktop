// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 PackingProof contributors

import AppKit
import Foundation
import SwiftUI

/// 菜单栏壳：菜单里直接设置用途、保存位置与开机自启；
/// 业务逻辑一律交给 .NET 主机，壳只做展示与转发。
final class HostShell: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem?
    private var mainWindow: NSWindow?
    private let model = AppStateModel()
    private var refreshTimer: Timer?
    private var launchedHosts: [Process] = []
    private var lastLaunchAttempt = Date.distantPast

    private var purpose = ""
    private var storagePath = ""
    private var autostartInstalled = false
    private var hostAddress = ""
    private var hostKey = ""
    private var hostNodeId = ""
    private var hostNodeName = ""
    private var storagePaths: [String] = []
    private var hostProblem = ""
    /// 刚发起过启动、还没开始监听：界面上说"启动中"而不是"未运行"
    private var hostLaunching = false
    /// 界面内提示的代次：新的提示会顶掉旧的自动消失
    private var bannerGeneration = 0
    /// 检查更新：启动后查一次，之后每天一次（只提示，不下载不替换）
    private var lastUpdateCheck = Date.distantPast
    private var updateDownloadUrl = ""
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
    private var hostDevices: [[String: Any]] = []
    private var lastHostDeviceRefresh = Date.distantPast
    private var lastStorageSummary = Date.distantPast
    private var discoveredHosts: [[String: Any]] = []
    private var hostSearchInFlight = false
    private var lastHostSearch = Date.distantPast

    private var viewerConnected: Bool { viewerState == "online" }

    /// 查看端与保存主机是两种功能：保存位置、容量上限、开机自启只对保存主机有意义
    private var isViewer: Bool { purpose == "ViewerClient" }

    private let port = Int(ProcessInfo.processInfo.environment["PACKINGPROOF_HOST_PORT"] ?? "") ?? 5280

    // MARK: - 生命周期

    func applicationDidFinishLaunching(_ notification: Notification) {
        if CommandLine.arguments.contains("--status") {
            refreshSettings()
            refreshAutostart()
            let serving = probeStatus(URL(string: "http://127.0.0.1:\(port)/api/node-info")!) == 200
            hostServing = serving
            if purpose == "MobileBackupHost" && !serving { hostProblem = readHostFailure() ?? "" }
            if isViewer {
                loadViewerStatusSync()
                if CommandLine.arguments.contains("--hosts") { loadDiscoveredHostsSync() }
            } else {
                loadStorageSummarySync()
            }
            print("用途: \(statusText)")
            print("本机主机服务: \(serving ? "运行中" : "未运行")")
            if isViewer {
                print("查看端状态: \(viewerState.isEmpty ? "—" : viewerState) \(viewerStatusText)")
            } else {
                for line in storageSummaryLines() { print(line) }
            }
            if CommandLine.arguments.contains("--hosts") && isViewer {
                let rows = hostRows()
                print("发现主机: \(rows.isEmpty ? statusWord("notFound") : rows.map { describeHost($0) }.joined(separator: "、"))")
            }
            // 查看端只要记住了一台主机就能点：未授权时点它会去申请接入
            print("回放项: \(isViewer ? (hostAddress.isEmpty ? "禁用" : "可用") : (serving ? "可用" : "禁用"))")
            exit(0)
        }

        if CommandLine.arguments.contains("--dump-menu") {
            refreshSettings()
            refreshAutostart()
            if isViewer {
                loadViewerStatusSync()
                if CommandLine.arguments.contains("--hosts") { loadDiscoveredHostsSync() }
            } else {
                loadStorageSummarySync()
            }
            print(dumpMenu())
            exit(0)
        }

        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        applyIcon(to: item)
        statusItem = item
        setUpModelActions()
        setUpApplicationMenu()
        // 常规窗口应用：有 Dock 图标，主窗口是主要入口，菜单栏图标保留做快捷操作
        NSApp.setActivationPolicy(.regular)
        openMainWindow()

        // 首次启动还没选过用途：让窗口直接把"选择这台电脑的用途"摆出来
        refreshSettings()
        if purpose.isEmpty {
            model.needsPurposeSetup = true
            model.requestPurposeChooser()
        }
        refreshUpdateStatus(force: true)

        refreshTimer = Timer.scheduledTimer(withTimeInterval: 10, repeats: true) { [weak self] _ in
            self?.refresh()
        }
        refresh()
    }

    /// 退出即停止：关掉程序就把自己拉起的主机一起停掉，不留没人管的进程
    func applicationWillTerminate(_ notification: Notification) { stopHost() }

    /// 主窗口关掉不等于退出：保存主机还要继续跑，菜单栏也还在
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    /// 点 Dock 图标重新打开主窗口
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag { openMainWindow() }
        return true
    }

    // MARK: - 主窗口与应用菜单

    private func openMainWindow() {
        if mainWindow == nil {
            let hosting = NSHostingController(rootView: MainWindowView(model: model))
            let window = NSWindow(contentViewController: hosting)
            window.title = "PackingProof"
            window.styleMask = [.titled, .closable, .miniaturizable, .resizable]
            // 高度按 MacViewer 的窗口比例来：列表自己滚动，不要把窗口撑满屏
            window.setContentSize(NSSize(width: 560, height: 360))
            window.isReleasedWhenClosed = false
            window.center()
            mainWindow = window
        }

        NSApp.activate(ignoringOtherApps: true)
        mainWindow?.makeKeyAndOrderFront(nil)
    }

    @objc private func openMainWindowAction() { openMainWindow() }

    /// ⌘, 打开主界面上的设置页
    @objc private func openSettingsAction() {
        openMainWindow()
        model.requestSettings()
    }

    /// ⌘U 打开用途选择（与主界面右上角"切换用途"是同一个界面）
    @objc private func openPurposeChooserAction() {
        openMainWindow()
        model.requestPurposeChooser()
    }

    /// 常规窗口应用需要的应用菜单：没有它菜单栏上会是一片空白
    private func setUpApplicationMenu() {
        let appName = "PackingProof"
        let mainMenu = NSMenu()

        let appMenuItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(
            withTitle: "关于 \(appName)",
            action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)),
            keyEquivalent: "")
        appMenu.addItem(.separator())
        let openItem = NSMenuItem(
            title: "打开主界面",
            action: #selector(openMainWindowAction),
            keyEquivalent: "0")
        openItem.target = self
        appMenu.addItem(openItem)
        let settingsItem = NSMenuItem(
            title: "设置…",
            action: #selector(openSettingsAction),
            keyEquivalent: ",")
        settingsItem.target = self
        appMenu.addItem(settingsItem)
        let purposeItem = NSMenuItem(
            title: "切换用途…",
            action: #selector(openPurposeChooserAction),
            keyEquivalent: "u")
        purposeItem.target = self
        appMenu.addItem(purposeItem)
        appMenu.addItem(.separator())
        appMenu.addItem(
            withTitle: "隐藏 \(appName)",
            action: #selector(NSApplication.hide(_:)),
            keyEquivalent: "h")
        appMenu.addItem(.separator())
        appMenu.addItem(
            withTitle: "退出 \(appName)",
            action: #selector(NSApplication.terminate(_:)),
            keyEquivalent: "q")
        appMenuItem.submenu = appMenu
        mainMenu.addItem(appMenuItem)

        let windowMenuItem = NSMenuItem()
        let windowMenu = NSMenu(title: "窗口")
        windowMenu.addItem(
            withTitle: "最小化",
            action: #selector(NSWindow.performMiniaturize(_:)),
            keyEquivalent: "m")
        let reopenItem = NSMenuItem(
            title: "主界面",
            action: #selector(openMainWindowAction),
            keyEquivalent: "")
        reopenItem.target = self
        windowMenu.addItem(reopenItem)
        windowMenuItem.submenu = windowMenu
        mainMenu.addItem(windowMenuItem)

        NSApp.mainMenu = mainMenu
    }

    /// 窗口按钮全部转给菜单栏壳里已有的实现，逻辑与状态只有一份
    private func setUpModelActions() {
        model.actions = AppStateModel.Actions(
            startupRefresh: { [weak self] in await self?.startupRefreshAsync() },
            search: { [weak self] in await self?.searchAsync() },
            clearRememberedHost: { [weak self] in await self?.clearRememberedHostAsync() },
            openWebPlayback: { [weak self] in await self?.openWebPlaybackAsync() },
            connectManually: { [weak self] input in await self?.connectManuallyAsync(input) },
            switchPurpose: { [weak self] viewer in await self?.switchPurposeAsync(viewer: viewer) },
            confirmPurpose: { [weak self] viewer in
                viewer ? self?.useViewerPurpose() : self?.useHostPurpose()
            },
            openStorageLocation: { [weak self] path in self?.openStorageLocation(path: path) },
            setCapacity: { [weak self] path, gigabytes in
                self?.applyStorageLimit(path: path, byCapacity: true, gigabytes: gigabytes)
            },
            setReserve: { [weak self] path, gigabytes in
                self?.applyStorageLimit(path: path, byCapacity: false, gigabytes: gigabytes)
            },
            addDisk: { [weak self] path in self?.addStorageDisk(root: path) },
            toggleAutostart: { [weak self] in self?.toggleAutostart() },
            openLogs: { [weak self] in self?.openLogs() },
            checkUpdate: { [weak self] in await self?.checkUpdateAsync() },
            openUpdatePage: { [weak self] in self?.openUpdatePage() })
    }

    // MARK: - 检查更新（只提示，不下载不替换）

    /// 启动后查一次，之后每天一次；判重与版本比较都在核心的 UpdateCheckService 里
    private func refreshUpdateStatus(force: Bool = false) {
        guard force || Date().timeIntervalSince(lastUpdateCheck) > 24 * 60 * 60 else { return }
        lastUpdateCheck = Date()

        runHostCommand(["--check-update"], timeout: 30) { [weak self] json in
            guard let self, let json, (json["ok"] as? Bool) == true else { return }

            let hasUpdate = (json["hasUpdate"] as? Bool) ?? false
            self.updateDownloadUrl = (json["downloadUrl"] as? String) ?? ""
            DispatchQueue.main.async {
                self.model.updateAvailable = hasUpdate
                self.model.updateVersion = (json["latestVersion"] as? String) ?? ""
                self.model.updateTitle = (json["title"] as? String) ?? ""
                self.model.updateDownloadUrl = self.updateDownloadUrl
                if hasUpdate { self.model.updateDismissed = false }
                self.rebuildMenu()
            }
        }
    }

    private func checkUpdateAsync() async {
        lastUpdateCheck = Date.distantPast
        refreshUpdateStatus(force: true)
    }

    @objc private func checkUpdateAction() { Task { await checkUpdateAsync() } }

    /// 打开下载页：优先用发布页地址；没有就退到 GitHub 的 releases 页
    private func openUpdatePage() {
        let fallback = "https://github.com/PackingProof/PackingProof-Desktop/releases/latest"
        let target = updateDownloadUrl.isEmpty ? fallback : updateDownloadUrl
        guard let url = URL(string: target) else { return }
        NSWorkspace.shared.open(url)
    }

    @objc private func openUpdatePageAction() { openUpdatePage() }

    // MARK: - 窗口动作（与菜单栏走同一套实现）

    private func runHostCommandAsync(
        _ arguments: [String],
        timeout: TimeInterval = 60
    ) async -> [String: Any]? {
        await withCheckedContinuation { continuation in
            runHostCommand(arguments, timeout: timeout) { json in
                continuation.resume(returning: json)
            }
        }
    }

    /// 壳里的搜索是异步的：等它把状态收干净，窗口的转圈才停得下来
    private func waitForHostSearch() async {
        var waited = 0.0
        while hostSearchInFlight && waited < 60 {
            try? await Task.sleep(nanoseconds: 200_000_000)
            waited += 0.2
        }
    }

    private func startupRefreshAsync() async {
        refresh()
        pushStateToModel()
    }

    private func searchAsync() async {
        refreshViewerStatus(state: "searching")
        refreshDiscoveredHosts(force: true, announce: true)
        await waitForHostSearch()
        pushStateToModel()
    }

    private func clearRememberedHostAsync() async {
        _ = await runHostCommandAsync(["--forget-host"])
        refreshSettings()
        viewerState = ""
        viewerStatusText = statusWord("notBound")
        showBanner("已忘记当前主机，重新搜索后再连接即可")
        rebuildMenu()
        await searchAsync()
    }

    private func openWebPlaybackAsync() async {
        model.isOpeningWeb = true
        openPlayback()
        // 打开浏览器是要等主机那边点允许的长流程，不能让界面一直转圈
        try? await Task.sleep(nanoseconds: 1_200_000_000)
        model.isOpeningWeb = false
        pushStateToModel()
    }

    private func connectManuallyAsync(_ input: String) async -> String? {
        let trimmed = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return "请输入主机地址或连接链接" }
        let response = await runHostCommandAsync(["--select-host", trimmed])
        guard let response, (response["ok"] as? Bool) == true else {
            return (response?["error"] as? String) ?? "地址无法识别"
        }

        refreshSettings()
        refreshViewerStatus(state: "searching")
        rebuildMenu()
        connectSelectedHost()
        return nil
    }

    private func switchPurposeAsync(viewer: Bool) async {
        if viewer {
            useViewerPurpose()
        } else {
            useHostPurpose()
        }
        // 切用途要改配置并停/起主机，等它落定再回填界面
        try? await Task.sleep(nanoseconds: 2_500_000_000)
        refresh()
        pushStateToModel()
    }

    /// 添加磁盘：追加后问一次是否立刻重启主机（主机启动时只读一次录像根目录）。
    /// 磁盘根 + 固定子目录名，与菜单栏"添加磁盘…"完全同一条路径
    private func addStorageDisk(root: String) {
        guard !root.isEmpty else { return }
        let path = (root as NSString).appendingPathComponent("快递打包视频")
        applySettings(addStoragePath: path, afterApply: { [weak self] in
            // 保存主机启动时只读一次录像根目录，新增磁盘后自动重启，不再问一遍
            self?.restartHost()
            self?.showBanner("已添加保存位置 \(path)，正在重启主机")
        })
    }

    /// 把壳里唯一那份状态灌给窗口：每次重建菜单都同步一次
    private func pushStateToModel() {
        let hosts = hostRows().map { host in
            DiscoveredHost(
                nodeId: (host["nodeId"] as? String) ?? "",
                nodeName: (host["nodeName"] as? String) ?? "",
                address: (host["address"] as? String) ?? "")
        }
        // 与 MacViewer 一样：记住哪台就选哪台；只发现一台时直接选中
        let selected = hostNodeId.isEmpty
            ? (hosts.count == 1 ? hosts[0].id : nil)
            : hosts.first { $0.nodeId == hostNodeId }?.id

        model.hosts = hosts
        model.status = viewerStatusText.isEmpty ? statusWord("notBound") : viewerStatusText
        model.isSearching = hostSearchInFlight
        model.isViewer = isViewer
        model.selectedHostId = selected
        model.onlineNodeIds = (viewerConnected && !hostNodeId.isEmpty) ? [hostNodeId] : []
        model.hostRunning = hostServing
        model.hostStatusText = hostServing
            ? "运行中"
            : (hostProblem.isEmpty ? (isHostLaunching ? "启动中" : "未运行") : hostProblem)
        model.hostLaunching = isHostLaunching
        model.appVersion = appVersion
        model.needsPurposeSetup = purpose.isEmpty
        model.storePaths = storagePaths
        model.autostartInstalled = autostartInstalled
        model.devices = hostDevices.map { device in
            DeviceItem(
                nodeId: (device["nodeId"] as? String) ?? "",
                name: (device["nodeName"] as? String) ?? "",
                typeText: (device["deviceType"] as? String)?.lowercased() == "mobile"
                    ? "手机录像设备"
                    : "电脑录像设备",
                address: (device["address"] as? String) ?? "",
                online: (device["online"] as? Bool) ?? false,
                todayBackupCount: (device["todayBackupCount"] as? Int) ?? 0)
        }
        model.disks = mountedVolumes().map { volume in
            let root = volume.path
            let prefix = root.hasSuffix("/") ? root : root + "/"
            let used = storagePaths.contains { $0 == root || $0.hasPrefix(prefix) }
            return DiskItem(path: root, name: volume.lastPathComponent, isUsed: used)
        }
        model.storages = storageLocations.compactMap { location in
            guard let path = location["path"] as? String else { return nil }
            return StorageItem(
                path: path,
                name: (location["displayName"] as? String) ?? path,
                available: (location["available"] as? Bool) ?? false,
                capacityKnown: (location["capacityKnown"] as? Bool) ?? false,
                capacityGB: number(location["capacityGB"]),
                reserveGB: number(location["reserveGB"]),
                recommendedReserveGB: number(location["recommendedReserveGB"]))
        }
    }

    // MARK: - 状态

    private func refresh() {
        refreshSettings()
        refreshAutostart()
        refreshStorageSummary()
        refreshViewerStatus()
        refreshDiscoveredHosts()
        refreshUpdateStatus()
        guard let url = URL(string: "http://127.0.0.1:\(port)/api/node-info") else { return }
        var request = URLRequest(url: url)
        request.timeoutInterval = 3
        URLSession.shared.dataTask(with: request) { [weak self] _, response, _ in
            let serving = (response as? HTTPURLResponse)?.statusCode == 200
            DispatchQueue.main.async {
                guard let self else { return }
                self.hostServing = serving
                if serving { self.hostLaunching = false }
                self.viewerRunning = self.launchedHosts.contains { $0.isRunning } && !serving
                if self.purpose == "MobileBackupHost" && !serving {
                    self.hostProblem = self.readHostFailure() ?? self.hostProblem
                    // 只有保存主机才需要在后台常驻；查看端由用户显式点开回放
                    self.ensureHostRunning()
                } else if serving {
                    self.hostProblem = ""
                }
                self.refreshHostDevices()
                self.rebuildMenu()
            }
        }.resume()
    }

    /// 用途项自己就是状态位：菜单不再单占一行显示"当前用途"
    private var hostPurposeTitle: String {
        // 只在当前就是保存主机时才带状态，用户不必去翻日志
        guard purpose == "MobileBackupHost" else { return "保存主机" }
        if !hostProblem.isEmpty { return "保存主机未启动：\(hostProblem)" }
        // 刚点过启动、还没开始监听：说"启动中"，不要说"未运行"
        return isHostLaunching ? "保存主机（启动中）" : "保存主机"
    }

    /// 主机是否正在启动：刚发起过启动、还没监听、也还没报错
    private var isHostLaunching: Bool {
        guard purpose == "MobileBackupHost", !hostServing, hostProblem.isEmpty else { return false }
        // 超过两分钟还没起来也不再显示"启动中"，避免一直骗用户
        return hostLaunching && Date().timeIntervalSince(lastLaunchAttempt) < 120
    }

    private var viewerPurposeTitle: String {
        guard isViewer, !viewerStatusText.isEmpty else { return "查看端" }
        return "查看端（\(viewerStatusText)）"
    }

    /// 排查用的单行状态（--status 输出）
    private var statusText: String {
        if isViewer { return viewerPurposeTitle }
        if purpose.isEmpty { return "未启动" }
        return hostPurposeTitle
    }

    /// 主机没在跑就把它拉起来；失败重试间隔 30 秒，避免配置有问题时反复拉起
    private func ensureHostRunning() {
        guard Date().timeIntervalSince(lastLaunchAttempt) > 30 else { return }
        lastLaunchAttempt = Date()
        startHostProcess()
    }

    /// 启动保存主机：装了开机自启就交给 launchd（壳再拉一个会两个主机抢同一个端口，
    /// 抢不到的那个被 launchd 反复重启），托管不可用时退回壳直接拉起
    private func startHostProcess() {
        hostLaunching = true
        if autostartInstalled, restartLaunchAgent() { return }
        runHost(arguments: ["--no-browser", "--service"])
    }

    // MARK: - 菜单

    private func rebuildMenu() {
        let menu = NSMenu()
        menu.addItem(actionItem("打开主界面", #selector(openMainWindowAction)))
        menu.addItem(.separator())
        // 状态直接挂在用途项上：已经显示用途了，不再单占一行
        let hostItem = actionItem(hostPurposeTitle, #selector(useHostPurpose))
        hostItem.state = purpose == "MobileBackupHost" ? .on : .off
        menu.addItem(hostItem)

        let viewerItem = actionItem(viewerPurposeTitle, #selector(useViewerPurpose))
        viewerItem.state = purpose == "ViewerClient" ? .on : .off
        menu.addItem(viewerItem)
        menu.addItem(.separator())

        // 保存位置、容量、磁盘、开机自启、日志都在主界面的"设置"里，
        // 菜单栏只留最常用的动作，不再堆配置项
        if isViewer {
            // 查看端这里是"要连哪台主机"，不是本机用途，所以叫连接主机
            let hostsItem = NSMenuItem(title: "连接主机…", action: nil, keyEquivalent: "")
            hostsItem.submenu = buildHostMenu()
            menu.addItem(hostsItem)
        }

        let playbackItem = actionItem("打开网页回放", #selector(openPlayback))
        // 查看端只要记住了一台主机就允许点：还没拿到主机允许时，点它就是去申请接入
        playbackItem.isEnabled = isViewer ? !hostAddress.isEmpty : hostServing
        menu.addItem(playbackItem)
        if model.updateAvailable && !model.updateDismissed {
            menu.addItem(actionItem("有新版本 \(model.updateVersion)…", #selector(openUpdatePageAction)))
        }
        menu.addItem(actionItem("检查更新", #selector(checkUpdateAction)))
        menu.addItem(actionItem("设置…", #selector(openSettingsAction)))
        menu.addItem(.separator())
        menu.addItem(actionItem("退出", #selector(quit)))

        pushStateToModel()
        updateWindowTitle()
        statusItem?.menu = menu
    }

    /// 查看端：把发现到的主机做成子菜单，标出当前那台，点选即切换，可移除
    /// 可选主机 = 这次扫到的 + 已经连上的那台（扫不到也保留，避免菜单和回放状态互相矛盾）
    private func hostRows() -> [[String: Any]] {
        // 已经连上的主机即使这次没扫到也要列出来：否则菜单会显示"没有找到主机"，
        // 而"打开网页回放"其实照样能用，前后自相矛盾
        var rows = discoveredHosts
        if !hostNodeId.isEmpty, !hostAddress.isEmpty,
           !rows.contains(where: { ($0["nodeId"] as? String) == hostNodeId }) {
            rows.insert(
                ["nodeId": hostNodeId, "nodeName": hostNodeName, "address": hostAddress],
                at: 0)
        }
        return rows
    }

    private func buildHostMenu() -> NSMenu {
        let menu = NSMenu()
        let rows = hostRows()
        // 有主机就照常列出来：后台重扫期间不能把已知主机换成"正在搜索"，
        // 否则已经连上的主机看起来像丢了。搜索状态由"查看端"那一行体现
        if rows.isEmpty {
            let word = statusWord(hostSearchInFlight ? "searching" : "notFound")
            if !word.isEmpty { menu.addItem(disabledItem(word)) }
        } else {
            for host in rows {
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
        lines.append("· 打开主界面")
        lines.append("· —")
        lines.append("· \(hostPurposeTitle) \(purpose == "MobileBackupHost" ? "✓" : "")")
        lines.append("· \(viewerPurposeTitle) \(purpose == "ViewerClient" ? "✓" : "")")
        lines.append("· —")
        if isViewer {
            lines.append("· 连接主机…：\(hostMenuSummary())")
        }
        let playback = isViewer
            ? (viewerConnected ? "已连接 \(hostAddress)" : "禁用（\(viewerStatusText)）")
            : (hostServing ? "本机" : "未启动，禁用")
        lines.append("· 打开网页回放（\(playback)）")
        lines.append("· 设置…（主界面里的设置页）")
        lines.append("· —")
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
        let rows = hostRows()
        if rows.isEmpty { return statusWord(hostSearchInFlight ? "searching" : "notFound") }
        return rows.map { host in
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
        applySettings(purpose: "host", autostart: nil,
                      afterApply: { [weak self] in self?.startHostAfterPurposeSwitch() })
    }

    @objc private func useViewerPurpose() {
        guard purpose != "ViewerClient" else { return }
        applySettings(purpose: "viewer", autostart: nil,
                      afterApply: { [weak self] in self?.stopHostAfterPurposeSwitch() })
    }

    /// 切到查看端必须把本机保存主机真正停掉：不停的话端口还占着、
    /// 本机也仍被自己当成一台可连的主机，紧接着就会弹出"自己请求连接自己"
    private func stopHostAfterPurposeSwitch() {
        hostProblem = ""
        stopHost()
        stopUntrackedHostProcesses()

        refresh()
    }

    /// 切回保存主机：马上拉起来；自启由 launchd 托管时交给它重启，避免壳再拉起第二个
    private func startHostAfterPurposeSwitch() {
        hostProblem = ""
        lastLaunchAttempt = Date()
        startHostProcess()

        DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in self?.refresh() }
    }

    /// 停掉同一个包里的主机进程：换用途时端口还被旧进程占着，
    /// 查看端就会把本机当成一台保存主机连上去
    private func stopUntrackedHostProcesses() {
        guard let executable = hostExecutable() else { return }
        let path = executable.path
        // 只允许对我们自己的包内主机进程下手
        guard path.contains("/Contents/MacOS/Host/") else { return }
        let kill = Process()
        kill.executableURL = URL(fileURLWithPath: "/usr/bin/pkill")
        kill.arguments = ["-f", path]
        try? kill.run()
        kill.waitUntilExit()
    }

    private var launchdServiceTarget: String { "gui/\(getuid())/com.packingproof.host" }

    private static var autostartPlistURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/com.packingproof.host.plist")
    }

    /// 重新加载自启任务（bootout + bootstrap）并按 RunAtLoad 立刻拉起保存主机。
    /// 不用 kickstart：launchd 对刚退出的任务有 60 秒节流，kickstart 会被拖到一分钟以后，
    /// 同步等它还会把菜单卡死。返回 false 表示托管不可用，调用方要自己拉起主机
    private func restartLaunchAgent() -> Bool {
        guard FileManager.default.fileExists(atPath: Self.autostartPlistURL.path) else { return false }
        if isLaunchAgentLoaded() {
            _ = runLaunchctl(["bootout", launchdServiceTarget])
        }
        return runLaunchctl(["bootstrap", "gui/\(getuid())", Self.autostartPlistURL.path]) == 0
    }

    /// 任务是否已被 launchd 加载
    private func isLaunchAgentLoaded() -> Bool {
        runLaunchctl(["print", launchdServiceTarget]) == 0
    }

    @discardableResult
    private func runLaunchctl(_ arguments: [String], wait: Bool = true) -> Int32 {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        process.arguments = arguments
        let output = Pipe()
        if wait {
            process.standardOutput = output
            process.standardError = output
        }
        guard (try? process.run()) != nil else { return -1 }
        guard wait else { return 0 }
        // launchctl 输出很短，读掉以免管道写满卡住子进程
        _ = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        return process.terminationStatus
    }

    @objc private func toggleAutostart() {
        if autostartInstalled {
            applySettings(autostart: false,
                          afterApply: { [weak self] in
                              // 取消托管后由壳继续看着主机，别让录像主机跟着一起停掉
                              self?.startHostProcess()
                              self?.showBanner("已取消开机自启")
                          })
            return
        }

        // 先让壳自己那个主机退出，再装自启：否则 launchd 拉起的那个抢不到端口，
        // 要等一个节流周期才能恢复
        stopHost()
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in
            self?.applySettings(autostart: true,
                                afterApply: { [weak self] in
                                    // 装 plist 时 launchd 已经按 RunAtLoad 把主机拉起来了，
                                    // 这里不要再 kickstart，否则刚起来的进程会被顶掉
                                    self?.refresh()
                                    self?.showBanner("已注册开机自启")
                                })
        }
    }

    /// 已挂载的磁盘：与桌面端一致，选磁盘分区，路径用默认子目录
    private func mountedVolumes() -> [URL] {
        let keys: [URLResourceKey] = [.volumeNameKey, .volumeIsBrowsableKey, .volumeIsInternalKey]
        let volumes = FileManager.default.mountedVolumeURLs(
            includingResourceValuesForKeys: keys,
            options: [.skipHiddenVolumes]) ?? []
        return volumes.filter { $0.path != "/" }
    }

    /// 改配置一律走主机命令行：查看端不常驻 HTTP 服务，
    /// 走本地设置接口在查看端会直接失败（切不回保存主机）
    private func applySettings(
        purpose newPurpose: String? = nil,
        autostart: Bool? = nil,
        addStoragePath: String? = nil,
        success: String? = nil,
        afterApply: (() -> Void)? = nil) {
        var arguments: [String] = []
        if let newPurpose { arguments += ["--set-purpose", newPurpose] }
        if let addStoragePath { arguments += ["--add-storage", addStoragePath] }
        if let autostart { arguments += ["--set-autostart", autostart ? "on" : "off"] }
        guard !arguments.isEmpty else { return }

        runHostCommand(arguments) { [weak self] json in
            DispatchQueue.main.async {
                guard let self else { return }
                guard let json, (json["ok"] as? Bool) == true else {
                    let message = (json?["error"] as? String) ?? "设置失败"
                    self.showBanner("设置未生效：\(message)", isError: true)
                    return
                }

                self.refreshSettings()
                self.refreshAutostart()
                if self.isViewer {
                    self.refreshViewerStatus()
                    self.refreshDiscoveredHosts(force: true, announce: true)
                } else {
                    self.refreshStorageSummary(force: true)
                }
                self.rebuildMenu()
                if let afterApply {
                    // 用途切换要先把进程收拾干净，提示由 afterApply 自己给（内容更准）
                    afterApply()
                } else if let success {
                    self.showBanner(success)
                }
            }
        }
    }

    private func restartHost() {
        hostLaunching = true
        // 装了开机自启就交给 launchd 重新加载（见 startHostProcess），不要自己停，
        // 否则托管关系被顶掉、launchd 又会拉起第二个主机
        if !autostartInstalled {
            stopHost()
        }

        lastLaunchAttempt = Date()
        hostProblem = ""
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in
            guard let self else { return }
            self.startHostProcess()
            DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in
                guard let self else { return }
                self.refresh()
                if self.purpose == "MobileBackupHost" && !self.hostServing, let reason = self.readHostFailure() {
                    self.showBanner("主机没有起来：\(reason)", isError: true)
                }
            }
        }
    }

    // MARK: - 其它菜单动作

    @objc private func openPlayback() {
        if isViewer {
            // 查看端：打开已连接的主机，而不是本机地址
            guard !hostAddress.isEmpty else {
                showBanner("还没有连接保存主机。请先在“连接主机…”里选一台")
                return
            }
            if viewerConnected {
                openHostPlaybackUrl()
            } else {
                // 还没拿到主机允许（或主机离线）：和桌面端查看窗口一样，
                // 点"打开网页回放"就去走一次接入申请，而不是把按钮禁掉让人无从下手
                connectSelectedHost()
            }
            return
        }

        var url = "http://127.0.0.1:\(port)/"
        if let key = readAccessKey() { url += "?key=\(key)" }
        NSWorkspace.shared.open(URL(string: url)!)
    }

    private func openHostPlaybackUrl() {
        var hostUrl = hostAddress
        if !hostKey.isEmpty {
            hostUrl += (hostAddress.contains("?") ? "&" : "?") + "key=\(hostKey)"
        }
        if let url = URL(string: hostUrl) { NSWorkspace.shared.open(url) }
    }

    @objc private func openLogs() {
        NSWorkspace.shared.open(Self.logDirectory)
    }

    // MARK: - 保存位置与容量上限

    /// 在 Finder 中打开保存位置；目录还没建出来时退到最近的已有上级目录，避免点了没反应
    private func openStorageLocation(path: String) {
        guard !path.isEmpty else { return }
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

    /// 容量上限与预留都在设置页里就地编辑（不再弹输入框），这里只负责落盘。
    /// 两者写的是配置里同一个预留值，换算规则由核心负责；改完不需要重启主机
    private func applyStorageLimit(path: String, byCapacity: Bool, gigabytes: Double) {
        let option = byCapacity ? "--set-storage-capacity" : "--set-storage-reserve"
        runHostCommand([option, formatNumber(gigabytes), "--storage-path", path]) { [weak self] json in
            guard let self else { return }
            guard let json, (json["ok"] as? Bool) == true else {
                self.showBanner((json?["error"] as? String) ?? "设置未生效", isError: true)
                return
            }

            self.refreshStorageSummary(force: true)
            let capacity = self.formatNumber(self.number(json["capacityGB"]))
            let reserve = self.formatNumber(self.number(json["reserveGB"]))
            self.showBanner("已更新：容量上限 \(capacity) GB，预留 \(reserve) GB")
        }
    }

    // MARK: - 查看端主机

    /// 切换主机：先记住新主机，再走一次查看端接入流程（申请接入 → 记住密钥 → 打开网页）
    @objc private func selectHost(_ sender: NSMenuItem) {
        guard let host = sender.representedObject as? [String: Any],
              let address = host["address"] as? String else { return }
        connectHost(address: address,
                    nodeId: (host["nodeId"] as? String) ?? "",
                    nodeName: (host["nodeName"] as? String) ?? "")
    }

    /// 主窗口按 NodeId 点"连接"：从同一份主机列表里取出那一台
    private func connectHost(nodeId: String) {
        guard let host = hostRows().first(where: { ($0["nodeId"] as? String) == nodeId }),
              let address = host["address"] as? String else { return }
        connectHost(address: address,
                    nodeId: (host["nodeId"] as? String) ?? "",
                    nodeName: (host["nodeName"] as? String) ?? "")
    }

    private func connectHost(address: String, nodeId: String, nodeName: String) {
        runHostCommand(
            ["--select-host", address, "--host-node-id", nodeId, "--host-node-name", nodeName]
        ) { [weak self] json in
            guard let self else { return }
            guard let json, (json["ok"] as? Bool) == true else {
                self.showBanner((json?["error"] as? String) ?? "切换主机失败", isError: true)
                return
            }

            self.refreshSettings()
            // 接入流程要重新发现主机，先把状态切成"正在搜索"，别让菜单停在旧结论上
            self.refreshViewerStatus(state: "searching")
            self.rebuildMenu()
            self.connectSelectedHost()
        }
    }

    /// 接入流程与桌面端一致：发现主机 → 需要时申请接入 → 记住地址与密钥 → 打开回放网页
    /// 不加 --service：第一次连接要在本机弹出"请到主机上点允许"，服务模式会把弹窗吞掉
    private func connectSelectedHost() {
        runHost(arguments: [])
    }

    @objc private func rescanHosts() {
        discoveredHosts = []
        hostSearchInFlight = false
        refreshDiscoveredHosts(force: true, announce: true)
        rebuildMenu()
    }

    @objc private func forgetHost() {
        runHostCommand(["--forget-host"]) { [weak self] json in
            guard let self else { return }
            guard let json, (json["ok"] as? Bool) == true else {
                self.showBanner((json?["error"] as? String) ?? "移除失败", isError: true)
                return
            }

            self.refreshSettings()
            self.viewerState = ""
            self.viewerStatusText = self.statusWord("notBound")
            self.showBanner("已忘记当前主机，重新搜索后再连接即可")
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

    /// 界面内提示，不再弹系统对话框；失败时让 Dock 图标跳一下，避免用户没看到
    private func showBanner(_ text: String, isError: Bool = false) {
        model.banner = text
        model.bannerIsError = isError
        if isError { NSApp.requestUserAttention(.informationalRequest) }
        bannerGeneration += 1
        let generation = bannerGeneration
        DispatchQueue.main.asyncAfter(deadline: .now() + 6) { [weak self] in
            guard let self, generation == self.bannerGeneration else { return }
            self.model.banner = nil
        }
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
            hostNodeName = ""
            return
        }

        purpose = (json["DeploymentPreset"] as? String) ?? ""
        hostAddress = (json["LastKnownHostAddress"] as? String) ?? ""
        hostKey = (json["LastKnownHostWebAccessKey"] as? String) ?? ""
        hostNodeId = (json["LastKnownHostNodeId"] as? String) ?? ""
        hostNodeName = (json["LastKnownHostNodeName"] as? String) ?? ""
        let locations = json["StorageLocations"] as? [[String: Any]]
        let ordered = (locations ?? [])
            .filter { (($0["Path"] as? String) ?? "").isEmpty == false }
            .sorted { (($0["Priority"] as? Int) ?? 99) < (($1["Priority"] as? Int) ?? 99) }
        storagePaths = ordered.compactMap { $0["Path"] as? String }
        storagePath = storagePaths.first ?? ""
    }

    // MARK: - 主机命令行

    /// 命令行调用完成后只允许回调一次（正常结束与超时保护之间抢跑）
    private final class CommandCompletion {
        var finished = false
    }

    /// 运行主机自带命令并读回 JSON。容量、主机发现与状态词都在核心实现里，
    /// 壳只做展示，不再自己算容量或自己编状态词。
    private func runHostCommand(
        _ arguments: [String],
        timeout: TimeInterval = 60,
        completion: @escaping ([String: Any]?) -> Void) {
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
        let state = CommandCompletion()
        process.terminationHandler = { _ in
            let data = output.fileHandleForReading.readDataToEndOfFile()
            let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            DispatchQueue.main.async {
                guard !state.finished else { return }
                state.finished = true
                completion(json)
            }
        }
        do {
            try process.run()
        } catch {
            DispatchQueue.main.async { completion(nil) }
            return
        }

        // 子进程卡住时不能让界面一直停在"正在搜索"
        DispatchQueue.main.asyncAfter(deadline: .now() + timeout) {
            guard !state.finished else { return }
            state.finished = true
            if process.isRunning { process.terminate() }
            completion(nil)
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
        // 查看端不录像也不保存，不去问容量
        guard !isViewer else { return }
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
        guard let json else {
            // 探测没回来时不能停在"正在搜索"上：按有没有记住主机给一个确定的说法
            let bound = !hostNodeId.isEmpty || !hostAddress.isEmpty
            viewerState = bound ? "offline" : "notBound"
            let word = statusWord(bound ? "hostOfflineOrChanged" : "notBound")
            if !word.isEmpty { viewerStatusText = word }
            rebuildMenu()
            return
        }
        if let words = json["texts"] as? [String: String] { viewerStatusWords = words }
        guard let text = json["text"] as? String, !text.isEmpty else { return }
        viewerStatusText = text
        viewerState = (json["state"] as? String) ?? ""
        rebuildMenu()
    }

    private func statusWord(_ key: String) -> String { viewerStatusWords[key] ?? "" }

    /// 主机发现较慢（要扫整个网段），按需刷新并把结果缓存在壳里。
    /// announce 只在用户主动搜索（或还没有连过主机）时为 true：后台每 60 秒静默重扫一次列表，
    /// 不能把已经连上的状态顶成"正在搜索"，否则菜单会一直闪"在搜索"
    private func refreshDiscoveredHosts(force: Bool = false, announce: Bool = false) {
        guard purpose == "ViewerClient", !hostSearchInFlight else { return }
        guard force || Date().timeIntervalSince(lastHostSearch) > 60 else { return }
        hostSearchInFlight = true
        lastHostSearch = Date()
        if announce || (hostNodeId.isEmpty && hostAddress.isEmpty) {
            refreshViewerStatus(state: "searching")
        }
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

    /// 版本号取自 .app 的 Info.plist：与电脑端一样把版本显示在窗口标题上
    private var appVersion: String {
        (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? ""
    }

    private func updateWindowTitle() {
        let role = isViewer ? "查看端" : "保存主机"
        mainWindow?.title = appVersion.isEmpty
            ? "PackingProof \(role)"
            : "PackingProof \(role) \(appVersion)"
    }

    /// 保存主机角色下列出连进来的录像设备（与电脑端"订单联动设备"同一份数据）
    private func refreshHostDevices() {
        guard !isViewer, hostServing else {
            if !hostDevices.isEmpty {
                hostDevices = []
                pushStateToModel()
            }
            return
        }
        guard Date().timeIntervalSince(lastHostDeviceRefresh) > 3 else { return }
        lastHostDeviceRefresh = Date()

        guard var components = URLComponents(
            string: "http://127.0.0.1:\(port)/api/recording-devices") else { return }
        // 带上 known：配对过但还没上传过录像的设备也要列出来，
        // 否则"已经连上、还没上传"的设备在列表里根本看不到
        components.queryItems = [URLQueryItem(name: "scope", value: "known")]
        if let key = readAccessKey() {
            components.queryItems?.append(URLQueryItem(name: "key", value: key))
        }
        guard let url = components.url else { return }

        var request = URLRequest(url: url)
        request.timeoutInterval = 5
        URLSession.shared.dataTask(with: request) { [weak self] data, _, _ in
            let devices = data
                .flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
                .flatMap { $0["devices"] as? [[String: Any]] } ?? []
            DispatchQueue.main.async {
                guard let self, !self.isViewer, self.hostServing else { return }
                self.hostDevices = devices
                self.pushStateToModel()
            }
        }.resume()
    }

    /// 主机启动失败时，日志最后一行就是原因
    /// 自启托管时输出写在 launchd 的两个日志里，壳自己拉起时写在 host.log，三处都要看
    private func readHostFailure() -> String? {
        let logNames = ["host.log", "host-launchd.err.log", "host-launchd.out.log"]
        let logs = logNames
            .map { Self.logDirectory.appendingPathComponent($0) }
            .compactMap { url -> (url: URL, modified: Date)? in
                guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
                      let modified = attributes[.modificationDate] as? Date
                else { return nil }
                return (url, modified)
            }
            // 最近写过的那个日志才代表这一次启动的结果
            .sorted { $0.modified > $1.modified }

        for log in logs {
            guard let text = try? String(contentsOf: log.url, encoding: .utf8) else { continue }
            let lines = text.split(separator: "\n")
                .map { $0.trimmingCharacters(in: .whitespaces) }
                .filter { !$0.isEmpty }
            guard let last = lines.last else { continue }
            // 只有"最后写进去的就是失败"才算原因：成功启动过就不再报旧错
            if last.contains("失败") {
                return last.replacingOccurrences(of: "保存主机启动失败：", with: "")
            }
            return nil
        }
        return nil
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
            showBanner("未找到主机程序：请把外壳与保存主机放在同一个 .app 里", isError: true)
            return
        }

        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        // 进程结束后回读配置与状态：查看端连上主机后菜单要马上显示已连接，
        // 主机起不来时第一行也要尽快给出原因
        process.terminationHandler = { [weak self] _ in
            DispatchQueue.main.async {
                guard let self else { return }
                self.refreshSettings()
                self.refreshViewerStatus()
                self.refreshAutostart()
                self.rebuildMenu()
            }
        }
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
            showBanner("启动主机失败：\(error.localizedDescription)", isError: true)
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
application.setActivationPolicy(.regular)
application.run()
