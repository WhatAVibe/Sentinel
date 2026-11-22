# Sentinel Installer

WPF setup wizard for the Windows agent. Version **2.1.1**.

The published `Sentinel-Setup-2.1.0.exe` is a self-contained single file. It contains the compressed
self-contained agent, so a clean Windows 10/11 x64 machine needs nothing preinstalled.

## What it does

- First-run wizard: terms, install folder (default `%APPDATA%\Sentinel`), recovery password, relay address
- Pairing with a QR code and a 6-character code; after the phone approves, the wizard asks for the live
  6-digit code so the device is confirmed before the install finishes
- Background install with a progress bar; the window never blocks
- Detects an existing install and offers **Update / Repair** (keeps config and pairing) or **Uninstall**
- Registers the installed agent as the uninstaller (`Sentinel.exe --uninstall`); no copy of the setup stays
  on disk
- Adds the tray autostart entry and an optional Start-menu shortcut
- Builds stay unsigned; publisher metadata is `WhatAVibe`

## Build

```powershell
.\build.ps1
# out\Sentinel Setup.exe
```

`build.ps1` publishes the agent (`PublishSingleFile`, compressed), zips it as the embedded payload, then
publishes the installer with the payload as a resource. Requires the .NET 10 SDK.

## Layout

| File | Purpose |
|---|---|
| `Sentinel.Installer/MainWindow.xaml(.cs)` | Wizard steps, pairing, progress |
| `Sentinel.Installer/InstallerCore.cs` | Install/update/uninstall, shortcuts, agent launch, relay calls |
| `build.ps1` | Agent payload + installer publish |

The uninstaller itself is the agent window (`agent/Sentinel/Views/UninstallWindow`), so removal works even
if the setup file was deleted.
