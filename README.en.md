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
<a href="https://github.com/PackingProof/PackingProof-Mobile/releases/latest">
  <img src="https://img.shields.io/badge/Download-Android-695647?style=for-the-badge&logo=android&logoColor=white" height="38" alt="Download for Android">
</a>

<br><br>

[简体中文](README.md) · English

<br>

[![GitHub Stars](https://img.shields.io/github/stars/PackingProof/PackingProof-Desktop?style=flat-square&color=E7B65C)](https://github.com/PackingProof/PackingProof-Desktop)
[![Downloads](https://img.shields.io/github/downloads/PackingProof/PackingProof-Desktop/total?style=flat-square&color=D97745)](https://github.com/PackingProof/PackingProof-Desktop/releases)
[![License](https://img.shields.io/github/license/PackingProof/PackingProof-Desktop?style=flat-square&color=695647)](LICENSE)

</div>

<br>

![PackingProof application](Image/软件截图.jpg)

---

## Why PackingProof

Conventional surveillance may show that a parcel was packed, but finding the video for one specific order is often difficult.

PackingProof links the **tracking number, order details, and packing recording**:

> Scan the shipping label to start recording, then stop and save the video when packing is complete.
> When evidence is needed, enter the tracking number to retrieve the recording.

PackingProof also surfaces special instructions, warns about duplicate tracking numbers, and helps stop refunded orders before shipment.

## Core Features

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

## Workflow

<div align="center">

**Scan the shipping label**

↓

**Start recording automatically**

↓

**Announce order notes and verify refund status**

↓

**Finish packing and stop recording**

↓

**Search and play by tracking number**

</div>

Camera recognition and a keyboard-mode scanner can be used together without changing the existing packing workflow.

## Workstation Roles

On first launch, two simple questions help select the purpose of the current computer.

| Role | Recommended use |
| --- | --- |
| **Record and store on this computer** | One packing station with long-term local storage |
| **Record and store on another computer** | Multiple recording PCs uploading to one host |
| **Recording file backup host** | Central receiver for phones and other recording PCs |
| **Connect to a host for viewing only** | Search, playback, and management without local recording |

A recording workstation remains usable before a host is bound or while its host is offline.

Completed videos remain in a local cache and upload automatically after connectivity returns. Cache cleanup considers a file only after the host has confirmed that it was received and verified in full.

## Quick Start

### 1. Prepare the Hardware

* A Windows 10 or Windows 11 x64 computer
* A USB camera
* A microphone, optional
* A keyboard-mode barcode scanner, optional but recommended

### 2. Install PackingProof

The recommended download is:

```text
PackingProof_Setup_vX.Y.Z.exe
```

The installer does not require administrator rights. It installs for the current user and creates a Start menu shortcut.

### 3. Complete First-Time Setup

After the first launch:

1. Choose the purpose of this computer.
2. Select the camera and microphone.
3. Choose a recording storage or cache location.
4. Connect a recording storage host if needed.
5. Place the shipping-label barcode inside the guide in the center of the preview.
6. When packing is complete, use the Stop button in the main window to end recording.

Recording starts automatically after the barcode is recognized.

### 4. Find a Recording

Open the recording list and enter a tracking number.

Recordings can also be played from a phone or another computer through the LAN Web interface.

## LAN Playback

The local-recording and recording-file-backup-host roles can run the LAN Web service.

1. Open **Connect phone/PC** in PackingProof.
2. Scan the recording Web QR code with a phone.
3. Alternatively, open the displayed address from another device on the same LAN.
4. Enter a tracking number to search and play recordings.

The Web interface can also keep a selected time range and download the resulting clip.

Allow LAN access if Windows Firewall prompts you.

![LAN Web playback](Image/WebService.jpg)

## Order Notes and Refund Interception

This feature uses the browser userscript included with PackingProof.

### Basic Setup

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

## Recording Storage and Cache

Long-term local recording can use multiple storage locations.

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

## Choosing a Download

| File | Purpose |
| --- | --- |
| `PackingProof_Setup_vX.Y.Z.exe` | Recommended for most users |
| Full `.7z` | Smaller portable package |
| Full `.zip` | Native Windows extraction and recovery |
| `ExpressPackingMonitoring_AppPatch_vX.Y.Z.zip` | Manual main-application update |
| `PackingProof_LauncherPatch_vX.Y.Z.zip` | Manual root-launcher update |

Official packages normally include the required .NET runtime and FFmpeg, so no separate installation is needed.

## Updating

For daily use, start PackingProof from:

* The Start menu or desktop shortcut created by Setup
* The root `ExpressPackingMonitoring.exe` in the installation directory

The launcher checks for verified incremental updates in the background and installs a pending update on the next launch.

<details>
<summary><strong>Show manual update and recovery instructions</strong></summary>

<br>

### Update the Main Application Manually

Download and fully extract:

```text
ExpressPackingMonitoring_AppPatch_vX.Y.Z.zip
```

Then run:

```text
双击更新主程序.cmd
```

The script validates the patch, locates the existing installation, rolls back on failure, and preserves configuration, databases, and recordings.

### Update the Root Launcher Manually

Download and fully extract:

```text
PackingProof_LauncherPatch_vX.Y.Z.zip
```

Then run:

```text
双击更新启动器.cmd
```

This script replaces only the root launcher and retains a verified backup of the previous launcher.

### Upgrade an Older Installation

If the installed version is below the AppPatch baseline, run the newer Setup for an in-place upgrade. The full ZIP can be used for recovery.

Do not delete:

```text
%LOCALAPPDATA%\ExpressPackingMonitoring\
```

This directory contains application settings, databases, and recording records.

</details>

## Uninstalling and Preserving Data

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

## Running from Source

Development requires:

* .NET 8 SDK
* FFmpeg, with an Essentials build recommended
* Windows 10/11 x64

```bash
git clone https://github.com/PackingProof/PackingProof-Desktop.git
cd PackingProof-Desktop
```

Open and build the solution with Visual Studio, Rider, or the `dotnet` CLI.

## Feedback and Contributions

Report problems or suggest features through [GitHub Issues](https://github.com/PackingProof/PackingProof-Desktop/issues).

Contributions to testing, documentation, code, and real-world usage guidance are welcome. If PackingProof is useful to you, consider starring the repository so more sellers can discover it.

## License

PackingProof is open source under the [AGPL-3.0 License](LICENSE).

You may use, study, and modify the project at no cost under the license. Distributing a modified version or providing it as a network service requires compliance with the corresponding AGPL-3.0 source-sharing obligations.

---

<div align="center">

<img src="Image/场景图.jpg" alt="PackingProof packing station">

<br><br>

**Make every parcel easy to trace back to its packing record.**

</div>
