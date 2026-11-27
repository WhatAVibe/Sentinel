# sentinel-key

Rust companion app for Sentinel's encrypted USB presence key. It turns a normal removable drive into a
physical presence factor: unlock or lock depending on whether the prepared drive is plugged in.

It is **not** a FIDO2 authenticator. A mass-storage stick cannot do challenge-response, and a stolen drive
is still a key. Use the FIDO2 security-key path in the agent for real cryptographic 2FA.

## Quick start

```powershell
cargo build --release
.\target\release\sentinel-key.exe
```

In the window:

1. Plug in the USB drive.
2. Select it under **Key drives**.
3. Choose **Enroll this drive**.
4. Choose the removal action and grace period.
5. Start the guard, and tick **Start the guard with Windows**.

From the Sentinel agent you can also use **Settings -> Security -> Sentinel USB key -> Set up key**; that
window drives the same CLI without leaving Sentinel.

## What gets written

Only two small files are added to the drive root:

- `SENTINEL.KEY` - AES-256-GCM marker, about 0.3 KB
- `SENTINEL-KEY-README.txt` - fixed, non-secret explanation

Existing files are not touched, and the drive is never formatted by this app.

## Encryption and binding

- The 32-byte master key is generated with `getrandom`, protected with Windows DPAPI `CurrentUser`, and
  stored in `%APPDATA%\Sentinel\usbkey.json`. It never goes on the drive.
- The marker uses a random 12-byte nonce and AES-256-GCM.
- The AAD binds the ciphertext to hardware serial, USB vendor/product/revision, volume GUID, volume serial
  and total size.
- Copying the marker to another drive fails authentication. Copying the whole drive to another PC fails
  because DPAPI cannot unwrap the key there.
- Marker v1 (the original format) still verifies on the enrolled PC. Re-enrolling writes v2 with the
  stronger identity fields.

No plaintext key id, label or PC name is stored on the drive.

## Secure-lock interaction

- If a Sentinel secure lock is already active (for example, locked from the phone), the guard reads
  `/local/status` and leaves it alone. It does not replace the message, stack a second overlay or re-lock.
- If no secure lock is active, removal can engage the overlay with a generic message:
  **"This PC is locked. Enter your Sentinel code to unlock."**
- The lock text never mentions a USB key or tells anyone to look for a drive.
- Default insertion policy is `key+code`: the key is marked present, but the code is still required. The
  weaker key-only mode is opt-in and labelled in the UI.
- If the agent is unavailable or unpaired, the guard falls back to `LockWorkStation` for the `secure` and
  `windows` policies.

## Policy

| Setting | Values | Default |
|---|---|---|
| Removal action | `secure`, `windows`, `away`, `none` | `secure` |
| Insertion action | `key+code`, `key`, `none` | `key+code` |
| Grace period | 0-600 seconds | 20 |
| Lock if absent at startup | on/off | off |

`secure` uses Sentinel's 2FA overlay and falls back to the Windows lock. `windows` locks the session
immediately. `away` arms Sentinel away mode. `none` only records presence.

## Speed

The guard checks the removable drive set every 300 ms with cheap `GetLogicalDrives` / `GetDriveTypeW`
calls. Full volume identity and marker decryption run only when the set changes or every five seconds while
a key is present. `sentinel-key probe 5` reports full-scan timings; `usbkey.log` records scans over 50 ms.

## CLI

```powershell
sentinel-key                         # GUI
sentinel-key list [--json] [--all]   # drives and key state
sentinel-key enroll E: [--force]     # write marker + binding
sentinel-key forget E: [--force]     # delete marker + binding
sentinel-key status [--json]         # bindings, presence, guard, agent
sentinel-key probe 5                 # time full scans
sentinel-key guard                   # background watcher
sentinel-key pause 30                # pause enforcement for 30 minutes
sentinel-key startup on|off          # hidden login startup
sentinel-key selftest                # crypto + file IO + agent health
```

`pause` is the safe way to unplug the drive for normal file transfer. The GUI has **Pause 10 min**.

## Files and logs

| Path | Purpose |
|---|---|
| `%APPDATA%\Sentinel\usbkey.json` | Enrolled devices, policy, DPAPI-protected master key |
| `%APPDATA%\Sentinel\usbkey-state.json` | Runtime presence / guard PID / last event |
| `%APPDATA%\Sentinel\usbkey-pause` | Pause-until timestamp |
| `%APPDATA%\Sentinel\usbkey-stop` | Stop request for the guard |
| `%APPDATA%\Sentinel\logs\usbkey.log` | Insert/remove, policy, lock fallbacks, slow scans |

The log rotates at 1 MB and never contains the DPAPI key or TOTP secret.

## Security limits

- A stolen drive is the key. Do not treat it as theft-proof.
- Local malware running as the same Windows user can read the DPAPI key and the local API token.
- A determined attacker can spoof USB descriptors; mass storage has no challenge-response.
- The app cannot unlock the Windows logon screen. It can lock Windows and release Sentinel's own overlay.
- Only `SENTINEL.KEY` is encrypted. Other files on the drive are normal storage.

See [../docs/USB-KEY.md](../docs/USB-KEY.md) for the full threat model and
[../BUILD.md](../BUILD.md) for the release checklist.
