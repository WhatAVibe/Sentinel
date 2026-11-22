# Code signing and publisher identity

Sentinel builds should show **WhatAVibe** as the publisher. On Windows that name comes from the
Authenticode certificate, not from the assembly metadata, so metadata alone is not enough.

## The only safe setup

1. Buy a code-signing certificate in the legal name **WhatAVibe** (an OV certificate is enough for
   SmartScreen reputation to build up; an EV certificate or a cloud HSM gets reputation sooner).
2. Keep the private key in hardware: a USB token (YubiKey PIV, SafeNet) or a cloud HSM
   (Azure Trusted Signing, DigiCert KeyLocker, SSL.com eSigner). Never commit a `.pfx` to the
   repository and never embed a private key in the app: anything inside a distributed binary can be
   extracted, so an embedded key is not a certificate, it is a leak waiting to happen.
3. Sign every release binary with `scripts/sign.ps1` and timestamp it.

```powershell
$env:SENTINEL_SIGN_THUMBPRINT = "A1B2C3..."      # certificate in the Windows store (hardware-backed)
# or, for a CI secret with a password:
$env:SENTINEL_SIGN_PFX = "C:\secrets\whatavibe.pfx"
$env:SENTINEL_SIGN_PFX_PASSWORD = "..."

.\scripts\sign.ps1 -Files installer\out\Sentinel Setup.exe, agent\Sentinel\bin\Release\net10.0-windows\Sentinel.exe
```

`installer/build.ps1` signs the setup and the agent automatically when one of those variables is set.

## Self-signed certificates

A self-signed certificate with `CN=WhatAVibe` shows the name, but Windows does not trust it: users
would still see an "Unknown publisher" prompt unless they install your certificate into Trusted Root,
which trains them to trust arbitrary certificates. Use it only on a lab machine, never for releases.

## Android

Android does not display a publisher name the way Windows does; the APK signature identifies the
publisher. Configure a release keystore in `mobile/android/key.properties` (not committed):

```properties
storeFile=C:\\keys\\whatavibe-release.jks
storePassword=...
keyAlias=whatavibe
keyPassword=...
```

`mobile/android/app/build.gradle.kts` falls back to the debug key when that file is absent, which is
fine for sideloading but not for Play. Keep the keystore backed up; losing it means a new package
identity forever.

## What is committed

- `AssemblyCompany`, `Product`, `Copyright` = WhatAVibe in the agent and installer projects.
- The uninstall registry entry `Publisher` = WhatAVibe.
- No certificate, key, password or token is stored in the repository.
