# LighthouseManager
Command-Line tool to manage Valve Lighthouse 2.0 base stations

Inspired by [Lighthouse Keeper](https://github.com/rossbearman/lighthouse-keeper#lighthouse-keeper) this is a .NET 10 solution to discover and manage Valve Lighthouse 2.0 Base Stations on Windows and Linux.
It uses [32feet.NET InTheHand.BluetoothLE](https://github.com/inthehandltd/32feet) to communicate with Base Stations (Windows Bluetooth LE API on Windows, BlueZ on Linux).

The solution consists of:
- **LighthouseManagerService**: background service that owns the Bluetooth connection, wakes/sleeps base stations automatically and offers a local [HTTP API](docs/API.md)
- **LighthouseManager.Tray**: tray icon with a flyout to see and control the base stations (a client of the API, so you can build your own UI as well)
- **LighthouseManager**: standalone command-line tool

## Requirements
- Windows 10 64bit (minimum 10.0.19041.0) or Linux x64 with BlueZ 5.50+
- Integrated Bluetooth or dongle managed by the OS

## Usage
With a terminal (Command Prompt / PowerShell on Windows) navigate to the location where LighthouseManager(.exe) is located.

Functions / parameters:

- `-d` or `--discover` Discover new Base Stations to get their MAC-Addresses (stop with Ctrl+C).
- `-i` or `--identify` `-a AA:AA:AA:AA:AA:AA` Identify a base station by flashing the LED. A sleeping base station wakes up from this (firmware behavior) and stays on until you put it to sleep.
- `-r` or `--read` `-a AA:AA:AA:AA:AA:AA,BB:BB:BB:BB:BB:BB` Read the power state of one or more Base Stations
- `-w` or `--wake` `-a AA:AA:AA:AA:AA:AA,BB:BB:BB:BB:BB:BB` Wake one or more Base Stations (write one or more addresses)
- `-s` or `--sleep` `-a AA:AA:AA:AA:AA:AA,BB:BB:BB:BB:BB:BB` Sleep one or more Base Stations (write one or more addresses)

The command-line tool talks to the base stations directly. Don't use it while LighthouseManagerService runs: two processes connecting to the same base stations at the same time fail. Use the service's [API](docs/API.md) instead (e.g. `curl -X POST http://127.0.0.1:47800/api/stations/wake`).

## Exit codes
Based on success or not LighthouseManager will exit with different codes
- `0` All commands executed successfully
- `1` One or more commands failed after given retry attempt, or invalid arguments

# LighthouseManagerService
LighthouseManagerService is a little background service that watches for trigger processes in a given interval (default 1000ms): base stations are woken while any of them runs and put to sleep when all of them exited. By default the trigger is SteamVR (`vrserver`), but any VR runtime or application works.

Settings in appsettings.json (`AppSettings` section):
- `BaseStationAddresses` comma separated Base Station MAC addresses (you can discover them with LighthouseManager `--discover` parameter)
- `TriggerProcesses` comma separated process names, default `vrserver`. Examples: `vrserver` (SteamVR), `monado-service` (Monado), `wivrn-server` (WiVRn)
- `Interval` polling interval in milliseconds (minimum 1000)
- `AutomaticMode` whether trigger processes wake/sleep the base stations (default `true`, can be toggled at runtime in the tray app / API)
- `StateRefreshInterval` how often the power state of the base stations is read, in seconds (default `300`, `0` = off)
- `ApiUrl` address of the [HTTP API](docs/API.md) (default `http://127.0.0.1:47800`, empty = off). The API has no authentication, keep it on loopback
- `AllowedOrigins` comma separated browser origins allowed to use the API, for web based UIs (default: none)

A manual wake/sleep (tray app, API) always wins: automatic handling only kicks in again when a trigger process starts or the last one exits.

It is possible to just run LighthouseManagerService, or start it automatically: as a scheduled task or Windows Service on Windows, as a systemd unit on Linux.

## Autostart on Windows (scheduled task, recommended)
Runs the service in the background when you log in, under your own user. No administrator rights needed. Run in PowerShell (adjust `$dir` to where you extracted the files):
```powershell
$dir = "C:\PATHTOEXTRACTEDFILES"
$action = New-ScheduledTaskAction -Execute "$dir\LighthouseManagerService.exe" -WorkingDirectory $dir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit 0 -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName "LighthouseManager" -Action $action -Trigger $trigger -Settings $settings
Start-ScheduledTask -TaskName "LighthouseManager"
```
`-ExecutionTimeLimit 0` is required, otherwise Windows stops the task after 3 days. Logs are in `App_Data\logs` next to the executable.
Uninstall with `Unregister-ScheduledTask -TaskName "LighthouseManager" -Confirm:$false`.

## Installation as Windows Service
Starts before anyone logs in and runs as the SYSTEM account. Requires administrator rights: open PowerShell as Administrator and run (`sc.exe`, because `sc` is a PowerShell alias; the space after `=` is required):
```powershell
sc.exe create LighthouseManager DisplayName= "LighthouseManager" binPath= "C:\PATHTOEXTRACTEDFILES\LighthouseManagerService.exe" start= auto
sc.exe start LighthouseManager
```
Uninstall with `sc.exe stop LighthouseManager` and `sc.exe delete LighthouseManager`.

## Installation as systemd user service (Linux)
Extract the files to `~/LighthouseManager` (or adjust `ExecStart` in `lighthousemanager.service`), then:
```sh
cp ~/LighthouseManager/lighthousemanager.service ~/.config/systemd/user/
systemctl --user daemon-reload
systemctl --user enable --now lighthousemanager
```
Logs: `journalctl --user -u lighthousemanager` or `App_Data/logs` next to the executable.
Uninstall with `systemctl --user disable --now lighthousemanager` and remove the unit file.

# LighthouseManager.Tray
Tray icon for Windows and Linux. The icon shows the overall state (green: on, yellow: working/starting, grey: sleeping, red: error, crossed out: service not reachable). Click it to open a flyout with:
- the state of every base station with buttons to turn it on, put it to sleep or identify it (flash the LED; only shown while the station is running, because identify would wake a sleeping one)
- turn all on / sleep all
- the automatic mode switch

Right click opens a menu with the most important actions and Quit.

The tray app needs a running LighthouseManagerService. It connects to `http://127.0.0.1:47800` by default; use `--api-url <url>` or the environment variable `LIGHTHOUSEMANAGER_API_URL` for a different address.

## Autostart
- **Windows:** press Win+R, enter `shell:startup` and put a shortcut to `LighthouseManager.Tray.exe` into the folder.
- **Linux:** copy `lighthousemanager-tray.desktop` to `~/.config/autostart/` (adjust `Exec` if the files aren't in `~/LighthouseManager`). KDE Plasma shows tray icons out of the box. GNOME needs the "AppIndicator and KStatusNotifierItem Support" extension.

# License
LighthouseManager is licensed under the [MIT License](LICENSE). The release builds include third-party components under their own licenses, see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

# Acknowledgements
[Rossbearman](https://github.com/rossbearman) for helping me with some Bluetooth LE perfomance problems in Windows BLE API.
[BenWoodford](https://gist.github.com/BenWoodford) for [this](https://gist.github.com/BenWoodford/3a1e500a4ea2673525f5adb4120fd47c) awesome Lighthouse 2.0 GATT documentation.
