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

## Unattended install

For a fleet, the setup can run without the wizard:

```powershell
Sentinel Setup.exe --silent --relay https://sentinel.example.com --recovery "a long recovery password" `
  --confirm-code 123456 --report C:\sentinel-install.json
```

| Flag | Meaning |
|---|---|
| `--silent` | Run without any window |
| `--relay <url>` | Relay base URL (http/https) |
| `--recovery <password>` | Recovery password, at least 8 characters (required) |
| `--dir <path>` | Install directory, default `%APPDATA%\Sentinel` |
| `--confirm-code <code>` | 6-digit code shown on the approving phone (if already known) |
| `--confirm-file <path>` | Wait for the 6-digit code to be written to this file (scripted approval) |
| `--auto-confirm` | Compute the code from the secret the PC just received and confirm without the phone prompt |
| `--no-startup` | Do not add the tray autostart entry |
| `--report <path>` | JSON report (default `%TEMP%\sentinel-install.json`) with `pair_code`, `status`, `ok` |

The command creates the pair request, writes `pair_code` to the report, waits for a phone to approve it
(up to 10 minutes), installs, writes the config, registers the uninstaller and starts the agent. Without a
`--confirm-code` or `--auto-confirm`, the device stays pending until someone confirms it in Linked devices.
`--auto-confirm` is convenient for scripted rollouts but removes the human confirmation step: the machine
that received the TOTP secret confirms itself. Only use it where you already trust the machine and the
relay.

Example with phone approval from a script:

```powershell
Start-Process "Sentinel Setup.exe" -ArgumentList '--silent','--relay','https://sentinel.example.com',
  '--recovery','...','--confirm-file','C:\sentinel-code.txt','--report','C:\sentinel-install.json'
# 1. enter pair_code from C:\sentinel-install.json in the phone app
# 2. approve it, read the 6-digit code on the phone
# 3. Set-Content C:\sentinel-code.txt 123456   # the installer picks it up and finishes
```

To roll out fully unattended, pass `--auto-confirm` instead; the installer derives the confirmation code
from the TOTP secret returned to it during the claim. That skips the human step, so reserve it for machines
you already control.

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
