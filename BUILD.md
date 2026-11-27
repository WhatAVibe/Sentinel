# Sentinel build and release

This is the checklist for building, verifying and preparing a Sentinel release. It covers the Windows
agent, relay, phone app, watch app, Flipper app, installer and the Rust USB key companion.

**Do not push a commit, tag or GitHub release until the owner has reviewed the diff and said to proceed.**

## Versions

Versions are per component. Read the source before cutting a release; do not assume the numbers below are
still current.

| Component | Version source | Current |
|---|---|---|
| Windows agent | `agent/Sentinel/Services/AgentInfo.cs`, `Sentinel.csproj` | 2.1.0 |
| Installer | `installer/Sentinel.Installer/InstallerCore.cs`, `.csproj` | 3.1.0 |
| Relay | `server/sentinel_server/config.py` | 2.0.1 |
| Phone app | `mobile/pubspec.yaml` | 2.0.2 (20) |
| Watch app | `watch/app/build.gradle.kts` | 2.0.1 (12) |
| Flipper app | `flipper/application.fam` | 1.1.0 |
| USB key | `usbkey/Cargo.toml` | 0.1.0 |

## Prerequisites

- Windows 11 with .NET 10 SDK, Rust 1.97 MSVC, Flutter 3.47, Android SDK, Java/Gradle, `ufbt` for Flipper.
- Flutter is not on PATH by default:

```powershell
$env:Path = "C:\flutter\bin;$env:Path"
```

- Use `curl.exe`, not PowerShell's `curl` alias.
- Keep scratch files in `C:\Users\lacel\AppData\Local\Temp\opencode`; never leave project edits there.

## Build

### Windows agent

```powershell
cd D:\Projects\Sentinel\agent
.\build.bat
```

Or:

```powershell
dotnet build Sentinel.sln -c Release
```

Agent output: `agent\Sentinel\bin\Release\net10.0-windows\Sentinel.exe`.

### USB key

```powershell
cd D:\Projects\Sentinel\usbkey
cargo fmt --check
cargo clippy -- --deny warnings
cargo test
cargo build --release
.\target\release\sentinel-key.exe selftest
```

Output: `usbkey\target\release\sentinel-key.exe`.

`selftest` checks DPAPI, v2 marker write/read/delete in a temp folder, and agent health. `probe 5` measures
full removable-drive scans without writing anything to the drive.

### Installer

```powershell
cd D:\Projects\Sentinel\installer
.\build.ps1
```

`build.ps1` publishes the self-contained agent, builds the Rust key app, puts both in the embedded
`payload.zip`, and publishes `out\Sentinel Setup.exe`.

### Relay

```powershell
cd D:\Projects\Sentinel\server
python -m venv .venv
.venv\Scripts\pip install -r requirements.txt
.venv\Scripts\python run.py
```

### Phone app

```powershell
$env:Path = "C:\flutter\bin;$env:Path"
cd D:\Projects\Sentinel\mobile
flutter analyze
flutter build apk --release --split-per-abi
```

### Watch app

```powershell
cd D:\Projects\Sentinel\watch
.\gradlew.bat :app:assembleDebug
```

### Flipper app

```powershell
cd D:\Projects\Sentinel\flipper
ufbt
```

## Verification before a release

Run everything, then record the measured results in `CHANGELOG.md`.

### Automated

```powershell
cd D:\Projects\Sentinel\agent
dotnet build Sentinel.sln -c Release

cd D:\Projects\Sentinel\usbkey
cargo fmt --check
cargo clippy -- --deny warnings
cargo test
cargo build --release

cd D:\Projects\Sentinel\installer
.\build.ps1
```

### USB key hardware

Use a real removable drive. Formatting is destructive; only do it with the owner's confirmation.

```powershell
cd D:\Projects\Sentinel\usbkey
.\target\release\sentinel-key.exe list
.\target\release\sentinel-key.exe enroll E:
.\target\release\sentinel-key.exe status
.\target\release\sentinel-key.exe probe 5
.\target\release\sentinel-key.exe forget E:
```

Expected:

- `list` shows the drive and its state.
- `enroll` writes a `SENTINEL.KEY` (v2) and `SENTINEL-KEY-README.txt`.
- `status` shows `PRESENT` and the key id.
- `probe` reports sub-millisecond scans on a mounted, idle stick; the first scan after insertion can be
  slower.
