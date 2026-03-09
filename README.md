# HamDeck v2.1

**Ham Radio Control Hub for Yaesu FTDX-101MP**

By WA0O — C# / WPF / .NET 8

## Overview

HamDeck is a Windows desktop application that provides centralized control of a Yaesu FTDX-101MP transceiver. It combines CAT serial control with a REST API, USB knob controller, DX cluster, audio recording, N1MM integration, and Stream Deck support into a single lightweight application.

## Installation

1. Download `HamDeck-v2.1-Setup.exe` from the [Releases](https://github.com/jwussler/HamDeck/releases) page
2. Run the installer — no .NET installation required (self-contained)
3. Launch HamDeck from the Start Menu or Desktop shortcut
4. Click **Settings** to configure your COM port and baud rate
5. Click **Connect**

Config is saved to `%USERPROFILE%\.hamdeck\config.json`

### Portable Install

Extract the zip to any folder and run `HamDeck.exe` directly. Right-click the zip → Properties → Unblock before extracting if Windows blocks it.

## Features

### Radio Control

* Full CAT command set for the FTDX-101MP over serial (38400 baud, 8N2)
* Band, mode, VFO, power, split, and filter controls from the GUI
* S-meter, power meter, SWR, and ALC readback
* Antenna switching (ANT 1/2/3, RX ANT)
* Preamp, attenuator, AGC, NB, NR, notch, VOX, compressor controls
* RIT/XIT with offset adjustment
* CW speed, pitch, break-in, and memory keyer
* Dial lock

### TCP CAT Proxy (N1MM / Logger Integration)

* Exposes the radio's CAT port over TCP on `localhost:4532`
* Allows N1MM, Log4OM, or any CAT-over-TCP client to share the radio without virtual serial port splitters
* Proxy responses are parsed in real-time to keep the HamDeck GUI updated — zero UI stall even under heavy external polling

### FlexKnob USB Controller

* USB rotary encoder support for hands-on VFO tuning, volume, and RIT
* Frequency snaps to clean step boundaries (10/50/100/500/1k/5k/10k Hz)
* Mode cycling and step size adjustment via button presses
* Supports both FlexKnob native protocol and legacy encoder protocol

### DX Cluster

* Polls the WA0O JSON spot API with configurable interval
* Band/mode/continent filtering with auto-band tracking
* Double-click-to-tune with automatic mode setting
* DXCC entity and flag display

### Audio Recording

* PTT-triggered auto-record with configurable timeout
* Ring buffer for lookback capture (catch the start of a QSO you forgot to record)
* QSY detection auto-saves when you change frequency mid-recording
* Organized file storage by date
* Manual record/stop and instant replay via GUI or API

### Wavelog Integration

* Real-time frequency/mode/power updates to Wavelog via REST API
* Click-to-tune HTTP server on port 54321 — click a log entry in Wavelog and the radio tunes
* WebSocket server on port 54322 for live push updates

### REST API (Port 5001)

Over 100 endpoints for complete remote control. Compatible with Stream Deck (via API Ninja plugin), automation scripts, and custom integrations.

Key endpoint groups:

| Group | Endpoints | Description |
| --- | --- | --- |
| Status | `/api/status`, `/api/health`, `/api/meters` | Radio state, S-meter, SWR, ALC |
| Frequency | `/api/freq/{digit}`, `/api/freq/send`, `/api/freq/get` | Digit-by-digit entry (Stream Deck numpad) |
| Band | `/api/band/{160m-6m}` | Direct band selection |
| Mode | `/api/mode/{usb,lsb,cw,am,fm,data}` | Mode switching |
| Power | `/api/power/{qrp,low,mid,high,max}` | Power presets (5/25/50/100/200W) |
| VFO | `/api/vfo/{a,b,swap}`, `/api/vfo-copy/{a2b,b2a}` | VFO control |
| Split | `/api/split/{on,off,toggle}`, `/api/quick-split` | Split operation |
| Recording | `/api/record/{start,stop,replay,status}` | Audio recording |
| Tuners | `/api/tune`, `/api/tune/tgxl`, `/api/tune/amp` | ATU, TG-XL, amp tune |
| Antenna | `/api/ant/{1,2,3,toggle}`, `/api/rxant/{get,cycle}` | TX/RX antenna switching |
| PTT | `/api/ptt/{on,off}` | Transmit control |
| RIT/XIT | `/api/rit/{on,off,up,down,clear}`, `/api/xit/{on,off}` | RIT/XIT control |
| Volume | `/api/volume/{get,up,down}`, `/api/mute/{on,off,toggle}` | AF gain |
| Filters | `/api/nb`, `/api/nr`, `/api/notch`, `/api/width` | DSP filters |
| CW | `/api/cw-speed/{get,up,down}` | Keyer speed |
| Presets | `/api/preset/{40cw,40ssb,20cw,20ssb,...}` | Quick band/mode combos |

### Tuner Support

* **Internal ATU** — one-click tune via CAT
* **TG-XL** — network-controlled remote antenna tuner
* **Amp Tune** — automated amplifier tune cycle (configurable power/duration)

### KMTronic Relay Control

* Network-controlled relay board for RX antenna switching
* Cycle through antenna ports via API or GUI

### System Integration

* Auto-connect on startup
* Minimize to system tray with proper exit handling
* Auto-reconnect on radio disconnect
* Session statistics (QSY count, TX count, TX time)

## N1MM Setup

1. In HamDeck Settings, enable the CAT proxy (default port 4532)
2. In N1MM: Configure Ports → Add → Type: **TCP** → Host: `127.0.0.1` → Port: `4532`
3. Both N1MM and HamDeck will have live radio data simultaneously

## Stream Deck Setup

1. Install the **API Ninja** plugin from the Stream Deck Store
2. Configure buttons to call `http://localhost:5001/api/{endpoint}`
3. Use GET requests — no authentication required

## Configuration

All settings are in `%USERPROFILE%\.hamdeck\config.json` and editable via the Settings dialog.

| Setting | Default | Description |
| --- | --- | --- |
| `radio_port` | — | COM port for FTDX-101MP |
| `radio_baud` | 38400 | Serial baud rate |
| `api_port` | 5001 | REST API listen port |
| `cat_proxy_port` | 4532 | TCP CAT proxy port |
| `flexknob_port` | COM13 | FlexKnob serial port |
| `flexknob_baud` | 9600 | FlexKnob baud rate |
| `cluster_api_url` | wa0o.com | DX cluster JSON API URL |
| `cluster_poll_interval` | 30 | Cluster poll seconds |
| `ptt_record_enabled` | true | Auto-record on PTT |
| `ptt_record_seconds` | 60 | Auto-record timeout |
| `wavelog_url` | — | Wavelog API base URL |
| `tgxl_host` | 192.168.40.51 | TG-XL tuner IP |
| `kmtronic_host` | 192.168.40.69 | KMTronic relay IP |

## Building from Source

Requires Visual Studio 2022 and .NET 8 SDK.

```
git clone https://github.com/jwussler/HamDeck.git
cd HamDeck
dotnet publish -c Release -r win-x64 --self-contained
```

To build the installer (requires [Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```
build-installer.bat
```

Output: `installer\HamDeck-v2.1-Setup.exe`

## Architecture

```
HamDeck/
├── Services/
│   ├── RadioController.cs    # CAT serial commands + cached state
│   ├── TcpCatProxy.cs        # TCP CAT proxy for N1MM
│   ├── ApiServer.cs          # REST API (100+ endpoints)
│   ├── FlexKnobController.cs # USB rotary encoder
│   ├── DxClusterClient.cs    # DX spot polling
│   ├── AudioRecorder.cs      # PTT recording + ring buffer
│   ├── WaveLogServer.cs      # Wavelog HTTP/WS bridge
│   ├── Tuners.cs             # TG-XL + Amp tune
│   ├── KmtronicService.cs    # RX antenna relay
│   ├── Keyers.cs             # CW/voice keyer
│   └── Logger.cs             # File + console logging
├── Views/
│   ├── MainWindow.xaml(.cs)  # Main GUI
│   ├── DxClusterWindow.cs    # DX cluster panel
│   └── SettingsDialog.xaml   # Configuration UI
├── Models/
│   ├── Config.cs             # JSON config model
│   ├── DXSpot.cs             # Cluster spot model
│   └── SessionStats.cs       # Operating statistics
└── Helpers/
    └── BandHelper.cs         # Band/frequency utilities
```

## License

MIT License — see [LICENSE](LICENSE) for details. 73 de WA0O!
