// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 PackingProof contributors
//
// 品牌色与图标约定与 MacViewer 完全一致：界面照它的代码搬，颜色不能自创。

import SwiftUI

/// 只保留 PackingProof 品牌色与图标约定，其余颜色走系统语义色以跟随明暗主题。
enum AppTheme {
    static let accentBlue = Color(red: 0x3B / 255.0, green: 0x82 / 255.0, blue: 0xF6 / 255.0)
    static let successGreen = Color(red: 0x10 / 255.0, green: 0xB9 / 255.0, blue: 0x81 / 255.0)
    static let errorRed = Color(red: 0xEF / 255.0, green: 0x44 / 255.0, blue: 0x44 / 255.0)

    enum Symbol {
        static let search = "arrow.clockwise"
        static let manualConnect = "link"
        static let changeHost = "arrow.triangle.2.circlepath"
        static let play = "safari.fill"
        static let noHost = "wifi.slash"

        // 保存主机这一侧沿用同一套图标习惯
        static let storage = "externaldrive"
        static let addStorage = "plus.circle"
        static let capacity = "chart.pie"
        static let autostart = "power"
        static let logs = "doc.text.magnifyingglass"
        static let reveal = "folder"
    }
}
