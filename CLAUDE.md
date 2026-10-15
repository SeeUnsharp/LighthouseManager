# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

.NET 10 solution to discover and control Valve Lighthouse 2.0 base stations over Bluetooth LE on **Windows and Linux**, via `InTheHand.BluetoothLE` (WinRT backend on Windows, BlueZ/D-Bus via `Linux.Bluetooth` + `Tmds.DBus` on Linux).

**Design rule:** UIs are decoupled. The service is the single owner of Bluetooth and exposes a documented local HTTP API (`docs/API.md`); the Avalonia tray app is just one client of it and must not reference the service or Core. Keep `docs/API.md` in sync with `LighthouseManagerService/Api.cs` and the tray's client-side DTO copies (`LighthouseManager.Tray/Api/ApiModels.cs`).

## Build

No linter config. Tests (xUnit) cover the platform independent logic: MAC parsing, power state values, `AutomationPolicy`, settings, the API request guard, the tray tooltip, and an API contract test that the tray's DTO copies match the service's JSON.

```sh
dotnet test tests/LighthouseManager.Tests
dotnet test tests/LighthouseManager.Tests --filter "FullyQualifiedName~AutomationPolicyTests"   # single class
```

Bluetooth itself (`BaseStationManager`, `StationController`) has no automated tests; it's verified against real hardware.

```sh
dotnet build                                     # builds both TFMs for all projects
dotnet run --project LighthouseManager -f net10.0 -- -d   # run CLI on Linux
```

`Directory.Build.props` multi-targets every project (except the tray app, which is `net10.0` only and sets `NoBluetooth`) to `net10.0` (Linux/BlueZ) and `net10.0-windows10.0.19041.0` (WinRT); `EnableWindowsTargeting` lets the Windows TFM build on Linux. **The TFM selects the Bluetooth backend**, so on Windows always use the `-windows` TFM — plain `net10.0` gets the BlueZ implementation.

Release publish (as in `.github/workflows/dotnet-build.yml`, per project and per platform):

```sh
dotnet publish LighthouseManager -c Release -f net10.0 -r linux-x64 -o ./Release \
  -p:PublishSingleFile=true -p:PublishTrimmed=true -p:DebugType=None --self-contained
# Windows: -f net10.0-windows10.0.19041.0 -r win-x64
# Tray app (both platforms -f net10.0): additionally -p:IncludeNativeLibrariesForSelfExtract=true for Skia/HarfBuzz
```

Trimming/single-file pitfalls (both verified to break things):
- The BlueZ stack is reflection based; `Directory.Build.props` roots `InTheHand.BluetoothLE`, `Linux.Bluetooth`, `Tmds.DBus` for the non-Windows TFM. Remove that and Linux fails at runtime.
- Don't add `IncludeAllContentForSelfExtract`: it makes `AppContext.BaseDirectory` point to the extraction cache, so `appsettings.json` and `App_Data/logs` are no longer resolved next to the executable.
- The service uses `EnableConfigurationBindingGenerator` (AppSettings binding), `EnableRequestDelegateGenerator` (minimal APIs), `WebApplication.CreateSlimBuilder` and the source generated `ApiJsonContext` (pass its `JsonTypeInfo` explicitly, e.g. to `TypedResults.Json`) so it survives trimming.
- The service roots `System.Security.Cryptography`: trimming removes `X509Certificate2Collection.GetEnumerator`, and Kestrel's config loader then fails at startup with a `TypeLoadException` (even for plain HTTP).

## Architecture

- **LighthouseManager.Core** (library): `BaseStationManager` does everything Bluetooth: discovery, identify, and power state changes (reads current state first and skips the write if already there). Each address runs in parallel with a Polly pipeline: retry (10 × 500 ms) around a 20 s timeout per attempt (BLE calls can hang; attempts use the pipeline's token, not the caller's). Public methods return `bool` success instead of throwing; cancellation is logged as "Cancelled", not as a failure. Every BLE call gets `WaitAsync(ct)` because InTheHand's GATT methods take no token (an abandoned write can still reach the station). GATT UUIDs/byte values are in `Lighthouse.cs`. `AppLogging` holds the shared Serilog setup for both executables.
- **Addresses** are `ulong` internally; `MacAddress` parses user input strictly (consistent `:`/`-` separators or 12 hex digits; `TryParseList` for lists) and converts to the **platform-specific InTheHand device id** (BlueZ `AA:BB:..`, WinRT plain hex), chosen with `#if WINDOWS` like the backend itself, not at runtime.
- **InTheHand behaves differently per platform**:
  - `Bluetooth.ScanForDevicesAsync` is real BlueZ discovery on Linux (honours `Timeout`, returns partial results instead of throwing when cancelled), but on Windows only `DeviceInformation.FindAllAsync` over already known devices (returns immediately). `RequestLEScanAsync` throws `PlatformNotSupportedException` on Linux. Hence discovery is split with `#if WINDOWS`: the WinRT `BluetoothLEAdvertisementWatcher` in active mode on Windows (the `LHB-` name is in the scan response), a scan loop on Linux.
  - On Linux `BluetoothDevice.FromIdAsync` only resolves devices BlueZ has seen recently, so `GetDeviceAsync` falls back to a 5 s scan. All scans go through `_scanLock` because BlueZ has one discovery session per adapter.
