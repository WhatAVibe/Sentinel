# Security policy

Sentinel is remote-administration software. Treat every deployment as security-sensitive: it can lock a
PC, capture screenshots and (if enabled) record activity.

## Supported versions

Only the latest released versions receive fixes:

| Component | Supported |
|---|---|
| Agent | 1.2.x |
| Relay | 1.2.x |
| Phone app | 1.2.x |
| Watch app | 1.2.x |

## Reporting a vulnerability

**Do not open a public issue for a security problem.**

Use GitHub's private vulnerability reporting (**Security → Report a vulnerability**) on this repository.
Please include:

- affected version and component,
- what an attacker gains,
- reproduction steps or a proof of concept,
- any suggested fix.

You can expect an acknowledgement within a few days. This is a personal project, so there is no bug bounty.

## Scope

In scope:

- Authentication or pairing bypass (agent, phone or relay tokens)
- TOTP bypass, replay, or a path where the agent skips local verification
- Secure-lock escape that does not require administrator rights or Ctrl+Alt+Del
- Unauthorized screenshot, event, or message access
- Remote code execution through the relay, API, or agent command handling

Out of scope:

- Killing the agent/supervisor with local administrator rights
- Ctrl+Alt+Del / Windows secure desktop limitations (documented)
- Anything requiring physical access to an unlocked PC
- Self-XSS, social engineering, denial of service by flooding your own relay
- Vulnerabilities in third-party dependencies without a practical exploit path

## Safe harbour

Testing must be limited to your own devices and your own relay. Do not attempt to access other users'
instances. Good-faith research that follows this policy will not be pursued.

## Before you expose a relay

Read [docs/SECURITY.md](docs/SECURITY.md). At minimum: use Tailscale or HTTPS, keep `RequirePhonePresence`
on if you carry the phone, leave keystroke capture off, and set a sensible `retention_days`.