- `forget` removes only the marker and README; unrelated files stay.
- Copying `SENTINEL.KEY` to another drive must fail to decrypt.

### Secure lock interaction

1. Link a phone, start the guard, insert the enrolled drive.
2. Trigger **Secure lock** from the phone. Confirm the overlay appears.
3. Unplug the drive. Confirm the overlay **does not** change its message, duplicate, or reveal the USB
   key. The guard reads `/local/status` and leaves an already-active secure lock alone.
4. Reinsert the drive with the default `key+code` policy. Confirm the overlay stays until the code is
   entered.
5. With `key` policy, confirm insertion releases the lock and removal re-locks generically.
6. Confirm the lock text never mentions a USB device.

### Installer payload

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead("D:\Projects\Sentinel\installer\Sentinel.Installer\payload.zip")
$zip.Entries | Select-Object FullName, Length
$zip.Dispose()
```

The payload must contain both `Sentinel.exe` and `sentinel-key.exe`. Compare the key entry hash with
`usbkey\target\release\sentinel-key.exe`.

## Logs and diagnostics

| What | Where |
|---|---|
| Agent JSONL log | `%APPDATA%\Sentinel\logs\agent-*.log` |
| USB key guard log | `%APPDATA%\Sentinel\logs\usbkey.log` (rotates at 1 MB) |
| USB key state | `%APPDATA%\Sentinel\usbkey-state.json` |
| USB key config + DPAPI key | `%APPDATA%\Sentinel\usbkey.json` |
| Agent config | `%APPDATA%\Sentinel\config.json` |
| Secure lock persistence | `%APPDATA%\Sentinel\secure-lock.json` |

`usbkey.log` records insert/remove, policy decisions, lock fallbacks, agent failures and full-scan timings
over 50 ms. It never writes the DPAPI key or TOTP secret.

## Security checks

- `git status --short` and `git diff --check` are clean apart from intended files.
- No `config.json`, `usbkey.json`, `*.log`, `target/`, `bin/`, `obj/`, `out/`, `payload.zip`, APK/AAB or
  credentials are staged.
- `D:\Projects\Creds.txt` and `D:\Projects\dashlnk-creds.txt` are never copied into the repo.
- The USB key marker is v2, encrypted with AES-256-GCM and bound to hardware serial, vendor/product/
  revision, volume GUID, volume serial and size.
- The secure-lock overlay text never names the USB key.
- An already-active secure lock is left alone by the guard.
- Local API endpoints require the bearer token; `/local/key/unlock` is rate-limited and requires a fresh
  key presence.
- The installer and agent are unsigned; SmartScreen will warn. Do not claim they are signed.

## Release preparation

1. Bump the component version(s) in source.
2. Add a dated `CHANGELOG.md` entry with verification numbers and open items.
3. Build every artifact and run the checks above.
4. Produce `SHA256SUMS.txt` for the release assets.
5. Commit the source changes locally. Do this only after owner review.
6. Ask the owner before pushing the commit, tag or release.

Suggested commands, **not to be run without confirmation**:

```powershell
cd D:\Projects\Sentinel
git status --short
git diff --check
git add <intended files>
git commit -m "3.1.0: USB presence key, secure-lock integration, release docs"
git push origin main

git tag -a v3.1.0 -m "Sentinel 3.1.0"
git push origin v3.1.0
```

Release assets:

- `Sentinel Setup 3.1.0.exe`
- `Sentinel-Agent-2.1.0-win-x64.exe` if publishing the portable agent
- `Sentinel-Phone-2.0.2.apk`
- `Sentinel-Watch-2.0.1.apk`
- `Sentinel-Flipper-1.1.0.fap`
- `Sentinel-Relay-2.0.1.zip`
- `SHA256SUMS.txt`

After publishing, fetch the live assets and verify the hashes and version strings. Do not reuse a version
number for a changed binary.

## Related docs

- [docs/USB-KEY.md](docs/USB-KEY.md) - USB presence key design, threat model, CLI and limitations
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) - component and secure-lock internals
- [docs/SECURITY.md](docs/SECURITY.md) - hardening and threat model
- [docs/LOGGING.md](docs/LOGGING.md) - log locations, rotation and diagnostics
- [installer/README.md](installer/README.md) - installer payload and unattended install
- [usbkey/README.md](usbkey/README.md) - key app usage
- [usbkey/BUILD.md](usbkey/BUILD.md) - key app build and hardware verification
