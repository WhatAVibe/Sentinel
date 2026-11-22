# Contributing

Thanks for looking. Sentinel is a small, opinionated project; focused fixes and clean features are
welcome.

## Before you start

- **Only test against your own devices and relay.** Never probe someone else's instance.
- Open an issue for anything larger than a bug fix so the approach can be agreed first.
- Security problems go through [SECURITY.md](SECURITY.md), not the public tracker.

## Ground rules

- Match the surrounding style: minimal comments, small files, no new dependencies without a reason.
- Keep the security model intact. Any change touching TOTP, tokens, pairing, secure lock or the supervisor
  must explain the threat-model impact in the PR.
- Do not add telemetry, analytics, accounts or cloud services.
- Keep the relay dependency list tiny (currently Flask + flask-sock).

## Building and testing

| Component | Command |
|---|---|
| Relay | `cd server && python -m pip install -r requirements.txt && python server.py` |
| API smoke test | `python server/tests/smoke.py http://127.0.0.1:8799` |
| Agent | `cd agent && dotnet build Sentinel.sln -c Release` |
| Agent self-test | `Sentinel.exe --selftest out.json` |
| Phone | `cd mobile && flutter pub get && flutter test` |
| Watch | `cd watch && ./gradlew :app:assembleDebug` |

## Pull requests

- One concern per PR, with a short description of the behaviour change and how you tested it.
- Update the relevant doc when behaviour or configuration changes (`README.md`, `docs/`, component
  README).
- If you change a version, bump it in the component's source and update the version table in the README.

## Releasing

1. Bump the component versions (`AgentInfo.cs`, `config.py`, `pubspec.yaml` + `models.dart`,
   `watch/app/build.gradle.kts` + `Api.kt`, installer `.csproj`, `flipper/application.fam`).
2. Update the version table in `README.md`, `CHANGELOG.md` and any asset names in the docs.
3. Build everything: `installer\build.ps1`, `flutter build apk --release`,
   `gradlew :app:assembleDebug`.
4. Tag `v<installer version>` and publish a GitHub release named `Sentinel v<version>` with these assets:

   | Asset | Source |
   |---|---|
   | `Sentinel-Setup-<product>.exe` | `installer/out/Sentinel Setup.exe` |
   | `Sentinel-Agent-<agent>-win-x64.exe` | the published single-file agent |
   | `Sentinel-Phone-<app>.apk` | `mobile/build/app/outputs/flutter-apk/app-release.apk` |
   | `Sentinel-Watch-<watch>.apk` | `watch/app/build/outputs/apk/debug/app-debug.apk` |
   | `Sentinel-Relay-<relay>.zip` | zip of `server/` |
   | `SHA256SUMS.txt` | `sha256sum` of every asset |

5. Flipper builds have their own `flipper-v<version>` release.
6. Verify the `releases/latest/download/...` links in `README.md` resolve after publishing.

## Code layout

```
agent/      C# .NET 10 WPF agent
mobile/     Flutter app for Android
watch/      Kotlin/Compose Wear OS app
flipper/    Flipper Zero FAP and optional ESP32 bridge
server/     Flask relay and tests
installer/  Windows setup wizard and agent uninstaller
docs/       setup, security, architecture, API
```

## License

By contributing you agree that your work is licensed under the repository's [MIT license](LICENSE).
