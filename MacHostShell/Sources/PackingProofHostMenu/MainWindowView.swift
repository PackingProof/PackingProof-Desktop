// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 PackingProof contributors
//
// 查看端部分的视图代码照搬 MacViewer（header / hostList / footer / HostCard /
// ManualConnectionView 原样保留），只把数据来源换成 AppStateModel。
// 保存主机那一侧没有现成界面可搬：列表按电脑端口径显示接入的录像设备，
// 保存位置、开机自启、日志这些配置收进"设置"。

import AppKit
import SwiftUI

struct MainWindowView: View {
    @ObservedObject var model: AppStateModel
    @State private var showManualConnection = false
    @State private var showSettings = false
    @State private var showPurposeChooser = false

    var body: some View {
        VStack(spacing: 0) {
            header
            if model.updateAvailable && !model.updateDismissed {
                Divider()
                updateRow
            }
            if let banner = model.banner {
                Divider()
                bannerRow(banner)
            }
            Divider()
            if model.isViewer {
                hostList
            } else {
                deviceList
            }
            Divider()
            if model.isViewer {
                viewerFooter
            } else {
                hostFooter
            }
        }
        .background(Color(nsColor: .windowBackgroundColor))
        .frame(minWidth: 520, minHeight: 300)
        .task { await model.startupRefresh() }
        .sheet(isPresented: $showManualConnection) {
            ManualConnectionView { input in
                await model.connectManually(input)
            }
        }
        .sheet(isPresented: $showSettings) {
            SettingsView(model: model)
        }
        .sheet(isPresented: $showPurposeChooser) {
            PurposeChooserView(currentIsViewer: model.isViewer) { viewer in
                model.choosePurpose(viewer: viewer)
            }
        }
        .onChange(of: model.settingsRequestToken) { _ in
            showSettings = true
        }
        .onChange(of: model.purposeChooserToken) { _ in
            showPurposeChooser = true
        }
        .onAppear {
            // 首次启动还没选过用途：直接把用途选择摆出来
            if model.needsPurposeSetup { showPurposeChooser = true }
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
            // 用途切换与电脑端一致：就一个按钮，点开再选，不在标题栏摆选择器
            Button {
                showPurposeChooser = true
            } label: {
                Label("切换用途", systemImage: AppTheme.Symbol.changeHost)
            }
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
        if model.hostRunning { return AppTheme.successGreen }
        return model.hostLaunching ? AppTheme.accentBlue : AppTheme.errorRed
    }

    /// 提示就写在窗口里，不再弹系统对话框
    private func bannerRow(_ text: String) -> some View {
        HStack(spacing: 8) {
            Image(systemName: model.bannerIsError
                  ? "exclamationmark.triangle.fill"
                  : "checkmark.circle.fill")
                .foregroundStyle(model.bannerIsError ? AppTheme.errorRed : AppTheme.successGreen)
            Text(text)
                .font(.subheadline)
                .lineLimit(2)
            Spacer(minLength: 0)
            Button {
                model.dismissBanner()
            } label: {
                Image(systemName: "xmark")
                    .font(.caption)
            }
            .buttonStyle(.borderless)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
        .background(
            (model.bannerIsError ? AppTheme.errorRed : AppTheme.successGreen).opacity(0.12))
    }

    /// 检查更新的提示：只说有新版本并给下载入口，不在应用里替换自己
    private var updateRow: some View {
        HStack(spacing: 8) {
            Image(systemName: "arrow.down.circle.fill")
                .foregroundStyle(AppTheme.accentBlue)
            VStack(alignment: .leading, spacing: 2) {
                Text("发现新版本 \(model.updateVersion)")
                    .font(.system(size: 13, weight: .semibold))
                if !model.updateTitle.isEmpty {
                    Text(model.updateTitle)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }
            Spacer(minLength: 8)
            Button("打开下载页") { model.openUpdatePage() }
                .controlSize(.small)
            Button {
                model.dismissUpdate()
            } label: {
                Image(systemName: "xmark")
                    .font(.caption)
            }
            .buttonStyle(.borderless)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
        .background(AppTheme.accentBlue.opacity(0.12))
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

                Button {
                    showSettings = true
                } label: {
                    Label("设置", systemImage: "gearshape")
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

    // MARK: - 保存主机：列表显示接入的录像设备（与电脑端同一口径）

    private var deviceList: some View {
        Group {
            if model.devices.isEmpty {
                VStack(spacing: 8) {
                    if model.hostLaunching {
                        ProgressView()
                            .controlSize(.small)
                        Text("启动中")
                            .font(.body)
                            .foregroundStyle(.secondary)
                    } else {
                        Image(systemName: "wifi.router")
                            .font(.title3)
                            .foregroundStyle(.tertiary)
                        Text(model.hostRunning ? "还没有录像设备接入" : model.hostStatusText)
                            .font(.body)
                            .foregroundStyle(.secondary)
                            .multilineTextAlignment(.center)
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    VStack(spacing: 0) {
                        ForEach(model.devices) { device in
                            DeviceRow(device: device)
                            if device.id != model.devices.last?.id { Divider() }
                        }
                    }
                    .background(
                        RoundedRectangle(cornerRadius: 10)
                            .fill(Color(nsColor: .controlBackgroundColor)))
                    .overlay(
                        RoundedRectangle(cornerRadius: 10)
                            .stroke(Color(nsColor: .separatorColor), lineWidth: 1))
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
                if model.hostLaunching {
                    ProgressView()
                        .controlSize(.small)
                }
                Text(model.hostStatusText)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                if let summary = storageSummary {
                    Text("·")
                        .font(.subheadline)
                        .foregroundStyle(.tertiary)
                    Text(summary)
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                Spacer()
            }

            HStack(spacing: 8) {
                Button {
                    showSettings = true
                } label: {
                    Label("设置", systemImage: "gearshape")
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

    private var storageSummary: String? {
        guard let first = model.storages.first else { return nil }
        if model.storages.count == 1 { return first.summary }
        return "\(first.name) 等 \(model.storages.count) 块磁盘"
    }
}

/// 设置页：保存位置与容量、开机自启、日志这些"配置"都收在这里
private struct SettingsView: View {
    @ObservedObject var model: AppStateModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("设置")
                    .font(.title3.weight(.semibold))
                Spacer()
                Button("完成") { dismiss() }
                    .keyboardShortcut(.defaultAction)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    storageSection
                    generalSection
                    aboutSection
                }
                .padding(16)
            }
        }
        .frame(width: 580, height: 460)
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private var storageSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("保存位置")
                    .font(.headline)
                Spacer()
                // 保存位置按磁盘算：同一张盘上再选目录没有意义，所以只能选磁盘
                Menu {
                    if model.disks.isEmpty {
                        Text("没有可添加的磁盘")
                    } else {
                        ForEach(model.disks) { disk in
                            Button(disk.isUsed ? "\(disk.name)（已在使用）" : disk.name) {
                                model.addDisk(disk.path)
                            }
                            .disabled(disk.isUsed)
                        }
                    }
                } label: {
                    Label("添加磁盘", systemImage: AppTheme.Symbol.addStorage)
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
            }

            if model.storages.isEmpty {
                Text(model.storePaths.isEmpty ? "还没有保存位置" : model.storePaths.joined(separator: "、"))
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            } else {
                VStack(spacing: 0) {
                    ForEach(model.storages) { store in
                        StorageCard(store: store, model: model)
                        if store.id != model.storages.last?.id { Divider() }
                    }
                }
                .background(
                    RoundedRectangle(cornerRadius: 8)
                        .fill(Color(nsColor: .controlBackgroundColor)))
                .overlay(
                    RoundedRectangle(cornerRadius: 8)
                        .stroke(Color(nsColor: .separatorColor), lineWidth: 1))
                // 保存主机要有一个能写的保存位置才起得来：这里直接把该做什么说出来
                if model.storages.allSatisfy({ !$0.available }) {
                    Text("现有保存位置都没有接入：接回磁盘，或在上面的“添加磁盘”里换成别的磁盘")
                        .font(.caption)
                        .foregroundStyle(AppTheme.errorRed)
                }
            }
        }
    }

    private var generalSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("常规").font(.headline)
            VStack(spacing: 0) {
                HStack {
                    Text("开机自启")
                    Spacer()
                    Toggle(
                        "",
                        isOn: Binding(
                            get: { model.autostartInstalled },
                            set: { _ in model.toggleAutostart() }))
                        .labelsHidden()
                        .toggleStyle(.switch)
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 10)
                Divider()
                HStack {
                    Text("日志")
                    Spacer()
                    Button {
                        model.openLogs()
                    } label: {
                        Label("打开日志目录", systemImage: AppTheme.Symbol.logs)
                    }
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 10)
            }
            .background(
                RoundedRectangle(cornerRadius: 8)
                    .fill(Color(nsColor: .controlBackgroundColor)))
            .overlay(
                RoundedRectangle(cornerRadius: 8)
                    .stroke(Color(nsColor: .separatorColor), lineWidth: 1))
        }
    }

    private var aboutSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("关于").font(.headline)
            VStack(alignment: .leading, spacing: 4) {
                Text(model.isViewer ? "PackingProof 查看端" : "PackingProof 保存主机")
                    .font(.subheadline.weight(.semibold))
                Text(model.appVersion.isEmpty ? "版本未知" : "版本 \(model.appVersion)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Text(model.isViewer
                     ? "只连接主机查看：录像、保存与网页回放都在主机那一侧"
                     : "接收手机与其他电脑上传的录像，并对外提供网页回放")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
                HStack(spacing: 8) {
                    Button {
                        Task { await model.checkUpdate() }
                    } label: {
                        Label("检查更新", systemImage: "arrow.triangle.2.circlepath")
                    }
                    .controlSize(.small)
                    if model.updateAvailable {
                        Text("有新版本 \(model.updateVersion)")
                            .font(.caption)
                            .foregroundStyle(AppTheme.accentBlue)
                        Button("打开下载页") { model.openUpdatePage() }
                            .controlSize(.small)
                    }
                }
                .padding(.top, 6)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(12)
            .background(
                RoundedRectangle(cornerRadius: 8)
                    .fill(Color(nsColor: .controlBackgroundColor)))
            .overlay(
                RoundedRectangle(cornerRadius: 8)
                    .stroke(Color(nsColor: .separatorColor), lineWidth: 1))
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

/// 接入的录像设备：排法与电脑端"手机/电脑备份"卡片一致 ——
/// 一行一台设备，状态点 + "名字 · 今日备份 N 个"，类型放右侧，
/// 地址只在鼠标悬停时提示，不再把四行字摞在一起
private struct DeviceRow: View {
    let device: DeviceItem

    var body: some View {
        HStack(spacing: 8) {
            Circle()
                .fill(device.online ? AppTheme.successGreen : Color.secondary.opacity(0.45))
                .frame(width: 7, height: 7)
            Text(device.name)
                .font(.system(size: 13, weight: .semibold))
                .lineLimit(1)
            Text("·")
                .font(.system(size: 12))
                .foregroundStyle(.tertiary)
            Text(device.backupSummary)
                .font(.system(size: 12))
                .foregroundStyle(device.todayBackupCount > 0 ? .secondary : .tertiary)
                .lineLimit(1)
            Spacer(minLength: 8)
            Text(device.typeText)
                .font(.system(size: 11))
                .foregroundStyle(.tertiary)
                .lineLimit(1)
        }
        .opacity(device.online ? 1 : 0.55)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .help(device.address.isEmpty ? device.name : device.address)
    }
}

private struct StorageCard: View {
    let store: StorageItem
    let model: AppStateModel
    @State private var capacityText = ""
    @State private var reserveText = ""
    @FocusState private var capacityFocused: Bool
    @FocusState private var reserveFocused: Bool

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
            // 就地编辑：容量上限与预留都写同一个预留值，改完立刻生效，不再弹输入框
            HStack(spacing: 6) {
                Text("容量上限")
                TextField("", text: $capacityText)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 64)
                    .focused($capacityFocused)
                    .onSubmit { applyLimit() }
                    .disabled(!store.capacityKnown)
                Text("GB")
                Text("预留")
                TextField("", text: $reserveText)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 64)
                    .focused($reserveFocused)
                    .onSubmit { applyLimit() }
                    .disabled(!store.capacityKnown)
                Text("GB")
                Button("应用") { applyLimit() }
                    .disabled(!store.capacityKnown)
                Spacer(minLength: 8)
                Button {
                    model.actions?.openStorageLocation(store.path)
                } label: {
                    Label("在 Finder 中打开", systemImage: AppTheme.Symbol.reveal)
                }
            }
            .controlSize(.small)
            .padding(.top, 4)
        }
        .opacity(store.available ? 1 : 0.55)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
        .onAppear { syncFromStore() }
        .onChange(of: store) { _ in syncFromStore() }
    }

    private func syncFromStore() {
        capacityText = numberText(store.capacityGB)
        reserveText = numberText(store.reserveGB)
    }

    private func applyLimit() {
        // 先收起焦点：否则输入框会一直显示旧值，看不到"已经生效"
        capacityFocused = false
        reserveFocused = false

        let trimmedCapacity = capacityText.trimmingCharacters(in: .whitespaces)
        if let capacity = Double(trimmedCapacity), abs(capacity - store.capacityGB) > 0.01 {
            model.setCapacity(path: store.path, gigabytes: capacity)
            return
        }

        let trimmedReserve = reserveText.trimmingCharacters(in: .whitespaces)
        if let reserve = Double(trimmedReserve), abs(reserve - store.reserveGB) > 0.01 {
            model.setReserve(path: store.path, gigabytes: reserve)
        }
    }

    private func numberText(_ value: Double) -> String {
        value == value.rounded() ? String(Int(value)) : String(format: "%.1f", value)
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
