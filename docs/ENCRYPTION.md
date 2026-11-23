# End-to-end encryption design (relay-blind data)

Status: design + quota enforcement shipped; client-side encryption is the next implementation batch.
This document is the contract the agent, phone, watch and relay must implement together.

## Goal

A relay operator — including someone who hosts a relay for other people — must not be able to read
events, messages, clipboard/keystroke text or screenshots. The relay becomes an untrusted store-and-forward
service that sees only routing metadata.

## Threat model

Protected against:

- A **passive or honest-but-curious relay operator**: reads the SQLite file, backups, logs or traffic.
- A relay host that is compromised after pairing.
- Other users on the same relay: they must not receive or decrypt another PC's data (already enforced for
  sockets and REST by device ownership checks).

Not protected against:

- **Active man-in-the-middle during manual pairing.** A malicious relay can substitute public keys while a
  user types the short 6-character code. QR pairing pins the PC key, so it is safe against this. Manual
  code entry remains vulnerable unless the user compares a key fingerprint shown on both devices.
- A compromised PC or phone (the endpoint holds the plaintext and the keys).
- Metadata: device names, timestamps, categories, severity, sizes, screenshot dimensions and counts remain
  visible to the relay so it can route, page, alert and enforce quotas.
- A user who deliberately uses `--insecure-tls` or a plain-HTTP relay (traffic confidentiality then depends
  entirely on the payload encryption, which is still end-to-end).

## Key hierarchy

```
PC identity keypair (P-256, generated once, DPAPI-protected)
      │
      ├── device data key (DEK, 32 random bytes, one per PC)
      │        │
      │        ├── AES-256-GCM for events, messages, clipboard/keystrokes, screenshots
      │        │
      │        └── wrapped per authorised device
      │
      └── per-device ECDH (P-256) → HKDF-SHA256 → AES-256-GCM key wrapping key
```

- The **DEK never leaves the PC in plaintext.** The agent wraps it for each phone/watch with a key derived
  from ECDH between the PC identity key and that device's ephemeral pairing key.
- Each paired device stores its ECDH private key and its unwrapped DEK in platform secure storage
  (`flutter_secure_storage` / Android Keystore on the phone, Keystore on the watch).
- Wrapped DEKs are stored on the relay as opaque blobs. The relay cannot unwrap them.
- Unpairing deletes the device row, its wrapped DEK and its token; the DEK itself stays on the PC.

## Pairing handshake (QR path)

```
PC                                    Relay                                   Phone
│ pair/request {pc_pub, fp}  ───────▶  store request (no secret)  ◀─────── scan QR:
│                                                                          url, request, code,
│                                                                          pc_pub, fingerprint
│                                       ◀──── pair/approve {phone_pub} ────  verify fingerprint,
│                                                                          generate phone keypair
│ poll pair/status ─────────────────▶  approved + phone_pub
│ ECDH(pc_priv, phone_pub) → HKDF → wrap DEK
│ POST agent/phones/<id>/key {wrapped_dek, pc_pub_fp} ─▶ store opaque
│                                       ◀──── GET devices/<id>/key ────────  phone unwraps DEK
```

- The existing 6-digit device confirmation stays: the PC must prove it holds the TOTP secret before the
  device is allowed.
- The QR carries the PC public key and a short fingerprint. The phone shows the fingerprint; the PC shows
  the same value in the pairing/confirm dialog for manual-code users to compare.
- For manual URL + 6-character pairing the phone has no pinned PC key; the ECDH still hides data from a
  passive relay, but an active relay could MITM the exchange. The UI must say so. A future "long pair key"
  or fingerprint confirmation closes this.

## Wire format

Every encrypted payload is a compact JSON envelope stored as a string:

```json
{"v":1,"alg":"A256GCM","kid":"<device id>","n":"<base64 nonce>","ct":"<base64 ciphertext>"}
```

- `kid` is the PC device id, so clients can select the right DEK.
- AAD: `sentinel:v1:<type>:<device_id>` where `type` is `event`, `message`, `shot` or `clip`. Prevents
  swapping ciphertexts between records or devices.
- Nonce: 12 random bytes per message. Never reused.
- Events: `title` and `detail` are serialised together into one plaintext, then encrypted. `category`,
  `severity` and `ts` stay plaintext for filtering/alerts.
