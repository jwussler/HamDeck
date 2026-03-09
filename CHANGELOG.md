# Changelog

## v2.1 — 2026-03-08

Audio streaming, dashboard integration, and performance improvements.

### Added
- WebSocket audio streaming: live radio audio to any browser via PCM-over-WebSocket
- Built-in audio player page at `/audio` on the dashboard port (5002)
- Audio player card embedded in the main web dashboard
- Web Audio API playback with level meter, volume control, and listener count
- AudioRecorder shares single capture with AudioStreamer (zero device contention)
- FlexKnob flood protection: batched volume/RIT commands, 50ms throttle interval
- FlexKnob `IsActive` guard suppresses UI serial polling during knob rotation
- Inno Setup installer auto-registers HTTP URL ACLs and Windows Firewall rules
- Installer cleanup on uninstall (removes URL ACLs and firewall rules)

### Fixed
- WebSocket race condition: unified send loop prevents concurrent SendAsync errors
- Thread pool starvation: audio sends use LongRunning tasks, never block thread pool
- Bounded send queue drops oldest audio chunks for slow clients instead of freezing app
- FlexKnob volume/RIT no longer send individual serial commands per tick

### Changed
- Audio stream sample rate uses `RecordSampleRate` from config (shared with recorder)
- Dashboard port (5002) now handles WebSocket upgrades for audio streaming
- `PrivilegesRequired` changed to `admin` in Inno Setup for netsh commands

## v2.0 — 2026-03-08

Complete rewrite from Go/Fyne to C#/WPF. Production-ready daily-driver release.

### Added
- TCP CAT proxy on port 4532 for N1MM and other logging software — eliminates need for virtual serial port splitters
- Proxy-fed UI cache — GUI stays live during heavy external polling by parsing proxy traffic instead of competing for the serial lock
- PTT-triggered audio recording with ring buffer lookback and QSY detection
- DX cluster window with JSON API polling, band/mode/continent filtering, auto-band tracking, and double-click-to-tune
- Wavelog integration: REST API push, click-to-tune HTTP server (port 54321), WebSocket live updates (port 54322)
- FlexKnob USB rotary encoder with frequency snap-to-step-boundary
- KMTronic network relay control for RX antenna switching
- TG-XL remote tuner and amplifier tune cycle support
- REST API with 100+ endpoints for Stream Deck and automation
- Digit-by-digit frequency entry via API (Stream Deck numpad workflow)
- Band presets, mode switching, power presets, filter toggles via API
- System tray minimization with proper exit handling
- Auto-reconnect on radio disconnect
- Session statistics (QSY count, TX count, TX time)
- Inno Setup installer for one-click deployment
- Self-contained .NET 8 publish — no runtime installation needed

### Architecture
- Migrated from Go/Fyne to C#/WPF on .NET 8
- Thread-safe RadioController with single serial lock shared between UI, API, proxy, and FlexKnob
- COM port cleanup: `Close()` + `Dispose()` + 100ms delay for Windows handle release
- FlexKnob race condition resolved with timestamp-based polling suppression

### Known Limitations
- Windows only (WPF dependency)
- Single radio support (FTDX-101MP CAT protocol)
- Amp tune endpoint uses configurable but per-call parameters (no saved profiles yet)
