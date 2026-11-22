# Sentinel Phone (Android)

Flutter app for Sentinel. It talks to your own relay over HTTPS (or to the agent directly over ADB) and
gives you the dashboard, 2FA code, screenshots, messages and every remote action.

- Live PC status (CPU, RAM, uptime, session), quick actions, secure lock and away mode
- Security tab with the live TOTP code, unlock, and policy toggles
- Activity timeline and screenshot gallery, both paged from the relay
- Messages with toast / dialog / alert / banner / fullscreen styles and PC replies
- Notification filters: security, power, messages, screenshots, clipboard, everything else
- Disk and battery cards, and activity/audit export to JSON or CSV from Settings
- Multiple PCs with local names, plus a direct ADB mode for a LAN-only setup
- Optional Flipper Zero bridge: the phone relays BLE commands and holds the secret
- PIN + biometrics lock, sessions in Android Keystore

## Install

Download **[Sentinel-Phone-1.6.0.apk](https://github.com/WhatAVibe/Sentinel/releases/latest/download/Sentinel-Phone-1.6.0.apk)**
(Android 8.0+) and open it, or:

```powershell
adb install -r Sentinel-Phone-1.6.0.apk
```

Releases are unsigned; Android asks you to allow installs from your file manager or ADB.

## Build

Requires Flutter with Dart 3.13+, the Android SDK and JDK 17+.

```powershell
flutter pub get
flutter build apk --release
adb install -r build\app\outputs\flutter-apk\app-release.apk
```

Building from a path containing `!` fails in Flutter; use the repository wrapper `.\flutter.ps1 build apk
--release`, which builds through a junction.

## Pair

1. On the PC open **Settings → Pair another phone** (or run the installer and reach the pairing step).
2. In the app choose **Code**, enter the relay URL and the 6-character code (or scan the QR).
3. Approve in the app. The PC asks for the live 6-digit code shown on the phone; until it is entered the
   phone shows **Waiting for your PC** with the same code and polls the relay. It enters the dashboard as
   soon as the PC confirms.
4. Add more PCs from the name at the top of the app → **Pair another PC**.

## Direct ADB mode

No relay needed when the phone is plugged into the PC. The agent exposes `127.0.0.1:8790` through
`adb reverse`. On the phone: **Pair → Direct**, enter the code shown in PC Settings → **Pair direct**.
VPNs and per-app adb restrictions can block the reverse tunnel.

## Release values

`version:` in `pubspec.yaml` is the shipped app version (currently `1.6.0+16`) and
`sentinelAppVersion` in `lib/models.dart` is what goes into `X-App-Version` so the PC can show the version
per linked device.
