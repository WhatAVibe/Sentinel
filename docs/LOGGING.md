# Logs and diagnostics

Sentinel keeps diagnostics local. Nothing in these logs is uploaded to the creator.

## Windows agent

| Path | Format | Rotation |
|---|---|---|
| `%APPDATA%\Sentinel\logs\agent-*.log` | JSON Lines (`ts`, `level`, `message`, `data`) | 10 days / size-bounded |
| `%APPDATA%\Sentinel\logs\usbkey.log` | timestamped text | rotates to `usbkey.log.1` at 1 MB |

Useful entries:

- `http` - relay calls, status codes and latency
- `event` - activity events with their detail object
- `security` - secure lock, codes, USB presence, exit authorization
- `notice` / `warning` / `error` - operator-visible state and failures

The agent never writes the TOTP secret, recovery password hash or DPAPI blob to the log. USB key ids and
drive roots are written; they are not secrets, but treat the log as private.

## USB key guard

`usbkey.log` records:

- `guard started` / `guard stopped`
- `key inserted` and `key reinserted before enforcement`
- `key absent for ...; enforcing secure`
- `secure lock already active; leaving it alone`
- `full key scan took N ms` for scans over 50 ms
- agent presence/unlock failures and Windows-lock fallbacks
- `enrolled key ...` / `forgot key ...`

The guard reads `/local/status` before locking. If the phone already engaged the secure lock, the guard
returns without touching the overlay or changing its message.

## Runtime state

| Path | Purpose |
|---|---|
| `%APPDATA%\Sentinel\usbkey-state.json` | presence, guard pid, last event, pause deadline |
| `%APPDATA%\Sentinel\usbkey.json` | devices, policy, DPAPI-protected master key |
| `%APPDATA%\Sentinel\secure-lock.json` | persisted secure-lock state for watchdog recovery |
| `%APPDATA%\Sentinel\config.json` | agent non-secret settings and DPAPI-protected tokens |

## Follow logs live

```powershell
Get-Content "$env:APPDATA\Sentinel\logs\usbkey.log" -Tail 50 -Wait

$agent = Get-ChildItem "$env:APPDATA\Sentinel\logs\agent-*.log" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-Content $agent.FullName -Tail 50 -Wait
```

## Quick diagnostics

```powershell
cd D:\Projects\Sentinel\usbkey
.\target\release\sentinel-key.exe status --json
.\target\release\sentinel-key.exe probe 5
.\target\release\sentinel-key.exe selftest

cd D:\Projects\Sentinel\agent
.\Sentinel\bin\Release\net10.0-windows\Sentinel.exe --selftest out.json
```

`probe` separates a slow USB device from a slow guard. `status --json` shows the enrolled fingerprint,
presence, guard pid, policy and agent connectivity.

## Privacy

- Do not paste full logs into a public issue without reviewing drive labels, usernames and device names.
- Rotate or delete logs before handing a machine to someone else.
- The installer's uninstall/wipe path removes the log directory with the rest of `%APPDATA%\Sentinel`.
