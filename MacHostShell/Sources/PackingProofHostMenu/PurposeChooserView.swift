// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 PackingProof contributors
//
// 用途选择：版式与文案照电脑端的"选择电脑用途"窗口（WorkstationSelectionWindow）：
// 标题 + 编号问题 + 选项卡片（图标、标题、说明、右侧选中圆点）+ 选择结果卡 + 取消/确认用途。
// Mac 没有摄像头录像那一步，所以只保留"要不要长期保存录像"这一个问题。

import SwiftUI

struct PurposeChooserView: View {
    /// 当前是不是查看端，用来在打开时预选
    let currentIsViewer: Bool
    /// 确认后回调：true = 查看端
    let onConfirm: (Bool) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var choice: Choice?

    /// 两张卡片等高：电脑端是 Grid 行拉伸，这里用固定高度做到同样效果，
    /// 否则两边文字长度不同，卡片看起来一高一矮
    private let cardHeight: CGFloat = 126

    private enum Choice: Equatable {
        case host
        case viewer
    }

    var body: some View {
        // 不用 ScrollView：内容一滚动就按小数坐标合成，粗体字会发虚
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 7) {
                Text("选择这台电脑的用途")
                    .font(.system(size: 28, weight: .bold))
                Text("回答一个问题，系统会给出这台电脑该做什么")
                    .font(.system(size: 14))
                    .foregroundStyle(.secondary)
            }
            .padding(.bottom, 22)

            Text("1. 这台电脑要负责长期保存录像吗？")
                .font(.system(size: 17, weight: .bold))
                .padding(.bottom, 10)

            HStack(alignment: .top, spacing: 14) {
                choiceCard(
                    selection: .host,
                    symbol: "checkmark.circle",
                    title: "要，作为保存主机",
                    detail: "主机就是负责长期保存录像的电脑，可接收手机和其他电脑上传的录像，并在局域网里提供网页回放")
                choiceCard(
                    selection: .viewer,
                    symbol: "xmark.circle",
                    title: "不要，只连接主机查看",
                    detail: "本机不保存录像，只在局域网里发现保存主机，并用浏览器打开它的网页回放")
            }
            .padding(.bottom, 20)

            resultCard
                .padding(.bottom, 20)

            HStack(spacing: 10) {
                Spacer()
                Button("取消") { dismiss() }
                    .controlSize(.large)
                Button("确认用途") {
                    guard let choice else { return }
                    onConfirm(choice == .viewer)
                    dismiss()
                }
                .controlSize(.large)
                .buttonStyle(.borderedProminent)
                .tint(AppTheme.accentBlue)
                .disabled(choice == nil)
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(32)
        .frame(width: 720, height: 540)
        .background(Color(nsColor: .windowBackgroundColor))
        .onAppear {
            if choice == nil { choice = currentIsViewer ? .viewer : .host }
        }
    }

    /// 与电脑端一致的卡片：图标 + 标题 + 说明 + 右侧选中圆点，选中时描边品牌蓝
    private func choiceCard(
        selection: Choice,
        symbol: String,
        title: String,
        detail: String
    ) -> some View {
        let selected = choice == selection
        return Button {
            choice = selection
        } label: {
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: symbol)
                    .font(.system(size: 20))
                    .foregroundStyle(selected ? AppTheme.accentBlue : Color.secondary)
                    .frame(width: 24)
                VStack(alignment: .leading, spacing: 5) {
                    Text(title)
                        .font(.system(size: 16, weight: .bold))
                        .foregroundStyle(.primary)
                    Text(detail)
                        .font(.system(size: 12))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .multilineTextAlignment(.leading)
                }
                Spacer(minLength: 8)
                Circle()
                    .strokeBorder(
                        selected ? AppTheme.accentBlue : Color(nsColor: .separatorColor),
                        lineWidth: 2)
                    .background(
                        Circle().fill(selected ? AppTheme.accentBlue : Color.clear))
                    .frame(width: 16, height: 16)
                    .padding(.top, 2)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 13)
            .frame(maxWidth: .infinity, minHeight: cardHeight, alignment: .topLeading)
            .background(
                RoundedRectangle(cornerRadius: 12)
                    .fill(selected
                          ? AppTheme.accentBlue.opacity(0.10)
                          : Color(nsColor: .controlBackgroundColor)))
            .overlay(
                RoundedRectangle(cornerRadius: 12)
                    .stroke(
                        selected ? AppTheme.accentBlue : Color(nsColor: .separatorColor),
                        lineWidth: 1))
            .contentShape(RoundedRectangle(cornerRadius: 12))
        }
        .buttonStyle(.plain)
    }

    private var resultCard: some View {
        HStack(alignment: .top, spacing: 16) {
            RoundedRectangle(cornerRadius: 10)
                .fill(Color(nsColor: .controlBackgroundColor))
                .frame(width: 44, height: 44)
                .overlay(
                    Image(systemName: choice == .viewer ? "safari" : "externaldrive.fill")
                        .font(.system(size: 22))
                        .foregroundStyle(AppTheme.accentBlue))
            VStack(alignment: .leading, spacing: 5) {
                Text("选择结果")
                    .font(.system(size: 12, weight: .bold))
                    .foregroundStyle(AppTheme.accentBlue)
                Text(resultTitle)
                    .font(.system(size: 21, weight: .black))
                Text(resultDetail)
                    .font(.system(size: 13))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if let choice {
                    VStack(alignment: .leading, spacing: 2) {
                        ForEach(capabilities(choice), id: \.self) { item in
                            HStack(alignment: .firstTextBaseline, spacing: 9) {
                                Circle()
                                    .fill(AppTheme.accentBlue)
                                    .frame(width: 5, height: 5)
                                Text(item)
                                    .font(.system(size: 13))
                            }
                        }
                    }
                    .padding(.top, 10)
                }
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 17)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 12)
                .fill(Color(nsColor: .controlBackgroundColor)))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .stroke(Color(nsColor: .separatorColor), lineWidth: 1))
    }

    private var resultTitle: String {
        switch choice {
        case .host: return "保存主机"
        case .viewer: return "查看端"
        case nil: return "请完成上面的选择"
        }
    }

    private var resultDetail: String {
        switch choice {
        case .host: return "这台电脑负责长期保存录像，手机与其他电脑把录像传给它"
        case .viewer: return "这台电脑只连接主机查看，不保存录像"
        case nil: return "完成后会在这里显示最终用途和支持的能力"
        }
    }

    private func capabilities(_ choice: Choice) -> [String] {
        switch choice {
        case .host:
            return [
                "接收手机与其他电脑上传的录像",
                "按磁盘管理保存位置、容量上限与预留空间",
                "对外提供网页回放，供查看端和手机浏览"
            ]
        case .viewer:
            return [
                "在局域网里自动发现保存主机",
                "首次连接由主机确认一次，之后直接查看",
                "搜索、播放与剪辑都在主机的网页里完成"
            ]
        }
    }
}
