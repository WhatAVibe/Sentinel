param(
    [Parameter(Mandatory = $true)][string[]]$Files,
    [string]$Thumbprint = $env:SENTINEL_SIGN_THUMBPRINT,
    [string]$Pfx = $env:SENTINEL_SIGN_PFX,
    [string]$PfxPassword = $env:SENTINEL_SIGN_PFX_PASSWORD,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $signtool) {
    Write-Warning "signtool.exe not found. Install the Windows SDK to sign builds."
    exit 0
}

if (-not $Thumbprint -and -not $Pfx) {
    Write-Warning "No signing certificate configured; builds stay unsigned. See docs/SIGNING.md."
    exit 0
}

foreach ($file in $Files) {
    if (-not (Test-Path $file)) { throw "Missing file: $file" }
    Write-Host "Signing $file"
    if ($Thumbprint) {
        & $signtool sign /sha1 $Thumbprint /fd sha256 /td sha256 /tr $TimestampUrl $file
    }
    else {
        if (-not $PfxPassword) { throw "Set SENTINEL_SIGN_PFX_PASSWORD for a .pfx signing key." }
        & $signtool sign /f $Pfx /p $PfxPassword /fd sha256 /td sha256 /tr $TimestampUrl $file
    }
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $file" }
    & $signtool verify /pa $file
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed for $file" }
}

Write-Host "Signing complete."
