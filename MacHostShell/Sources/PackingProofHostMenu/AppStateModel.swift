// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 PackingProof contributors

import Foundation

/// 发现到的主机。字段与 MacViewer 的 DiscoveredHost 一致，视图代码才能原样搬过来。
struct DiscoveredHost: Identifiable, Equatable {
    let nodeId: String
    let nodeName: String
    let address: String
    var capabilitySummary: String = ""

    var id: String { nodeId.isEmpty ? address : nodeId }
}

/// 一个保存位置：与 MacViewer 的卡片同一套呈现方式。
struct StorageItem: Identifiable, Equatable {
    let path: String
    let name: String
    let available: Bool
    let capacityKnown: Bool
    let capacityGB: Double
    let reserveGB: Double
    let recommendedReserveGB: Double

    var id: String { path }

    var summary: String {
        guard available else { return "磁盘未接入" }
        guard capacityKnown else { return "磁盘太小，放不下最低预留" }
        return "容量上限 \(Self.numberText(capacityGB)) GB，预留 \(Self.numberText(reserveGB)) GB"
    }

    private static func numberText(_ value: Double) -> String {
        value == value.rounded() ? String(Int(value)) : String(format: "%.1f", value)
    }
}

/// 可添加为保存位置的磁盘。保存位置按磁盘算：同一张盘上再加目录没有意义。
struct DiskItem: Identifiable, Equatable {
    let path: String
    let name: String
    let isUsed: Bool

    var id: String { path }
}

/// 窗口的界面状态。
///
/// 属性与动作刻意对齐 MacViewer 的 ViewerModel：视图代码照搬，只有数据来源换成
/// 本机主机的命令行（同一份 config.json）。若连它的服务一起搬，
/// 主机与密钥会存成两份，窗口和菜单栏就会各说各话。
final class AppStateModel: ObservableObject {
    @Published var hosts: [DiscoveredHost] = []
    @Published var status = ""
    @Published var isSearching = false
    @Published var isOpeningWeb = false
    @Published var selectedHostId: String?
    @Published var onlineNodeIds: Set<String> = []

    /// 本机用途：查看端 / 保存主机
    @Published var isViewer = false
    @Published var hostStatusText = ""
    @Published var hostRunning = false
    @Published var isSwitchingPurpose = false
    @Published var storages: [StorageItem] = []
    @Published var disks: [DiskItem] = []
    @Published var storePaths: [String] = []
    @Published var autostartInstalled = false

    var actions: Actions?

    struct Actions {
        var startupRefresh: () async -> Void
        var search: () async -> Void
        var clearRememberedHost: () async -> Void
        var openWebPlayback: () async -> Void
        var connectManually: (String) async -> String?
        var switchPurpose: (Bool) async -> Void
        var openStorageLocation: (String) -> Void
        var promptCapacity: (String) -> Void
        var promptReserve: (String) -> Void
        var addDisk: (String) -> Void
        var toggleAutostart: () -> Void
        var openLogs: () -> Void
    }

    var selectedHost: DiscoveredHost? {
        guard let selectedHostId else { return nil }
        return hosts.first { $0.id == selectedHostId }
    }

    func startupRefresh() async {
        await actions?.startupRefresh()
    }

    func search() async {
        guard !isSearching else { return }
        await actions?.search()
    }

    func clearRememberedHost() async {
        await actions?.clearRememberedHost()
    }

    func openWebPlayback() async {
        guard !isOpeningWeb else { return }
        await actions?.openWebPlayback()
    }

    func connectManually(_ input: String) async -> String? {
        await actions?.connectManually(input)
    }

    func switchPurpose(viewer: Bool) async {
        guard viewer != isViewer, !isSwitchingPurpose else { return }
        isSwitchingPurpose = true
        await actions?.switchPurpose(viewer)
        isSwitchingPurpose = false
    }

    func addDisk(_ path: String) {
        actions?.addDisk(path)
    }

    func toggleAutostart() {
        actions?.toggleAutostart()
    }

    func openLogs() {
        actions?.openLogs()
    }
}
