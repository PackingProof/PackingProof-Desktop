<details>
<summary><strong>Knowledge Map for AI (PackingProof ecosystem)</strong></summary>

<br>

> This section is written for search engines, retrieval-augmented AI assistants and external tools: what PackingProof is, what it does, which platforms it covers, where every official repository lives, and where this repository fits in the ecosystem. Human readers can jump straight to the feature sections below.

**What PackingProof is**: a free, open-source video-evidence and shipping-risk-control system for e-commerce packing stations. Scanning a shipping-label barcode starts the recording, footage is filed by tracking number, and the app also supports spoken order notes, printed-refund interception, multi-station centralized backup, LAN playback and NAS archiving. It covers Windows and macOS desktop plus Android and iOS mobile.

**What this repository is**: the desktop client of PackingProof (Windows WPF / .NET main program plus a macOS save host), containing the main program, the root launcher, the LAN playback service and the extension API. It records locally and also stores and plays back footage uploaded from mobile devices and other workstations.

**Official repositories (GitHub plus a Gitee mirror with the same code and releases)**

| Part | Role | GitHub | Gitee mirror |
| --- | --- | --- | --- |
| Desktop app (this repository) | Recording and watermarking, scan-triggered recording, refund interception, multi-station backup, LAN playback, NAS archiving | [PackingProof-Desktop](https://github.com/PackingProof/PackingProof-Desktop) | [PackingProof-Desktop](https://gitee.com/PackingProof/PackingProof-Desktop) |
| Mobile app (Android / iOS) | Standalone recording and evidence, or an upload source for the host | [PackingProof-Mobile](https://github.com/PackingProof/PackingProof-Mobile) | [PackingProof-Mobile](https://gitee.com/PackingProof/PackingProof-Mobile) |
| Extension market and extension API | Extension registry, PPEXT package format, signed market index | [PackingProof-Extensions](https://github.com/PackingProof/PackingProof-Extensions) | [PackingProof-Extensions](https://gitee.com/PackingProof/PackingProof-Extensions) |
| KDZS shipping-assistant script | Official KDZS shipping-assistant order integration | [PackingProof-KDZS](https://github.com/PackingProof/PackingProof-KDZS) | [PackingProof-KDZS](https://gitee.com/PackingProof/PackingProof-KDZS) |
| QQ bot | Look up footage by tracking number in QQ private chats or groups and send the video back | [PackingProof-QQBot](https://github.com/PackingProof/PackingProof-QQBot) | [PackingProof-QQBot](https://gitee.com/PackingProof/PackingProof-QQBot) |
| Enterprise / partner adapters | Kuaimai (快麦) ERP adapter, WeCom (企业微信) bot, etc., plugged in as extensions | — | — |

**Platform support**

| Platform | Status | How to get it |
| --- | --- | --- |
| Windows desktop (this repository) | Released | [GitHub Releases](https://github.com/PackingProof/PackingProof-Desktop/releases) · [Gitee Releases](https://gitee.com/PackingProof/PackingProof-Desktop/releases) |
| macOS desktop (this repository, Apple Silicon) | Released: save host and viewer | [GitHub Releases](https://github.com/PackingProof/PackingProof-Desktop/releases) · [Gitee Releases](https://gitee.com/PackingProof/PackingProof-Desktop/releases) |
| Android mobile | Released, signed APK | [GitHub Releases](https://github.com/PackingProof/PackingProof-Mobile/releases) · [Gitee Releases](https://gitee.com/PackingProof/PackingProof-Mobile/releases) |
| iOS mobile | Same feature set as Android, distributed via TestFlight | [Join the beta](https://testflight.apple.com/join/KR4qNs6t) |
| Backup download (mainland China) | Baidu Netdisk: full desktop installer | [Baidu Netdisk](https://pan.baidu.com/s/1B9L9l19ZkjtNpK_9rVZxbw?pwd=6666) (access code 6666) |

> **Mainland China / restricted networks**: when GitHub is slow or unreachable, use the Gitee mirror above to clone the source, open issues or download releases. The Gitee release ships `PackingProof_Setup_no-runtime_vX.Y.Z.exe` (no .NET runtime, about 60 MB), which needs [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0) installed first; the full installer that bundles the runtime is also mirrored on Baidu Netdisk above.

> **The mobile app runs standalone**: one phone alone can record, recognize shipping-label barcodes and look footage up by tracking number, with no PC required; connecting it to a PC adds LAN auto-backup and spoken order alerts. Because of app-store filing requirements in mainland China, Android and iOS are not listed in the app stores yet and ship as a signed APK and a TestFlight build.

> **macOS is a feature subset**: it acts either as a save host (receiving footage uploaded by phones and other computers, serving web playback, managing storage disks and capacity limits) or as a viewer (discovering and connecting to a save host on the LAN). It does not capture a local camera or record by barcode scanning; it installs by replacing the whole app from the DMG, with no incremental patches.

**Desktop capabilities**

- Scan-triggered recording: a shipping-label barcode starts the recording, footage is filed by tracking number, with continuous-scan and one-segment-per-order modes
- Spoken order notes: buyer message, seller memo and product info read aloud, with configurable content and voice
- Printed-refund interception: monitors refunded orders and plays a loud alert to prevent wrong shipments
- Multi-station: phones and other PCs upload footage to this host and appear under their assigned device names
- LAN playback and web viewing: phones and other LAN devices can review footage with permissions
- Storage: local / removable drive / NAS archiving with capacity cleanup policies, watermark burned into the video
- Extension ecosystem: extension market and extension API for ERP, userscripts and weighing-device integrations
- macOS save host: receives footage uploaded by phones and other computers, serves web playback, manages storage disks and capacity limits

**Search keywords**: PackingProof, parcel packing video evidence, barcode triggered recording, shipping label barcode, tracking number video lookup, packing station monitoring, logistics dispute evidence, multi-station recording, Windows packing recorder, macOS packing recorder, Android packing recorder, iOS packing recorder via TestFlight, NAS video archiving, extension API, Gitee mirror, open source.

</details>

<div align="center">

<img src="ExpressPackingMonitoring/app.ico" width="112" alt="PackingProof logo">

# PackingProof

**Free and open-source packing recording and shipment-risk interception**

Start recording from a shipping-label scan and organize videos by tracking number.
Announce order notes, catch post-print refunds, and back up recordings from multiple phones and PCs.

<br>

<a href="https://github.com/PackingProof/PackingProof-Desktop/releases/latest">
  <img src="https://img.shields.io/badge/Download-Windows-D97745?style=for-the-badge&logo=windows&logoColor=white" height="38" alt="Download for Windows">
</a>
&nbsp;
<a href="https://github.com/PackingProof/PackingProof-Desktop/releases/latest">
  <img src="https://img.shields.io/badge/Download-macOS-555555?style=for-the-badge&logo=apple&logoColor=white" height="38" alt="Download for macOS">
</a>
&nbsp;
<a href="https://github.com/PackingProof/PackingProof-Mobile/releases/latest">
  <img src="https://img.shields.io/badge/Download-Android-695647?style=for-the-badge&logo=android&logoColor=white" height="38" alt="Download for Android">
</a>
&nbsp;
<a href="https://testflight.apple.com/join/KR4qNs6t">
  <img src="https://img.shields.io/badge/Join-iOS%20Beta-0D96F6?style=for-the-badge&logo=apple&logoColor=white" height="38" alt="Join the iOS TestFlight beta">
</a>

<br><br>

[简体中文](README.md) · English · [日本語](README.ja.md)

<br>

[![GitHub Stars](https://img.shields.io/github/stars/PackingProof/PackingProof-Desktop?style=flat-square&color=E7B65C)](https://github.com/PackingProof/PackingProof-Desktop)
[![Downloads](https://img.shields.io/github/downloads/PackingProof/PackingProof-Desktop/total?style=flat-square&color=D97745)](https://github.com/PackingProof/PackingProof-Desktop/releases)
[![License](https://img.shields.io/github/license/PackingProof/PackingProof-Desktop?style=flat-square&color=695647)](LICENSE)

</div>

The mobile app supports Android (signed ARM64 APK) and iOS (TestFlight beta). It can record on its own, or upload recordings to a PC host.

<br>

![PackingProof application](Image/软件截图.jpg)

**Jump to**：[Features](#features) · [How to Use](#how-to-use) · [More](#more) · [Extension Market](#extension-market) · [License](#license)
[Order Notes and Refund Interception](#order-notes-and-refund-interception) · [Workstation Roles](#workstation-roles) · [LAN Playback](#lan-playback) · [Updating](#updating)

---

## Why PackingProof

Conventional surveillance may show that a parcel was packed, but finding the video for one specific order is often difficult.

PackingProof links the **tracking number, order details, and packing recording**:

> Scan the shipping label to start recording, then stop and save the video when packing is complete.
> When evidence is needed, enter the tracking number to retrieve the recording.

PackingProof also surfaces special instructions, warns about duplicate tracking numbers, and helps stop refunded orders before shipment.

## Features

### Scan to Record

Recognize a one-dimensional barcode on a shipping label and start recording automatically.

Keyboard-mode barcode scanners remain supported as the primary input method or as a fallback when camera recognition is unavailable.

### Order Note Announcements

Integrate with Kuaidi Assistant to announce:

* Buyer messages
* Seller notes
* Product information

This helps reduce missed instructions and packing mistakes.

### Post-Print Refund Interception

If an order is refunded after its shipping label has been printed, PackingProof can warn the packer when that label is scanned.

Refund verification runs asynchronously and does not delay recording startup.

### Multiple Phones and PCs

One computer can act as a recording storage host and receive:

* Android phone recordings
* Recordings from other PC workstations
* Recordings made by the host itself

The resulting library can be searched and played across the LAN.

The recording file backup host can also archive recordings to a NAS or network share and switches automatically when a NAS is full.

## Extension Market

PackingProof supports the official [extension market](https://gitee.com/PackingProof/PackingProof-Extensions), which installs userscripts and external adapters. Extensions are published and updated independently of the Desktop installer.

Currently supported extensions include:

* [Express Assistant order integration](https://gitee.com/PackingProof/PackingProof-KDZS): syncs orders, memos, and refund status from the Express Assistant (KDZS) pages
* [PackingProof QQBot](https://gitee.com/PackingProof/PackingProof-QQBot): looks up and sends packing recordings by tracking number in QQ private chats or groups

External adapters must access PackingProof through the user-authorized extension API. They must not read the database, recording directory, or NAS credentials directly. Desktop never runs external programs automatically after installation, and being listed in the market is not a security guarantee for third-party programs.

## How to Use

### Workflow

Scan the shipping label → start recording automatically → announce order notes and verify refund status → finish packing and stop recording → search and play by tracking number.

Camera recognition and a keyboard-mode scanner can be used together without changing the existing packing workflow.

### 1. Prepare the Hardware

* A Windows 10 or Windows 11 x64 computer
* A camera: USB or network/IP (RTSP/RTMP/HTTP streams are supported)
* A microphone, optional
* A keyboard-mode barcode scanner, optional but recommended

### 2. Install PackingProof

Download `PackingProof_Setup_vX.Y.Z.exe` from [GitHub Releases](https://github.com/PackingProof/PackingProof-Desktop/releases) or [Gitee Releases](https://gitee.com/PackingProof/PackingProof-Desktop/releases) and install it. On networks where GitHub is slow, the [Baidu Netdisk backup](https://pan.baidu.com/s/1B9L9l19ZkjtNpK_9rVZxbw?pwd=6666) (access code 6666) works too.

The installer does not require administrator rights. It installs for the current user and creates a Start menu shortcut. See “Choosing a Download” below for which file you need.

### 3. Complete First-Time Setup

After the first launch, the wizard walks you through these steps:

![Purpose selection](Image/询问用途.jpg)

1. Choose the purpose of this computer.
2. Select the camera and microphone.
3. Choose a recording storage or cache location.
4. Connect a recording storage host if needed.

After that, place the shipping-label barcode inside the guide in the center of the preview to start recording, and press Stop in the main window when packing is done.

### 4. Find a Recording

Open the recording list and enter a tracking number.

![Recording search and playback](Image/Replay.jpg)

Recordings can also be played from a phone or another computer through the LAN Web interface.

### Choosing a Download

| File | Purpose |
| --- | --- |
| `PackingProof_Setup_vX.Y.Z.exe` | Recommended for most users |
| `PackingProof_AppPatch_vX.Y.Z.zip` | Manual main-application update |
| `PackingProof_LauncherPatch_vX.Y.Z.zip` | Manual root-launcher update |
| `PackingProof_Setup_no-runtime_vX.Y.Z.exe` | Gitee only: installer without the .NET runtime (~60 MB), requires the .NET 8 Desktop Runtime (x64) first |

Official packages normally include the required .NET runtime and FFmpeg, so no separate installation is needed.

### Order Notes and Refund Interception

This feature uses the browser userscript included with PackingProof.

#### Basic Setup

1. Install Tampermonkey or Violentmonkey.
2. Click **Install order integration** in PackingProof.
3. Follow the guide to install the provided userscript.
4. Open and sign in to the Kuaidi Assistant printing page.

When orders on the printing page change, the script synchronizes their information with PackingProof.

After a shipping label is scanned, PackingProof can announce buyer messages, seller notes, and product information.

<details>
<summary><strong>Show refund verification details</strong></summary>

<br>

To enable post-print refund alerts, keep one signed-in Kuaidi Assistant batch-printing page open.

The userscript creates a dedicated refund verification worker page in the background:

* It does not take focus from the operator.
* Only the worker page changes the official post-print-refund filter.
* The printing page currently used by the operator is not changed automatically.
* The worker has a dedicated title and translucent overlay and should not be operated manually.
* If closed accidentally, it is recreated automatically.

Scanning a tracking number starts recording immediately while refund data is requested asynchronously.

Verification follows this order:

1. Check the current post-print-refund list.
2. If the tracking number is absent, perform an exact historical order lookup.
3. If the lookup fails or the printing workstation is offline, use order data retained in local SQLite storage for the last 90 days.

Duplicate tracking numbers are checked against non-deleted recording records from the last 30 days and do not depend on browser cache.

</details>

When the userscript connects to a new monitor address for the first time, the browser may request cross-origin access. Allow it only after confirming that the destination is this computer or another trusted PackingProof service on the LAN. Reinstalling the script through the in-app guide adds the exact permission required for the current service.

### Workstation Roles

One computer can take on a single job or several at once. The wizard picks a sensible role on first launch, and you can switch it later in settings. The four roles are:

| Role | Recommended use |
| --- | --- |
| **Record and store on this computer** | One packing station with long-term local storage |
| **Record and store on another computer** | Multiple recording PCs uploading to one host |
| **Recording file backup host** | Central receiver for phones and other recording PCs |
| **Connect to a host for viewing only** | Search, playback, and management without local recording |

A recording workstation remains usable before a host is bound or while its host is offline.

Completed videos remain in a local cache and upload automatically after connectivity returns. Cache cleanup considers a file only after the host has confirmed that it was received and verified in full.

### LAN Playback

The local-recording and recording-file-backup-host roles can run the LAN Web service.

1. Open **Connect phone/PC** in PackingProof.
2. Scan the recording Web QR code with a phone.
3. Alternatively, open the displayed address from another device on the same LAN.
4. Enter a tracking number to search and play recordings.

The Web interface can also keep a selected time range and download the resulting clip.

Allow LAN access if Windows Firewall prompts you.

![LAN Web playback](Image/WebService.jpg)

### Packing Statistics

Summarize packing counts, storage usage, accumulated working time, and average time per parcel by day or by week.

![Packing statistics](Image/Statistics.jpg)

## More

### Recording Storage and Cache

Long-term local recording can use multiple storage locations.

The **recording file backup host** role can also add a NAS or network share as a backup location:

* Local disks store recordings directly; network locations keep verified copies only
* Backup locations are used in list order and switch automatically when a NAS is full
* NAS extends the local retention window; when NAS space is low, the oldest archived recordings are cycled automatically (records remain searchable)
* NAS unavailability never blocks local capacity-based cycling; locally cleaned copies without remote confirmation are recorded with a dedicated reason code

When a drive falls below its configured free-space reserve, PackingProof can:

1. Stop writing new recordings to that drive.
2. Switch to the next available storage location.
3. Clean older recordings according to the configured policy.
4. Keep an additional safety reserve on the Windows system drive.

The **record and store on another computer** role uses a separate local cache.

Its default limit is `100 GB`, but that space is not preallocated.

<details>
<summary><strong>Show cache safety rules</strong></summary>

<br>

Usable cache capacity is limited by all of the following:

* The configured cache limit
* Actual free disk space
* The minimum free-space reserve

Under storage pressure, PackingProof removes only recordings that the storage host has already confirmed and verified.

The following files are never removed automatically by cache cleanup:

* Recordings made before a host is bound
* Pending uploads
* Active uploads
* Failed uploads
* Recordings not yet fully confirmed by the host

</details>

### Updating

For daily use, start PackingProof from:

* The Start menu or desktop shortcut created by Setup
* The root `ExpressPackingMonitoring.exe` in the installation directory

The launcher checks for verified incremental updates in the background and installs a pending update on the next launch.

<details>
<summary><strong>Show manual update and recovery instructions</strong></summary>

<br>

#### Update the Main Application Manually

Download `PackingProof_AppPatch_vX.Y.Z.zip`, extract it fully, and run `双击更新主程序.cmd` inside it. The script validates the patch, locates the existing installation, rolls back on failure, and preserves configuration, databases, and recordings.

#### Update the Root Launcher Manually

Download `PackingProof_LauncherPatch_vX.Y.Z.zip`, extract it fully, and run `双击更新启动器.cmd` inside it. This script replaces only the root launcher and retains a verified backup of the previous launcher.

#### Upgrade an Older Installation

If the installed version is below the AppPatch baseline, you skipped too many versions: run the newest Setup to install in place. Configuration, databases, and recordings are kept.

Do not delete `%LOCALAPPDATA%\ExpressPackingMonitoring\` — application settings, databases, and recording records live there.

</details>

### Uninstalling and Preserving Data

The uninstaller provides two independent options:

* Delete settings and temporary files
* Delete recordings and recording records

Both options are cleared by default, so a normal uninstall keeps user settings, databases, and recordings.

<details>
<summary><strong>Show recording deletion safeguards</strong></summary>

<br>

Settings cleanup removes only application settings, logs, and temporary cache. It does not remove recordings, the recording database, or database recovery backups.

Recording cleanup processes only exact files that remain registered in the database and have not changed after confirmation. It never scans and empties an entire recording directory.

Recordings and databases are retained if the database is missing, corrupt, busy, or if any recording deletion fails. Detailed results are written to the uninstall log in the system temporary directory.

</details>

### Running from Source

Development requires:

* .NET 8 SDK
* FFmpeg, with an Essentials build recommended
* Windows 10/11 x64
* macOS 12+ on Apple Silicon, for building the macOS save host

```bash
git clone https://github.com/PackingProof/PackingProof-Desktop.git
cd PackingProof-Desktop
```

On networks where GitHub is slow, use the Gitee mirror instead:

```bash
git clone https://gitee.com/PackingProof/PackingProof-Desktop.git
cd PackingProof-Desktop
```

Open and build the solution with Visual Studio, Rider, or the `dotnet` CLI.

### Feedback and Contributions

Report problems or suggest features through [GitHub Issues](https://github.com/PackingProof/PackingProof-Desktop/issues) or [Gitee Issues](https://gitee.com/PackingProof/PackingProof-Desktop/issues).

Contributions to testing, documentation, code, and real-world usage guidance are welcome. If PackingProof is useful to you, consider starring the repository so more sellers can discover it.

### License

PackingProof is open source under the [AGPL-3.0 License](LICENSE).

You may use, study, and modify the project at no cost under the license. Distributing a modified version or providing it as a network service requires compliance with the corresponding AGPL-3.0 source-sharing obligations.

<details>
<summary><strong>Brand Asset Policy</strong></summary>

<br>

The `PackingProof` name and official application icon are project brand assets. The AGPL-3.0 source-code license does not grant permission to use them as the product identity of a modified version. Public modifications should use a distinct product name and icon, clearly identify themselves as unofficial, and may use “based on PackingProof” to describe their origin. See the [Brand Policy](docs/BRAND_POLICY.md).

</details>

---

<div align="center">

<img src="Image/场景图.jpg" alt="PackingProof packing station">

<br><br>

**Make every parcel easy to trace back to its packing record.**

</div>