- **LighthouseManager** (CLI): plain `Main` (no generic host) with FluentArgs; parse handlers only capture the command, which runs after parsing. Exit codes `0` success / `1` failure or invalid args are a documented contract (README).
- **LighthouseManagerService** (Web SDK, minimal APIs): `AddWindowsService()` + `AddSystemd()` (both no-ops outside their host). `AppSettings` is validated with `ValidateOnStart`, and fatal errors set exit code 1, so systemd `Restart=on-failure` / Windows recovery see failures.
  - `StationController` is the **only path to `BaseStationManager`**: one Bluetooth operation at a time (`_bluetoothLock`), a cached `StationInfo` per station, and `SubscribeAsync` for live snapshots (full status per event, latest-only per subscriber). Refresh and power commands update each station as soon as its own result arrives (one unreachable station doesn't hold back the others). Operation kinds: *Automatic* (from the watcher; cancelled by manual commands, a newer automatic op, or disabling automatic mode), *Manual* (client power commands; bumps `ManualOverrideVersion`), *Refresh* (state read; cancelled by power commands and rescheduled 10 s later, so a wake never waits behind an unreachable station), *Passive* (identify; just waits its turn). `Publish` snapshots and writes under `_sync`, otherwise an older snapshot can overwrite a newer one in the latest-only channels. Delayed refreshes use `ApplicationStopping`.
  - `AutomationPolicy` (pure, unit tested) decides when to wake/sleep; `Worker` polls for `AppSettings:TriggerProcesses` (comma separated process names, default `vrserver`) every `AppSettings:Interval` ms (polling is deliberate: `Process.Exited` is unreliable for non-child processes on Linux), reports the trigger to the controller, and runs automatic wake/sleep jobs (retried every 30 s, max 10). After a manual command or while automatic mode is off, the current target counts as handled; the watcher acts again only when the trigger state changes. It also does the startup and periodic (`StateRefreshInterval`) state reads. On stop, running operations are cancelled but stations are deliberately **not** put to sleep (don't kill tracking mid-session).
  - `Api.cs`: endpoints, SSE (`TypedResults.ServerSentEvents`), and `UseLocalRequestGuard`: 403 for non-loopback `Host` (DNS rebinding) and for browser `Origin`s not in `AllowedOrigins` (CSRF against the unauthenticated loopback API). An empty `ApiUrl` swaps in `NoListenerServer`; without URLs Kestrel would listen on localhost:5000.
  - Example systemd user unit: `LighthouseManagerService/linux/lighthousemanager.service` (`Type=notify`).
- **LighthouseManager.Tray** (Avalonia 12, CommunityToolkit.Mvvm, compiled bindings): `TrayIcon` + borderless `FlyoutWindow` that hides on deactivation. `MainViewModel.RunEventLoopAsync` follows `/api/events` and reconnects; commands go through `ApiClient`. The tray icon is drawn at runtime (`TrayIconRenderer`), no image assets. Tray APIs don't expose the icon position, so the flyout goes to the right corner on the panel side; Plasma under XWayland reports working area == screen, hence `UnknownPanelMargin`. The window's `MaxHeight` follows the working area and only the station list (`ScrollViewer` in the `DockPanel` fill) scrolls, so any number of stations fits. Tray tooltips are capped at 127 chars (Windows limit), with a per-state summary when the per-station list doesn't fit. `--open` shows the flyout at startup.

Logs go to `<exe dir>/App_Data/logs/*.log`.

## Testing against hardware

Only one process should talk to the base stations at a time — concurrent BlueZ connections from two processes fail with `org.bluez.Error.InProgress` / `br-connection-canceled`. To check the flyout visually, run the tray with `--open` and capture only the flyout's rectangle, never the whole screen (it shows the user's other windows). When testing the service, a fake trigger is any process with a configured name, e.g. `vrserver` (e.g. a copy of `/usr/bin/sleep`). Background jobs in non-interactive shells ignore SIGINT: stop the service with SIGTERM, and test CLI Ctrl+C in the foreground via `timeout -s INT`. Settings can be overridden on the command line, e.g. `--AppSettings:Interval=500`.

Lighthouse 2.0 GATT reference: https://gist.github.com/BenWoodford/3a1e500a4ea2673525f5adb4120fd47c
