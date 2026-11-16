# End-to-end test: isolated relay + real agent + phone-side API calls.
# Leaves your existing agent config untouched.

param(
    [int]$Port = 8799,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tmp = Join-Path $env:TEMP "pcm-e2e"
$cfgDir = Join-Path $env:LOCALAPPDATA 'PCMonitor'
$agentExe = Join-Path $root 'agent\PCMonitor.Agent\bin\Debug\net10.0-windows\PCMonitor.Agent.exe'

Get-Process PCMonitor.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
New-Item -ItemType Directory $tmp | Out-Null

$backup = $null
if (Test-Path (Join-Path $cfgDir 'config.json')) {
    $backup = Join-Path $tmp 'config.bak'
    Copy-Item (Join-Path $cfgDir 'config.json') $backup -Force
}

$server = $null
$agent = $null
$exit = 1
try {
    if (-not $SkipBuild) {
        & dotnet build (Join-Path $root 'agent\PCMonitor.Agent\PCMonitor.Agent.csproj') -c Debug | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'agent build failed' }
    }

    Remove-Item (Join-Path $cfgDir 'config.json') -Force -ErrorAction SilentlyContinue

    $serverCfg = @{
        server_name = 'E2E'
        host = '127.0.0.1'
        port = $Port
        storage_dir = 'storage'
        pair_code_ttl_seconds = 600
        command_timeout_seconds = 90
        retention_days = 30
        max_upload_mb = 32
    } | ConvertTo-Json
    Set-Content -Path (Join-Path $tmp 'server.json') -Value $serverCfg

    $server = Start-Process -FilePath 'python' -PassThru -WindowStyle Hidden -ArgumentList @(
        (Join-Path $root 'server\server.py'), '--config', (Join-Path $tmp 'server.json')
    )
    Start-Sleep -Seconds 3

    $pair = Start-Process -FilePath $agentExe -PassThru -ArgumentList @(
        '--pair-server', "http://127.0.0.1:$Port",
        '--pair-report', (Join-Path $tmp 'pair.json'),
        '--insecure-tls'
    )

    & python (Join-Path $root 'server\tests\e2e_pair_approve.py') "http://127.0.0.1:$Port" `
        (Join-Path $tmp 'pair.json') (Join-Path $tmp 'mobile.json') | Out-Host
    if (-not $pair.WaitForExit(90000)) { Stop-Process -Id $pair.Id -Force; throw 'pairing timed out' }

    $cfg = Get-Content (Join-Path $cfgDir 'config.json') -Raw | ConvertFrom-Json
    $cfg.AwayScreensaver = $false
    $cfg.AutoScreenshotInAway = $false
    $cfg | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $cfgDir 'config.json')

    $agent = Start-Process -FilePath $agentExe -PassThru -ArgumentList @('--tray')
    Start-Sleep -Seconds 6

    & python (Join-Path $root 'server\tests\e2e_agent_checks.py') (Join-Path $tmp 'mobile.json') | Out-Host
    $exit = $LASTEXITCODE
}
finally {
    if ($agent) { Stop-Process -Id $agent.Id -Force -ErrorAction SilentlyContinue }
    if ($server) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    Get-Process PCMonitor.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
    if ($backup) {
        Copy-Item $backup (Join-Path $cfgDir 'config.json') -Force
    } else {
        Remove-Item (Join-Path $cfgDir 'config.json') -Force -ErrorAction SilentlyContinue
    }
}

Write-Output "e2e exit=$exit"
exit $exit
