// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 PackingProof contributors

import SwiftUI

/// 主窗口：界面沿用 MacViewer 的排布（状态 + 主机列表 + 一个主要动作），
/// 但数据全部来自本机主机的命令行，不再另起一套发现与密钥实现。
struct MainWindowView: View {
    @ObservedObject var model: AppStateModel

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            header
            purposePicker
            Divider()
            if model.isViewer {
                viewerSection
            } else {
                hostSection
            }
            Divider()
            footer
        }
        .padding(20)
        .frame(minWidth: 560, minHeight: 440)
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("PackingProof")
                .font(.title2).bold()
            Text(model.isViewer ? "查看端：\(model.viewerStatusLine)" : "保存主机：\(model.hostStatusText)")
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var purposePicker: some View {
        HStack(spacing: 8) {
            purposeButton(title: "保存主机", selected: !model.isViewer) {
                model.actions?.useHostPurpose()
            }
            purposeButton(title: "查看端", selected: model.isViewer) {
                model.actions?.useViewerPurpose()
            }
            Spacer()
            Button(model.isViewer ? "打开网页回放" : "打开本机回放") {
                model.actions?.openPlayback()
            }
            .disabled(!model.playbackEnabled)
            .keyboardShortcut(.defaultAction)
        }
    }

    private func purposeButton(title: String, selected: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 6) {
                Image(systemName: selected ? "largecircle.fill.circle" : "circle")
                Text(title)
            }
        }
        .buttonStyle(.bordered)
        .disabled(selected)
    }

    private var viewerSection: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("保存主机").font(.headline)
                Spacer()
                Button("重新搜索") { model.actions?.rescanHosts() }
                Button("移除当前主机") { model.actions?.forgetHost() }
                    .disabled(model.currentHostId.isEmpty && model.hosts.isEmpty)
            }

            if model.hosts.isEmpty {
                Text(model.searchingHosts ? "正在搜索同一网络中的主机…" : "还没有找到保存主机")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.vertical, 8)
            } else {
                VStack(spacing: 0) {
                    ForEach(model.hosts) { host in
                        hostRow(host)
                        if host.id != model.hosts.last?.id { Divider() }
                    }
                }
                .background(RoundedRectangle(cornerRadius: 8).fill(.quaternary.opacity(0.4)))
            }
        }
    }

    private func hostRow(_ host: AppStateModel.HostRow) -> some View {
        let isCurrent = host.id == model.currentHostId
        return HStack(spacing: 10) {
            Image(systemName: isCurrent ? "checkmark.circle.fill" : "circle")
                .foregroundStyle(isCurrent ? Color.accentColor : Color.secondary)
            VStack(alignment: .leading, spacing: 2) {
                Text(host.name).bold()
                Text(host.address).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if isCurrent {
                Text(model.viewerConnected ? "已连接" : model.viewerStatusLine)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else {
                Button("连接") { model.actions?.selectHost(host.id) }
            }
        }
        .padding(10)
    }

    private var hostSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("保存位置与容量").font(.headline)
                Spacer()
                Button("打开本机回放") { model.actions?.openPlayback() }
                    .disabled(!model.playbackEnabled)
            }

            if model.storages.isEmpty {
                Text(model.storePaths.isEmpty ? "还没有设置保存位置" : model.storePaths.joined(separator: "、"))
                    .foregroundStyle(.secondary)
            } else {
                VStack(spacing: 0) {
                    ForEach(model.storages) { store in
                        storageRow(store)
                        if store.id != model.storages.last?.id { Divider() }
                    }
                }
                .background(RoundedRectangle(cornerRadius: 8).fill(.quaternary.opacity(0.4)))
            }

            Toggle("开机自启", isOn: Binding(
                get: { model.autostartInstalled },
                set: { _ in model.actions?.toggleAutostart() }))
                .toggleStyle(.switch)
        }
    }

    private func storageRow(_ store: AppStateModel.StorageRow) -> some View {
        HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                Text(store.name).bold()
                Text(capacityText(store))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Button("在 Finder 中打开") { model.actions?.openStorageLocation(store.id) }
            Button("容量上限…") { model.actions?.promptCapacity(store.id) }
                .disabled(!store.capacityKnown)
            Button("预留空间…") { model.actions?.promptReserve(store.id) }
                .disabled(!store.capacityKnown)
        }
        .padding(10)
    }

    private func capacityText(_ store: AppStateModel.StorageRow) -> String {
        guard store.available else { return "磁盘未接入" }
        guard store.capacityKnown else { return "磁盘太小，放不下最低预留" }
        var text = "容量上限 \(numberText(store.capacityGB)) GB，预留 \(numberText(store.reserveGB)) GB"
        if store.reserveGB < store.recommendedReserveGB {
            text += "（预留偏低，建议至少 \(numberText(store.recommendedReserveGB)) GB）"
        }
        return text
    }

    private func numberText(_ value: Double) -> String {
        value == value.rounded() ? String(Int(value)) : String(format: "%.1f", value)
    }

    private var footer: some View {
        HStack {
            Button("打开日志目录") { model.actions?.openLogs() }
            Spacer()
            Button("退出") { model.actions?.quit() }
        }
    }
}
