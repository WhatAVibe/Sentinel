# sentinel-key build and verification

## Requirements

- Rust 1.97 with the MSVC toolchain
- Windows 10/11
- A removable USB drive for the hardware test (optional for CI)

## Build

```powershell
cd D:\Projects\Sentinel\usbkey
cargo fmt --check
cargo clippy -- --deny warnings
cargo test
cargo build --release
```

Output: `target\release\sentinel-key.exe` (about 7 MB, stripped).

## Automated tests

```powershell
cargo test
.\target\release\sentinel-key.exe selftest
```

- `cargo test`: marker round trip, tamper rejection, wrong-drive rejection, changed hardware identity
  rejection.
- `selftest`: DPAPI protect/unprotect, v2 marker write/read/delete in a temp folder, agent health and
  removable-drive count.

Expected output includes:

```text
DPAPI: ok
AES-256-GCM marker: ok (323 bytes on disk)
Agent: connected
```

## Scan timing

```powershell
.\target\release\sentinel-key.exe probe 5
```

This only reads volumes and markers; it never writes. Use it to distinguish a slow stick from a slow guard.
The idle guard itself uses a 300 ms cheap poll, so `probe` measures the expensive full scan, not the poll.

## Real hardware test

Formatting is destructive. Only format with the owner's confirmation.

```powershell
.\target\release\sentinel-key.exe list
.\target\release\sentinel-key.exe enroll E:
.\target\release\sentinel-key.exe status
Get-ChildItem E:\ -Force
.\target\release\sentinel-key.exe forget E:
```

Confirm:

- the drive appears as `removable`;
- the marker is `SNTK` v2 and about 0.3 KB;
- `status` says `PRESENT`;
- `forget` removes only `SENTINEL.KEY` and `SENTINEL-KEY-README.txt`;
- unrelated files and `System Volume Information` stay untouched;
- copying the marker to a different drive fails to decrypt.

## Secure-lock test

1. Start the guard with an enrolled drive inserted.
2. Trigger **Secure lock** from the Sentinel phone app.
3. Unplug the drive. The existing overlay must stay as-is: same message, no duplicate, no USB wording.
4. Reinsert with `key+code`. The overlay must still require the code.
5. Switch to `key` only if you accept the weaker policy; insertion should release, removal should lock
   generically.

The guard checks the agent's `/local/status` before engaging a secure lock. An already-active lock is left
alone.

## Logs

`%APPDATA%\Sentinel\logs\usbkey.log` records insert/remove, policy decisions, fallbacks, agent errors and
full scans over 50 ms. It rotates at 1 MB.

## Installer

`installer\build.ps1` builds this crate and puts `sentinel-key.exe` in the embedded payload next to
`Sentinel.exe`.

Verify the payload:

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead("D:\Projects\Sentinel\installer\Sentinel.Installer\payload.zip")
$zip.Entries | Select-Object FullName, Length
$zip.Dispose()
```

The key entry hash must match `usbkey\target\release\sentinel-key.exe`. The installer is unsigned; do not
claim otherwise.

## Release

Follow [../BUILD.md](../BUILD.md). Do not push commits, tags or releases without the owner's approval.
