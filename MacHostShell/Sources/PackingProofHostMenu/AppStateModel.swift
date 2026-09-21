// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 PackingProof contributors

import Foundation

/// 主窗口与菜单栏共用的界面状态。
///
/// 状态只有一份，由 HostShell 在每次 rebuildMenu() 时灌进来，
/// 所以窗口和菜单栏永远不会出现"一个说已连接、一个说没连"。
/// 不加 @MainActor：壳本身（NSObject 子类）不是主 actor，而它的所有回调
/// （定时器、菜单动作、命令行回调）本来都已经切回主线程。
final class AppStateModel: ObservableObject {
    struct HostRow: Identifiable, Equatable {
        let id: String
        let name: String
        let address: String
    }

    struct StorageRow: Identifiable, Equatable {
        let id: String
        let name: String
        let available: Bool
        let capacityKnown: Bool
        let capacityGB: Double
        let reserveGB: Double
        let recommendedReserveGB: Double
    }

    /// 窗口上的按钮怎么落到主机动作上：全部转给菜单栏壳里已有的实现
    struct Actions {
        var useHostPurpose: () -> Void
        var useViewerPurpose: () -> Void
        var rescanHosts: () -> Void
        var forgetHost: () -> Void
        var selectHost: (String) -> Void
        var openPlayback: () -> Void
        var openLogs: () -> Void
        var quit: () -> Void
        var openStorageLocation: (String) -> Void
        var promptCapacity: (String) -> Void
        var promptReserve: (String) -> Void
        var toggleAutostart: () -> Void
    }

    @Published var purposeTitle = "未启动"
    @Published var hostPurposeTitle = "保存主机"
    @Published var viewerPurposeTitle = "查看端"
    @Published var isViewer = false
    @Published var hostServing = false
    @Published var hostProblem = ""
    @Published var viewerStatusText = ""
    @Published var viewerConnected = false
    @Published var searchingHosts = false
    @Published var hosts: [HostRow] = []
    @Published var currentHostId = ""
    @Published var storePaths: [String] = []
    @Published var storages: [StorageRow] = []
    @Published var autostartInstalled = false
    @Published var playbackEnabled = false

    var actions: Actions?

    /// 保存主机这一侧的一句话状态
    var hostStatusText: String {
        if hostServing { return "运行中" }
        return hostProblem.isEmpty ? "未运行" : hostProblem
    }

    var viewerStatusLine: String {
        viewerStatusText.isEmpty ? "—" : viewerStatusText
    }
}
