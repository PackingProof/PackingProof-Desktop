// swift-tools-version:5.9
// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 PackingProof contributors
//
// Mac 保存主机的菜单栏壳：只负责状态显示与动作转发，
// 录像、备份、存储与网页全部仍在 .NET 主机进程里。

import PackageDescription

let package = Package(
    name: "PackingProofHostMenu",
    platforms: [.macOS(.v13)],
    products: [
        .executable(name: "PackingProofHostMenu", targets: ["PackingProofHostMenu"])
    ],
    targets: [
        .executableTarget(
            name: "PackingProofHostMenu",
            path: "Sources/PackingProofHostMenu"
        )
    ]
)
