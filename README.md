# LighthouseManager
Command-Line tool to manage SteamVR Lighthouse 

Inspired by Lighthouse Keeper (https://github.com/rossbearman/lighthouse-keeper#lighthouse-keeper) this is a .NET 5 solution to discover and manage Valve Lighthouse Base Stations.
It uses Windows Bluetooth Low Energy SDK (https://docs.microsoft.com/de-de/windows/uwp/devices-sensors/bluetooth-low-energy-overview) to communicate with Base Stations.

## Requirements
- Windows 10 64bit
- Integrated Bluetooth or dongle managed by Windows

## Usage
With Command Promt or Powershell navigate to the location where LighthouseManager.exe is located.

Functions / parameters:

- -d Discover new Base Stations to get their MAC-Addresses.
- -w -a MAC_HERE,ANOTHERMAC_HERE Wake one or more Base Stations
- -s -a MAC_HERE,ANOTHERMAC_HERE Sleep one or more Base Stations