- Messages: `title`, `body` and `reply` encrypted; direction/status stay plaintext.
- Screenshots: the PNG bytes are encrypted before upload. The relay stores `.enc` files and the row keeps
  `width`, `height`, `size` and `trigger` for quotas. Clients decrypt before rendering.
- Clipboard and keystrokes are event payloads, so they inherit event encryption. They are never stored in
  plaintext once the client is updated.

## Relay changes

Database (all nullable, so old rows keep working during migration):

- `devices.public_key` — PC P-256 public key (base64), set at pair/request.
- `mobiles.public_key` — device pairing public key, set at pair/approve.
- `mobiles.wrapped_dek` — opaque wrapped data key.
- `events.enc`, `messages.enc`, `screenshots.enc` — envelope strings; when present, `title`/`detail`/`body`
  columns are cleared.
- `devices.enc_version` — protocol version.

Endpoints:

| Endpoint | Auth | Purpose |
|---|---|---|
| `POST /agent/phones/<id>/key` | agent | Store `{wrapped_dek, pc_fingerprint}` |
| `GET /devices/<id>/key` | mobile | Return this device's wrapped DEK (own device only) |
| `GET /api/v1/me` | mobile | Already returns `totp_secret`; add `enc_required` and the device's key state |

The relay rejects mixed-mode writes once `enc_required` is set for a device (all new writes must carry an
envelope), so a client downgrade cannot silently reintroduce plaintext. Old plaintext rows remain readable
only by clients that still have the old data; migration purges or re-encrypts them from the PC.

## Client changes

- **Agent**: generate/load the identity keypair and DEK; encrypt before every event/message/screenshot
  upload; accept `enc` on none (it produces all of them). Keep local plaintext in the agent database for
  the dashboard.
- **Phone/watch**: fetch own wrapped DEK after pairing, unwrap with the pairing private key, store the DEK
  in secure storage, decrypt `enc` payloads for display. Fall back to plaintext only for legacy rows.
- **Flipper**: the phone relays commands and already holds the secret; no Flipper change.
- **Web/HA**: a future Home Assistant integration cannot decrypt without the DEK. Either issue the
  integration its own wrapped DEK during pairing (it becomes another authorised device) or expose only
  metadata. Do not make the relay a decryption oracle.

## Migration

1. Ship key generation, wrapping and relay storage while still accepting plaintext.
2. Update clients to encrypt new writes and read both formats.
3. Set `enc_required` per device once all its devices report the new version.
4. Purge or re-upload old screenshots; old events/messages can be left as legacy plaintext with a warning,
   or deleted after a configurable grace period.

There is no way to encrypt data the relay already holds without the PC; the PC has the DEK but not the
relay's copies. Plan a one-time `agent` job to download legacy events/screenshots (they are already visible
to the agent through the API), re-encrypt and re-upload them, then purge the originals.

## Abuse and quota controls (shipped, relay 1.3.5)

Encryption does not stop a malicious client from filling the disk with ciphertext, so the relay enforces
limits per device, configurable in `config.json`:

| Key | Default | Meaning |
|---|---|---|
| `screenshot_max_per_device` | 500 | Keep the newest N screenshots; older files are deleted |
| `screenshot_max_mb_per_device` | 1024 | Per-device screenshot budget |
| `manual_screenshots_per_30m` | 5 | Limit for manual/tray/remote captures |
| `screenshot_uploads_per_minute` | 30 | Hard upload rate, including away mode |
| `events_per_minute` | 600 | Event ingestion cap; the request returns `429` with `retry_after` |

Agents handle `429` by dropping the capture/event batch and logging it; no retry storm. The same counters
apply to encrypted payloads, since the relay still sees sizes.

## Open decisions

- P-256 everywhere (available in .NET, JCA and Dart via `cryptography`) versus X25519 (cleaner, needs
  `cryptography` on Dart and a native dependency on the agent). P-256 is the lowest-friction choice.
- Whether to encrypt `monitor_name`/window titles in screenshots metadata (currently plaintext metadata).
- Whether Home Assistant gets its own wrapped DEK or metadata-only entities.
