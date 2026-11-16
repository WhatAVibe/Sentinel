# Flutter wrapper: the Flutter tool rejects project paths containing '!'.
# This creates a junction without special characters and runs Flutter from there.
param([Parameter(ValueFromRemainingArguments = $true)]$Args)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$link = Join-Path $env:USERPROFILE 'pcm-link'

if (-not (Test-Path $link)) {
    cmd /c mklink /J "$link" "$repo" | Out-Null
}

$target = Join-Path $link 'mobile'
Push-Location $target
try {
    if ($Args.Count -eq 0) {
        flutter --version
    } else {
        & flutter @Args
    }
    exit $LASTEXITCODE
} finally {
    Pop-Location
}
