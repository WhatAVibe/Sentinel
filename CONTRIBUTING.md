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

## Code layout

```
agent/    C# .NET 10 WPF agent
mobile/   Flutter app (Android + Windows test target)
watch/    Kotlin/Compose Wear OS app
server/   Flask relay and tests
docs/     setup, security, architecture, API
```

## License

By contributing you agree that your work is licensed under the repository's [MIT license](LICENSE).
