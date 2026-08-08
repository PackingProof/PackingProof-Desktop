# Repository Guidelines

## Project Structure & Module Organization

- `ExpressPackingMonitoring.sln` is the main solution.
- `ExpressPackingMonitoring/` contains the WPF application, including XAML views, view models, services, SQLite access, recording logic, and `Web/index.html`.
- `ExpressPackingMonitoring.Launcher/` contains the small launcher executable used by the clean package layout.
- `Tools/Publish-CleanPackage.ps1` creates the per-user Setup, distributable directory, LZMA2 solid 7z, compatibility zip, update manifest, launcher manifest, optional AppPatch, and a separate LauncherPatch.
- `Scripts/快递助手订单推送.user.js` is the browser userscript for order push integration.
- `Image/` stores README and project screenshots. `Test/HTML/` contains captured sample pages for script/debug reference, not an automated test suite.

## Build, Test, and Development Commands

```powershell
dotnet restore ExpressPackingMonitoring.sln
dotnet build ExpressPackingMonitoring.sln -c Debug
dotnet run --project ExpressPackingMonitoring
pwsh -NoProfile -File Tools\Publish-CleanPackage.ps1
pwsh -NoProfile -File Tools\Test-Release-Automated.ps1
```

- `restore` downloads NuGet dependencies.
- `build` verifies the WPF app and launcher compile.
- `run` starts the main app locally.
- `Tools\Publish-CleanPackage.ps1` produces the clean release layout with the root launcher and `app\` payload.
- `Tools\Test-Release-Automated.ps1` runs the isolated WPF smoke test, userscript concurrency/routing tests, and headless Web UI acceptance suite.

## Runtime and Distribution Notes

- The publish script generates a directory package and a matching `.zip`.
- The clean package root should mainly contain `ExpressPackingMonitoring.exe` and `app\`; the real app payload, dependencies, Web files, LibVLC files, and `tools\ffmpeg.exe` live under `app\`.
- Release packages must not include `config.json`, `videos.db`, cache files, logs, recordings, or other local runtime data.
- Runtime data is stored under `%LOCALAPPDATA%\ExpressPackingMonitoring\`, so normal upgrades keep user configuration and database records.
- `ffmpeg.exe` may be resolved from `app\tools\ffmpeg.exe`, the application runtime directory, or the system `PATH`.
- 正式发布基线固定为 FFmpeg 4.4.1 Essentials（兼容 Win7 老显卡 NVENC API 11.1）。AV1 硬件编码不作为产品能力，选择 AV1 时会自动回退 H.265；双 FFmpeg 基线方案（8.0.1 + 4.4.1）已评估但暂不实施。高级用户可在 Win8+ 自行替换 `app\tools\ffmpeg.exe` 获取新能力，官方不保证支持。
- LibVLC 随包收录全部播放相关插件（解码/解封装/字幕/滤镜/输出），仅排除与本地录像回放无关的目录（access_output/mux/services_discovery/stream_out/visualization/lua）；发布时移除设计时程序集（ReachFramework、WinForms Design）。收录与排除规则集中在 `ExpressPackingMonitoring.csproj`，新增播放能力需要插件时按同目录模式追加。
- `Scripts/快递助手订单推送.user.js` is the browser userscript used for order push integration.
- Edge TTS is the default online voice path. Kokoro local TTS models and runtime dependencies are optional and should not be bundled unless explicitly intended.
- Full packages include the generated default Edge TTS cache. AppPatch packages must exclude TTS cache files.

## Update & Release Workflow

- Users should start the root launcher. The launcher starts the app immediately, checks updates in the background, downloads verified AppPatch packages into `%LOCALAPPDATA%\ExpressPackingMonitoring\cache\updates`, and installs pending patches on the next launcher run.
- The main app may update the root launcher through the optional, separately verified `launcher_package` descriptor. It must wait for the old launcher process to exit, use the shared update mutex, replace only the standard root launcher, verify the result, and restore the previous launcher on failure.
- AppPatch packages are fixed-baseline cumulative patches. The AppPatch baseline is specified by `-PatchBaselineVersion` and defaults to `0.0.18`, but scripts may allow overriding it when a new formal baseline is chosen. It is independent from the launcher baseline.
- Keep update URLs configurable through environment variables or `.env`. The default update check URL is GitHub releases latest API; `.env` may point to another release provider.
- Do not generate AppFull or ManualUpdate packages. GitHub Release uploads normally include the Setup, full 7z, compatibility zip, `update_vX.Y.Z.json`, optional `ExpressPackingMonitoring_AppPatch_vX.Y.Z.zip`, and a LauncherPatch only when that release establishes a new launcher baseline.
- The launcher baseline is immutable and recorded in `Tools/launcher-baseline.json`; it locks the launcher source fingerprint and the launcher bytes used in clean packages and `launcher_package`. Ordinary app releases must reuse the locked launcher bytes and must not rebuild or re-upload LauncherPatch. When launcher logical inputs change, run `Tools/Publish-LauncherBaseline.ps1`, commit the new lock, and create a plain `launcher-vX.Y.Z` Git tag without creating a GitHub or Gitee Release for that component tag. A launcher change does not force a full reinstall: the release ships a LauncherPatch that the updated app applies automatically through `launcher_package`.
- AppPatch and LauncherPatch are separate ZIP files. Each includes its own double-click manual installer and instructions; AppPatch must never contain the launcher executable. The old launcher updates the app first, then the updated app applies the independently verified launcher bridge.
- Keep release notes in `update_vX.Y.Z.json` synchronized with the final release description before uploading.
- Keep `launcher_manifest_vX.Y.Z.json` and `release_info_vX.Y.Z.txt` as local verification and handoff files; do not upload them to GitHub or Gitee by default.
- Gitee releases receive the update JSON, optional AppPatch, and a LauncherPatch only for a new launcher baseline, but not the Setup, full package 7z, or full package zip.
- For Gitee, open the new-release page for the user and let the user complete the form and upload files manually; do not automate submission unless the user explicitly changes this workflow.
- Update the launcher only when necessary; once its logic changes, publish a new launcher baseline and LauncherPatch instead of modifying the locked bytes.

## Storage, Cache, and Web Video

- Storage settings are expressed as reserved free space for the system and other apps, not as a recording quota. Keep `StorageSpacePolicy` as the single source of truth for minimum reserve rules.
- Cache-like Web artifacts, including transcode cache, clip previews, and clipped downloads, live under `%LOCALAPPDATA%\ExpressPackingMonitoring\cache` and are cleaned by the Web cache limit.
- Web clipping is named “剪辑” / “剪辑并下载”. Do not call it “导出视频”, which can be confused with original video download.

## Destructive File Operation Safety

- Treat deletion of recordings, databases, configuration, update payloads, and generated outputs as concurrency-sensitive. Before deleting, verify the exact file owner, lifecycle state, and current source/target relationship under the same synchronization used to create or replace it.
- A failed task must not delete a shared output merely because that output exists. Another task may have completed successfully and removed or replaced the source before the failed task observes it.
- Keep incomplete-output cleanup inside the owning operation and lock. Only remove an output when the original source is still preserved and the current operation can prove that it created the incomplete file.
- Add a regression test for destructive or replacement logic that exercises the competing-task ordering: task A completes and publishes the target, then task B reaches failure cleanup. The test must verify that task B preserves task A's valid target.
- Prefer recoverable cleanup or explicit database deletion records where practical. Log the reason and exact target for every automatic deletion of material data.

## Coding Style & Naming Conventions

Use C# with nullable references and implicit usings enabled. Follow the existing WPF/MVVM style: `PascalCase` for public types, properties, and commands; `camelCase` for locals; `_camelCase` for private fields. Keep XAML names descriptive and aligned with their backing view or view model. Preserve UTF-8 text and avoid broad line-ending or encoding churn, especially in Chinese strings, XAML, HTML, and userscript files. UI copy should not be followed by a Chinese period or English period at the end of the sentence.

## Testing Guidelines

`ExpressPackingMonitoring.Tests/` contains the automated regression suite. At minimum, run `dotnet test ExpressPackingMonitoring.Tests/ExpressPackingMonitoring.Tests.csproj -c Debug` and `dotnet build ExpressPackingMonitoring.sln -c Debug` before committing. For recording, Web playback, TTS, packaging, or FFmpeg changes, also run the affected workflow manually and note what was verified. Use `Test/HTML/` pages when validating userscript parsing behavior.

Before every release, run `pwsh -NoProfile -File Tools/Test-Release-Automated.ps1`; packaging remains blocked unless the automated checks pass. The real-device scenarios in `RELEASE_CHECKLIST.md` are recommended but non-blocking, and any unverified scenarios must be reported with the release. Do not pass `-ConfirmManualCoreChecks` unless those real-device checks were actually performed.

Before declaring a release ready, perform an explicit release-readiness audit in addition to running the automated checks. Review the complete change set since the previous release and trace the affected critical paths for omitted requirements, unresolved defects or TODOs, newly introduced technical debt, performance or resource-lifetime regressions, and concurrency or race hazards, especially around recording, updates, enrollment, backup, deletion, and file replacement. Investigate every failing or flaky test instead of dismissing it as unrelated, and treat any credible correctness, data-safety, compatibility, performance, or race issue as a release blocker until it is fixed or the user explicitly accepts a documented exception. A successful build or test run alone is not sufficient to declare the release ready.

## Cross-Device Backup Compatibility

- Treat every change to device enrollment, backup authentication, upload, or verified-receipt behavior as a two-sided protocol change. Hosts and clients must exchange explicit protocol, enrollment, authentication, application-version, and build capabilities instead of inferring compatibility from a display version alone.
- Reject an incompatible client before showing the host approval prompt or issuing, rotating, or persisting a device token. Return a structured upgrade response that identifies which side must update, the minimum compatible version, and a trusted download location.
- An incompatible host must be rejected before a phone or RecordingWorkstation requests a token. Compatibility failure may block connection and backup, but must never delete or reset local recordings, databases, upload queues, stable device IDs, or the last-host hint.
- Keep concrete minimum versions and protocol numbers in the centralized compatibility policy code, not in this document. Update desktop and mobile regression tests together whenever that policy or the wire contract changes.
- Release a compatible client package before publishing a host version that raises the client minimum. Verify both upgrade directions and a newer-but-compatible peer before release.

## Commit & Pull Request Guidelines

Recent history uses conventional prefixes with Chinese subjects, for example `fix: 优化 Web 搜索和转码确认` and `docs: 优化 README 表述`. Keep commits scoped and include a short body explaining what changed and why. Do not include secrets, local paths, account IDs, signing files, or machine-specific details.

Pull requests should include a concise summary, validation steps, linked issue if applicable, and screenshots or recordings for UI, playback, or packaging changes.

## Security & Configuration Tips

Do not commit generated configs, databases, logs, caches, recordings, `.env` files, certificates, or signing material. Runtime data belongs under `%LOCALAPPDATA%\ExpressPackingMonitoring\`; release packages should not include local user state.

## UI 字体规范

- 默认禁止使用与现有 UI 不同的字体（`FontFamily`），新界面一律复用项目默认字体（`Microsoft YaHei UI, Segoe UI`）和现有字号/字重风格；确需使用其他字体时必须显式设置并说明原因。
