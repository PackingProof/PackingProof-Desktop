# <img src="ExpressPackingMonitoring/app.ico" align="left" width="128" height="128"/> Express Packing Monitoring

[简体中文](README.md) | English

[![GitHub Stars](https://img.shields.io/github/stars/PackingProof/PackingProof-Desktop?style=flat&color=ffcf49)](https://github.com/PackingProof/PackingProof-Desktop)
[![GitHub All Releases](https://img.shields.io/github/downloads/PackingProof/PackingProof-Desktop/total)](https://github.com/PackingProof/PackingProof-Desktop/releases)

A packing video and shipment-risk interception tool for e-commerce sellers and packing stations. It records automatically when a shipping barcode is scanned, integrates with Kuaidi Assistant to announce buyer messages and seller notes, and alerts packers when an order is refunded after its shipping label has already been printed.

> It does more than preserve evidence for disputes: it surfaces special instructions and stops refunded orders before shipment, helping prevent packing mistakes and avoidable losses.

![Application screenshot](Image/软件截图.jpg)

## Who It Is For

- Sellers who print shipping labels with Kuaidi Assistant and want to keep their existing workflow
- Packing stations that need to catch refunds occurring after a label has been printed
- Teams that want buyer messages, seller notes, and product information announced while packing
- Sellers who need hands-free recording and fast retrieval by tracking number
- Warehouses that need recordings from phones or other recording PCs backed up centrally and played on the LAN
- Users who want to trim the beginning or end of a recording before downloading it
- Computers with limited storage that need automatic cleanup while reserving free disk space

## Main Features

- Integrates with Kuaidi Assistant to sync orders and announce buyer messages, seller notes, and product information
- Checks for post-print refunds asynchronously in shipping and return modes, with status-specific alerts that do not interrupt recording
- Uses the camera to recognize one-dimensional shipping-label barcodes and start recording automatically
- Reads the central guide at a high rate, adds a low-rate full-frame fallback while idle, and restricts recognition to the guide while recording to reduce product-barcode false triggers
- Keeps camera recognition and keyboard-mode scanners available together, so a scanner can remain as a background-input and recovery fallback
- Supports camera recording, audio capture, and video watermarks
- Offers four computer roles through a two-question selector: record and store locally, record and upload to another computer, recording-file backup host, or view-only client
- Keeps a recording workstation usable before its storage host is bound or while the host is offline; completed files remain in a safe local cache and are uploaded later
- Lets a backup host receive recordings from Android phones and other recording PCs
- Searches recordings by order or tracking number and plays them in a browser
- Provides browser-based trim-and-download with a selectable time range
- Supports multiple storage locations, automatic drive switching, and reserve-space-based cleanup
- Keeps multi-location long-term storage separate from the recording workstation's single-location rolling cache
- Checks for updates through the launcher, verifies incremental packages, and installs pending updates on the next launch; both AppPatch and LauncherPatch archives include double-click manual installers

## Requirements

- Windows 10/11 x64
- USB camera
- Barcode scanner configured as a keyboard input device (optional, but recommended as a fallback)

`PackingProof_Setup_vX.Y.Z.exe` is the recommended download. It installs per-user without administrator rights, always adds a Start menu shortcut, and selects the desktop shortcut by default. The full 7z is the smaller portable package, while the full ZIP supports native Windows extraction and recovery. These distributions normally include the required .NET runtime and `ffmpeg.exe`. Running from source requires the .NET 8 SDK and `ffmpeg.exe` (the Essentials build is recommended).

## Quick Start

1. Choose what this computer should do on first launch.
2. For either recording role, select the camera, microphone, and long-term storage or cache location.
3. A recording workstation can start recording immediately and bind its storage host later; a backup host can connect Android phones and other recording PCs.
4. Place the shipping-label barcode inside the guide until it is recognized, or use the existing scanner workflow.
5. Finish the shipment or scan the stop command to end recording.
6. Enter the tracking number in the recording list whenever you need to retrieve the video.

Camera sleep is disabled by default. If it is explicitly enabled in Advanced settings, click the application, press a key, or use the scanner to wake the camera before placing a label inside the guide.

## Updating

- Start the app from an installer-created shortcut or the root `ExpressPackingMonitoring.exe`. The launcher downloads verified incremental packages in the background and installs them on the next launch.
- To update the main application manually, extract `ExpressPackingMonitoring_AppPatch_vX.Y.Z.zip` completely and run `双击更新主程序.cmd`. The script validates every patched file, locates the existing installation, and rolls back on failure without deleting configuration, database records, or recordings.
- To update the root launcher manually, extract `PackingProof_LauncherPatch_vX.Y.Z.zip` completely and run `双击更新启动器.cmd`. It replaces only the root entry executable and retains a verified launcher backup. Automatic updates do not require downloading either archive manually.
- If the installed version is below the patch baseline, run the newer Setup for an in-place upgrade. The full ZIP is the recovery alternative. Existing portable folders are never migrated or removed automatically. Keep `%LOCALAPPDATA%\ExpressPackingMonitoring\` to preserve configuration and database records.

## Uninstalling and Data

- Uninstall keeps configuration, database, logs, cache, and recordings by default.
- Local application data and database-registered recording files are separate, default-No choices.
- Recording deletion shows the exact count and total size before a second confirmation. It deletes only unchanged files still registered in the database and never scans or clears recording directories.
- If the database is missing, corrupt, busy, or any recording cleanup fails, recordings and local data are retained. Details remain in the uninstall log under the system temporary directory.

## LAN Playback

1. Run the app in the local-recording or recording-file-backup-host role.
2. Open “Connect phone/PC” and scan the recording Web QR code, or open the displayed address from another device on the same LAN.
3. Android phones can scan the separate app-download QR code; mobile browsers also show a download entry at the top of the Web page.

Allow network access if Windows Firewall prompts you.

![LAN Web playback](Image/WebService.jpg)

## Order Note Announcements

This feature uses the included browser userscript:

1. Install Tampermonkey or Violentmonkey.
2. Click “Install order integration” in the application and follow the guide to install the included userscript.
3. When the printing page opens or its orders change, the script sends the current order information to the monitoring workstation automatically. Normal order syncing does not depend on the refund worker page.
4. The monitoring workstation can announce buyer messages, seller notes, and product information.
5. To enable post-print refund alerts, keep one signed-in Kuaidi Assistant batch-printing page open. The script opens a background refund verification worker without taking focus. Only this worker changes the official post-print-refund filter; the page being used by the operator is not changed.
6. After a scan, recording starts immediately while refund data is requested asynchronously. The worker first returns the current refund list. If the tracking number is absent, it performs an exact historical lookup. When the printing workstation is offline or the lookup fails, the monitor falls back to order data retained in SQLite for 90 days.

The refund worker has a dedicated title and translucent overlay. Do not operate it manually. If it is closed accidentally, the script recreates it automatically; it can also be reopened from the userscript menu.

When the userscript connects to a new monitor address for the first time, the browser may request cross-origin access. Confirm that the destination is the local computer or a trusted LAN workstation before allowing it. Reinstalling the script through the monitor's setup guide adds an exact permission for the current workstation.

Duplicate tracking numbers are checked against non-deleted recording records from the last 30 days, independently of the browser cache. Order and refund caches are stored in SQLite. The legacy `orderinfo_cache.json` file is migrated during an upgrade and removed afterward.

## Recording Storage

Configuration, databases, logs, and recordings are stored in the current user's local data directories. Existing settings and recording records are preserved during normal upgrades as long as the user data is not deleted.

Long-term storage settings represent reserved free space, not a recording quota. When a drive falls below its reserve threshold, the application stops writing new recordings to that drive and prefers the next configured location. The system drive automatically receives a larger safety reserve to protect Windows and other applications.

The “record and upload to another computer” role uses a local rolling cache. Its default 100 GB limit does not preallocate disk space; the effective safe capacity is also constrained by actual free space and the drive reserve. Cleanup removes only the oldest recordings already verified by the storage host. Unbound, pending, uploading, or failed recordings are never removed automatically.

## License

This project is open source under the [AGPL-3.0 License](LICENSE).

Personal learning and use in your own store are free. If you distribute a modified version or provide it as a network service, you must comply with the source-sharing requirements of AGPL-3.0.

![Packing station scenario](Image/场景图.jpg)
