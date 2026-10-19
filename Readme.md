<div align="center">

# 🖥️ NexusInk-server
### Native Windows Ink Receiver & Real-Time Full HD Screen Streamer

[![Download Executable](https://img.shields.io/badge/Download-NexusInk--server.exe-blue?style=for-the-badge&logo=windows)](https://github.com/brandalexdev/Nexus-ink-Server/releases/latest)
[![Status](https://img.shields.io/badge/Status-Stable%20%7C%20Feature--Complete-success?style=for-the-badge)](#)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011%20(x64)-0078D6?style=for-the-badge&logo=windows)](https://microsoft.com/windows)
[![Latency](https://img.shields.io/badge/Latency-%3C1ms%20(USB)-brightgreen?style=for-the-badge)](#-connection-modes)

<p align="center">
  A finished, standalone desktop application that turns your Windows PC into a native <b>Windows Ink</b> drawing display with real <b>1024 pressure levels</b>, <b>tilt sensitivity</b>, and ultra-low latency <b>1080p screen streaming</b> to your tablet.
</p>

> [!NOTE]
> **Standalone Release:** This project is provided as a complete, ready-to-run binary (`.exe`). It is fully self-contained and does not require compiling or installing the .NET runtime.

</div>

---

## 📥 Download & Quick Start

You only need the pre-compiled executable to use this program:

1. **Download:** Get the latest standalone **[`NexusInk-server.exe`](https://github.com/brandalexdev/Nexus-ink-Server/releases/latest)** from the Releases section.
2. **Firewall (One-Time Setup):** Open **PowerShell as Administrator** and execute:
   ```powershell
   New-NetFirewallRule -DisplayName "NexusInk UDP (Stylus & AutoConnect)" -Direction Inbound -Protocol UDP -LocalPort 5000,5001 -Action Allow
   New-NetFirewallRule -DisplayName "NexusInk TCP (HD Screen Stream)" -Direction Inbound -Protocol TCP -LocalPort 5002 -Action Allow
   ```
3. **Run:** Double-click **`NexusInk-server.exe`** (it will request Administrator permissions automatically to inject pen strokes across all apps).
4. **Connect:** Open the tablet client app — it will automatically detect the server and start streaming.

---

## 🌟 Features

* 🎯 **Native Windows Ink Digitizer (`PT_PEN`):** Direct Win32 synthetic pointer injection. Recognized as a true hardware graphics tablet (not a mouse emulation).
* 🖊️ **Pressure & Tilt Dynamics:** 1024 pressure levels, X/Y tilt angle support, stylus hover, and eraser button toggle.
* ⚡ **Ultra-Low Latency Telemetry:** UDP port `5000` with 28-byte binary packets for instantaneous response and hotkeys (`Ctrl+Z`, `Ctrl+Shift+Z`, `Space`, `Alt`, `B`, `E`).
* 📡 **Zero-Config Auto-Discovery:** UDP port `5001` broadcast pulse pairs tablet and PC automatically.
* 📺 **Crisp Full HD Screen Streaming:** TCP port `5002` low-latency screen mirror at 1920×1080 with adjustable JPEG quality (60% – 85%).

---

## 🔌 Connection Modes

### ⚡ Option 1: Wired USB (Best for Professional Drawing — <1 ms Latency)
1. Connect your tablet to the PC via a USB-C cable.
2. Enable **USB Tethering** in your tablet's network settings.
3. Windows creates a direct virtual local network link over the cable.
4. Tap **Auto-Discover** in the tablet app (or enter the USB IP shown on the server).

### 📶 Option 2: Wireless (5GHz Wi-Fi)
1. Connect PC and tablet to the same Wi-Fi network (5GHz strongly recommended).
2. Open the tablet app — it will auto-detect and pair in less than 1 second.
3. Select **Balanced HD (70%)** stream quality for smooth performance.

---

## ⚙️ Architecture & Network Protocols

```
┌────────────────────────┐                    ┌────────────────────────┐
│     TABLET CLIENT      │                    │     WINDOWS 10/11      │
│      (Stylus App)      │                    │  (NexusInk-server)     │
└───────────┬────────────┘                    └───────────┬────────────┘
            │                                             │
            ├──────── UDP 5001 (Auto-Discover) ──────────►│ Pairs in <1s
            │                                             │
            ├──────── UDP 5000 (Stylus Telemetry) ───────►│ Injects PT_PEN
            │         (28-byte packet @ 120Hz)            │ (Zero Latency)
            │                                             │
            │◄─────── TCP 5002 (HD Screen Stream) ────────┤ TCP_NODELAY
            │         (1080p JPEG @ ~35 FPS)              │ Monitor Mirror
```

| Port | Protocol | Purpose | Payload Details |
| :---: | :---: | :--- | :--- |
| **`5000`** | **UDP** | Stylus & Telemetry | 28-byte Big-Endian binary packet ($X, Y$, pressure, tilt) + 4-byte shortcut triggers. |
| **`5001`** | **UDP** | Auto-Discovery | Broadcast pulse (`DISCOVER_MATEPAD_SERVER`) for instant zero-configuration pairing. |
| **`5002`** | **TCP** | Screen Mirroring | Low-latency Full HD (1920×1080) stream with selectable JPEG quality (60% – 85%). |

---

## 🎨 Creative Software Configuration

| Software | Configuration Step |
| :--- | :--- |
| **Adobe Photoshop / Illustrator** | Automatically recognized. Ensure the *"Use Pressure for Size/Opacity"* icon is checked in the top brush bar. |
| **Krita** | Go to **Settings ➔ Configure Krita ➔ Tablet Settings** and select **Windows 8+ Pointer Input (Windows Ink)**. |
| **Clip Studio Paint** | Go to **File ➔ Preferences ➔ Tablet** and select **Tablet PC**. |
| **Blender** | Go to **Edit ➔ Preferences ➔ Input ➔ Tablet API** and set it to **Windows Ink**. |

---

## 💻 System Requirements

* **OS:** Windows 10 (version 1809 or newer) or Windows 11 (x64).
* **Permissions:** Administrator privileges (requested automatically via `app.manifest`).
* **Dependencies:** None. The standalone executable is fully self-contained.

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).