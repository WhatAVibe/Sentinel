# Sentinel USB key

`sentinel-key` turns a normal USB flash drive into a **presence key** for Sentinel. It is a Rust companion
app in `usbkey/`, built into the same repository and shipped next to the Windows agent.

It is deliberately **not** a FIDO2 authenticator. A mass-storage device cannot do challenge-response, and
anything copied off it can be copied again. Sentinel already supports real FIDO2 keys through
`agent/Sentinel/Services/SecurityKey.cs`; use that when you need cryptographic 2FA. The USB key is a
convenience factor and a dead-man's switch.

## What it does

- Enrolls one specific removable drive and writes an encrypted `SENTINEL.KEY` marker at its root.
- Watches for that drive while a background guard runs.
- On removal, after a configurable grace period, can:
  - engage the Sentinel secure-lock overlay (default, falls back to Windows lock),
  - lock the Windows session,
  - arm Sentinel away mode,
  - or do nothing.
- On insertion, can mark the key present, or release the Sentinel secure lock with the key alone.
- Reports presence to the agent through the authenticated loopback API, so the agent state and activity log
  include `key_present` / `key_label`.
- Keeps the 2FA code, recovery password and phone as fallbacks. The USB key is never the only way in.

## Detection speed

The guard checks the removable drive set every **300 ms** with two cheap calls (`GetLogicalDrives` and
`GetDriveTypeW`). It only reads the volume identity, opens `\\.\E:`, and decrypts `SENTINEL.KEY` when:

- the drive set changed (insert/remove), or
- a key is present and five seconds have passed since the last verification.

That keeps normal idle cost near zero while making insertion/removal visible in well under a second on most
machines. The first full scan after a USB insert is still limited by how fast Windows enumerates the stick;
slow flash media shows up in `%APPDATA%\Sentinel\logs\usbkey.log` as `full key scan took N ms` when it takes
more than 50 ms.

## Encryption model

```
Windows DPAPI (CurrentUser)          USB drive
┌──────────────────────┐            ┌─────────────────────────────┐
│ 32-byte master key   │            │ SENTINEL.KEY                │
│ usbkey.json          │            │ magic | version | nonce     │
└──────────┬───────────┘            │ AES-256-GCM ciphertext      │
           │                        └──────────────┬──────────────┘
           └──────────── decrypt with key ─────────┘
                              AAD binds:
                              disk serial + volume serial + size + filesystem
```

- The master key is generated with `getrandom`, protected with DPAPI `CurrentUser`, and stored in
  `%APPDATA%\Sentinel\usbkey.json`. It never goes on the drive.
- The marker is AES-256-GCM with a random 12-byte nonce. The AAD includes the drive's hardware serial,
  vendor/product/revision, volume GUID, volume serial and total size.
- Copying `SENTINEL.KEY` to another drive fails authentication because the AAD fingerprint changes.
- Copying the whole drive to a different PC also fails because DPAPI cannot unwrap the key there.
- No plaintext key id, label or PC name is stored on the drive. `SENTINEL-KEY-README.txt` is a fixed,
  non-secret explanation.
- Version 1 markers (written before the vendor/product/volume-GUID fields were added) still verify on the
  enrolled PC. Re-enrolling the drive upgrades it to the stronger version 2 marker.

This stops casual inspection and naive copying. It does **not** stop malware running as the same Windows
user, a stolen drive used on the enrolled PC, or a determined attacker who can spoof USB descriptors.

## Use

1. Plug the drive into the PC.
2. Open Sentinel -> Settings -> Security -> **Sentinel USB key** -> **Set up key**.
3. Select the removable drive and choose **Enroll selected drive**.
4. Leave **Start the guard with Windows** ticked, then close the window.
5. Use **Advanced** to change the removal/insertion policy in the full Sentinel Key app.

CLI equivalents:

```powershell
cd usbkey
cargo build --release
.\target\release\sentinel-key.exe list
.\target\release\sentinel-key.exe enroll E:
.\target\release\sentinel-key.exe status
.\target\release\sentinel-key.exe guard
.\target\release\sentinel-key.exe pause 10
.\target\release\sentinel-key.exe forget E:
.\target\release\sentinel-key.exe startup on
.\target\release\sentinel-key.exe selftest
```

`pause` is the escape hatch when you need to unplug the drive for normal file transfer. The GUI also has
**Pause 10 min**.

## Agent integration

The guard sends authenticated `POST` requests to the agent's loopback API:

| Endpoint | Purpose |
|---|---|
| `POST /local/key/presence` | `{id, present, label, drive}`; updates `key_present` / `key_label` |
| `POST /local/key/unlock` | Releases the Sentinel secure lock when the same key was seen recently |

Any local process running as the user can read the DPAPI key and the local API token. That is the same
trust boundary as the rest of Sentinel's local API; the USB key is a presence signal, not a defence against
malware on the PC.

## Secure-lock behavior

- If the phone (or the tray) already engaged the secure lock, the guard checks the agent's `/local/status`
  and leaves it alone. It does not replace the message, stack a second overlay or re-lock.
- If removal needs to engage the lock, the overlay uses a generic message:
  **"This PC is locked. Enter your Sentinel code to unlock."**
- The lock text never names the USB key or tells anyone to look for a drive.
- The default insertion policy is `key+code`. `key` (release on insertion) is opt-in and labelled weaker.
- If the agent is unreachable or unpaired, `secure` falls back to `LockWorkStation`.

## Logging

| Path | Contents |
|---|---|
| `%APPDATA%\Sentinel\logs\usbkey.log` | Insert/remove, policy decisions, lock fallbacks, agent failures, scans over 50 ms |
| `%APPDATA%\Sentinel\usbkey-state.json` | Current presence, guard PID, last event, pause time |
| `%APPDATA%\Sentinel\usbkey.json` | Devices, policy, DPAPI-protected master key |

The log rotates at 1 MB. It does not contain the DPAPI key or the TOTP secret. A missing key or a slow first
scan after insertion is normal for cheap flash; `sentinel-key probe 5` separates the two.

## Limitations

- A plain USB stick is **not** a cryptographic second factor. Do not describe it as one.
- It cannot unlock the Windows logon screen. A user-mode app can lock Windows (`LockWorkStation`) but not
  enter credentials there. It can release Sentinel's own secure-lock overlay.
- `Win32_DiskDrive` serial numbers are missing on some cheap enclosures. Enrollment still works with volume
  serial + size, but the warning in the GUI is real: the binding is weaker.
- Changing the filesystem, repartitioning or formatting the drive invalidates the marker.
- Only `SENTINEL.KEY` is encrypted. Other files on the drive are normal storage. BitLocker To Go can encrypt
  the rest, but the marker must be readable when the guard runs; a locked volume looks like an absent key.
- If the drive is lost, use the 2FA code or recovery password, then **Forget** the old key from the app or
  delete `%APPDATA%\Sentinel\usbkey.json` while signed in.

## Build and test

```powershell
cd D:\Projects\Sentinel\usbkey
cargo test
cargo build --release
.\target\release\sentinel-key.exe selftest
.\target\release\sentinel-key.exe probe 5
```

The installer build runs `cargo build --release` and packs `sentinel-key.exe` next to `Sentinel.exe`:

```powershell
cd D:\Projects\Sentinel\installer
.\build.ps1
```

Full release steps: [../BUILD.md](../BUILD.md).
