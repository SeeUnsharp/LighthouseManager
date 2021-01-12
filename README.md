# LighthouseManager
Command-Line tool to manage SteamVR Lighthouse 

Inspired by [Lighthouse Keeper](https://github.com/rossbearman/lighthouse-keeper#lighthouse-keeper) this is a .NET 5 solution to discover and manage Valve Lighthouse Base Stations.
It uses [Windows Bluetooth Low Energy SDK](https://docs.microsoft.com/de-de/windows/uwp/devices-sensors/bluetooth-low-energy-overview) to communicate with Base Stations.

## Requirements
- Windows 10 64bit
- Integrated Bluetooth or dongle managed by Windows

## Usage
With Command Promt or Powershell navigate to the location where LighthouseManager.exe is located.

Functions / parameters:

- `-d` or `--discover` Discover new Base Stations to get their MAC-Addresses.
- `-w` or `--wake` `-a MAC_HERE,ANOTHERMAC_HERE` Wake one or more Base Stations
- `-s` or `--sleep` `-a MAC_HERE,ANOTHERMAC_HERE` Sleep one or more Base Stations

## Exit codes
Based on success or not LighthouseManager will exit with different codes
- `0` All commands executed successfully
- `1` One or more commands failed after given retry attempt

## SteamVRWatcher
SteamVRWatcher is a litte Command-Line tool that monitors SteamVR process (vrserver.exe) for starting or closing in a given interval (default 1000ms) and then starting LighthouseManager with corresponding parameters. You can configure interval, path to LighthouseManager.exe and base station MAC addresses in appsettings.json

## Installation
Just copy LighthouseManager.exe, SteamVRWatcher.exe and appsettings.json to a folder. Open appsettings.json and set your Base Station Mac Addresses (You can disover them with LighthouseManager `--discover` parameter).
Then you can use Command-Line or Powershell to execute SteamVRWatcher.
You can also start SteamVRWatcher automatically when Windows starts. To do so press WIN+R and type `shell:startup`. It should open the Autostart folder. Just paste a shortcut to SteamVRWatcher.exe there.
