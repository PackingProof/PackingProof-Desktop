// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 PackingProof contributors
//
// 查看端部分的视图代码照搬 MacViewer（header / hostList / footer / HostCard /
// ManualConnectionView 原样保留），只把数据来源换成 AppStateModel。
// 保存主机那一侧没有现成界面可搬，按同一套版式与配色补上。

import AppKit
import SwiftUI

struct MainWindowView: View {
    @ObservedObject var model: AppStateModel
    @State private var showManualConnection = false

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            if model.isViewer {
                hostList
            } else {
                storageList
            }
            Divider()
            if model.isViewer {
                viewerFooter
            } else {
                hostFooter
            }
        }
        .background(Color(nsColor: .windowBackgroundColor))
        .frame(minWidth: 520, minHeight: 420)
        .task { await model.startupRefresh() }
        .sheet(isPresented: $showManualConnection) {
            ManualConnectionView { input in
                await model.connectManually(input)
            }
        }
    }

    private var header: some View {
        HStack(spacing: 12) {
            Image(nsImage: Self.appIcon)
                .resizable()
                .interpolation(.high)
                .frame(width: 38, height: 38)
                .clipShape(RoundedRectangle(cornerRadius: 9, style: .continuous))
            VStack(alignment: .leading, spacing: 2) {
                Text(model.isViewer ? "PackingProof 查看端" : "PackingProof 保存主机")
                    .font(.title3.weight(.semibold))
                Text(model.isViewer ? "只连接主机查看" : "接收手机与其他电脑的录像")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            // 这台电脑既能当保存主机也能当查看端，切换放在标题旁边
            Picker(
                "",
                selection: Binding(
                    get: { model.isViewer },
                    set: { newValue in Task { await model.switchPurpose(viewer: newValue) } })
            ) {
                Text("保存主机").tag(false)
                Text("查看端").tag(true)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .frame(width: 190)
            .disabled(model.isSwitchingPurpose)

            Circle()
                .fill(statusDotColor)
                .frame(width: 8, height: 8)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
    }

    private var statusDotColor: Color {
        if model.isViewer {
            return model.onlineNodeIds.isEmpty ? Color.secondary.opacity(0.45) : AppTheme.successGreen
        }
        return model.hostRunning ? AppTheme.successGreen : AppTheme.errorRed
    }

    private static var appIcon: NSImage {
        NSApp.applicationIconImage
            ?? NSImage(systemSymbolName: "shippingbox.fill", accessibilityDescription: nil)
            ?? NSImage()
    }

    // MARK: - 查看端（照搬 MacViewer）

    private var hostList: some View {
        Group {
            if model.hosts.isEmpty {
                VStack(spacing: 8) {
                    if model.isSearching {
                        ProgressView()
                            .controlSize(.small)
                    } else {
                        Image(systemName: AppTheme.Symbol.noHost)
                            .font(.title3)
                            .foregroundStyle(.tertiary)
                        Text("未找到主机")
                            .font(.body)
                            .foregroundStyle(.secondary)
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    LazyVStack(spacing: 8) {
                        ForEach(model.hosts) { host in
                            HostCard(
                                host: host,
                                isSelected: host.id == model.selectedHostId,
                                isOnline: model.onlineNodeIds.contains(host.id)
                            ) {
                                model.selectedHostId = host.id
                            }
                        }
                    }
                    .padding(.horizontal, 16)
                    .padding(.vertical, 10)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var viewerFooter: some View {
        VStack(spacing: 8) {
            HStack(spacing: 6) {
                if model.isSearching || model.isOpeningWeb {
                    ProgressView()
                        .controlSize(.small)
                }
                Text(model.status)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                Spacer()
            }

            HStack(spacing: 8) {
                Button {
                    Task { await model.search() }
                } label: {
                    Label("重新搜索", systemImage: AppTheme.Symbol.search)
                }
                .disabled(model.isSearching)

                Button {
                    showManualConnection = true
                } label: {
                    Label("手动连接", systemImage: AppTheme.Symbol.manualConnect)
                }

                Button {
                    Task { await model.clearRememberedHost() }
                } label: {
                    Label("更换主机", systemImage: AppTheme.Symbol.changeHost)
                }

                Spacer()

                Button {
                    Task { await model.openWebPlayback() }
                } label: {
                    Label("打开网页回放", systemImage: AppTheme.Symbol.play)
                }
                .buttonStyle(.borderedProminent)
                .tint(AppTheme.accentBlue)
                .disabled(model.hosts.isEmpty || model.isOpeningWeb)
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
    }

    // MARK: - 保存主机（按同一套版式补）

    private var storageList: some View {
        Group {
            if model.storages.isEmpty {
                VStack(spacing: 8) {
                    Image(systemName: AppTheme.Symbol.storage)
                        .font(.title3)
                        .foregroundStyle(.tertiary)
                    Text(model.storePaths.isEmpty ? "还没有保存位置" : model.storePaths.joined(separator: "、"))
                        .font(.body)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    LazyVStack(spacing: 8) {
                        ForEach(model.storages) { store in
                            StorageCard(store: store, model: model)
                        }
                    }
                    .padding(.horizontal, 16)
                    .padding(.vertical, 10)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var hostFooter: some View {
        VStack(spacing: 8) {
            HStack(spacing: 6) {
                if model.isSwitchingPurpose {
                    ProgressView()
                        .controlSize(.small)
                }
                Text(model.hostStatusText)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                Spacer()
            }

            HStack(spacing: 8) {
                Button {
                    addStorageViaPanel()
                } label: {
                    Label("添加保存位置", systemImage: AppTheme.Symbol.addStorage)
                }

                Button {
                    model.toggleAutostart()
                } label: {
                    Label(
                        model.autostartInstalled ? "取消开机自启" : "开机自启",
                        systemImage: AppTheme.Symbol.autostart)
                }

                Button {
                    model.openLogs()
                } label: {
                    Label("日志目录", systemImage: AppTheme.Symbol.logs)
                }

                Spacer()

                Button {
                    Task { await model.openWebPlayback() }
                } label: {
                    Label("打开网页回放", systemImage: AppTheme.Symbol.play)
                }
                .buttonStyle(.borderedProminent)
                .tint(AppTheme.accentBlue)
                .disabled(!model.hostRunning)
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
    }

    private func addStorageViaPanel() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = "使用这个位置"
        panel.message = "选择录像保存位置（可以选外接硬盘）"
        if panel.runModal() == .OK, let url = panel.url {
            model.addStorage(url.path)
        }
    }
}

private struct HostCard: View {
    let host: DiscoveredHost
    let isSelected: Bool
    let isOnline: Bool
    let action: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(host.nodeName)
                    .font(.headline)
                Text(host.address)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                Spacer(minLength: 0)
                if !isOnline {
                    Text("离线")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            if !host.capabilitySummary.isEmpty {
                Text(host.capabilitySummary)
                    .font(.caption)
                    .foregroundStyle(.tertiary)
                    .lineLimit(1)
            }
        }
        .opacity(isOnline ? 1 : 0.55)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
        .background(
            RoundedRectangle(cornerRadius: 8)
                .fill(isSelected
                    ? AppTheme.accentBlue.opacity(0.12)
                    : Color(nsColor: .controlBackgroundColor))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 8)
                .stroke(
                    isSelected ? AppTheme.accentBlue : Color(nsColor: .separatorColor),
                    lineWidth: isSelected ? 1.5 : 1
                )
        )
        .contentShape(RoundedRectangle(cornerRadius: 8))
        .onTapGesture(perform: action)
    }
}

private struct StorageCard: View {
    let store: StorageItem
    let model: AppStateModel

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(store.name)
                    .font(.headline)
                Text(store.path)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer(minLength: 0)
                if !store.available {
                    Text("未接入")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            Text(store.summary)
                .font(.caption)
                .foregroundStyle(.tertiary)
                .lineLimit(1)
            if store.available, store.capacityKnown, store.reserveGB < store.recommendedReserveGB {
                Text("预留偏低，建议至少 \(Int(store.recommendedReserveGB)) GB")
                    .font(.caption)
                    .foregroundStyle(AppTheme.errorRed)
                    .lineLimit(1)
            }
            HStack(spacing: 8) {
                Button {
                    model.actions?.openStorageLocation(store.path)
                } label: {
                    Label("在 Finder 中打开", systemImage: AppTheme.Symbol.reveal)
                }
                Button {
                    model.actions?.promptCapacity(store.path)
                } label: {
                    Label("容量上限", systemImage: AppTheme.Symbol.capacity)
                }
                .disabled(!store.capacityKnown)
                Button {
                    model.actions?.promptReserve(store.path)
                } label: {
                    Label("预留空间", systemImage: AppTheme.Symbol.storage)
                }
                .disabled(!store.capacityKnown)
                Spacer()
            }
            .controlSize(.small)
            .padding(.top, 4)
        }
        .opacity(store.available ? 1 : 0.55)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
        .background(
            RoundedRectangle(cornerRadius: 8)
                .fill(Color(nsColor: .controlBackgroundColor))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 8)
                .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
        )
    }
}

private struct ManualConnectionView: View {
    @Environment(\.dismiss) private var dismiss
    @State private var input = ""
    @State private var errorText: String?
    @State private var isConnecting = false

    let onConnect: (String) async -> String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("手动连接主机")
                .font(.title3.weight(.semibold))

            TextField("例如 192.168.1.5:5280 或带 key 的完整链接", text: $input)
                .textFieldStyle(.roundedBorder)

            if let errorText {
                Text(errorText)
                    .font(.subheadline)
                    .foregroundStyle(AppTheme.errorRed)
            }

            HStack {
                Spacer()
                Button("取消") {
                    dismiss()
                }
                Button {
                    connect()
                } label: {
                    Label("连接", systemImage: AppTheme.Symbol.manualConnect)
                }
                .buttonStyle(.borderedProminent)
                .tint(AppTheme.accentBlue)
                .disabled(input.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || isConnecting)
            }
        }
        .padding(20)
        .frame(width: 400)
        .fixedSize(horizontal: false, vertical: true)
    }

    private func connect() {
        guard !isConnecting else { return }
        isConnecting = true
        errorText = nil
        let value = input
        Task {
            let error = await onConnect(value)
            if let error {
                errorText = error
                isConnecting = false
            } else {
                dismiss()
            }
        }
    }
}
