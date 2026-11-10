param([switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
$runtime = Join-Path $env:LOCALAPPDATA 'ShrinkDesktop'
$exe = Join-Path $runtime 'app\Shrink.Desktop.exe'
$settings = Get-Content -LiteralPath (Join-Path $runtime 'launcher.json') -Raw | ConvertFrom-Json
if (-not (Test-Path -LiteralPath $exe)) { throw 'Shrink Desktop is not installed.' }
if (-not (Get-Process Shrink.Desktop -ErrorAction SilentlyContinue | Where-Object Path -EQ $exe)) {
    Start-Process -FilePath $exe -WindowStyle Hidden
}
$worker = Join-Path $runtime 'Dashboard-Tunnel.ps1'
$existing = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine.Contains($worker) }
if (-not $existing) {
    Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -ArgumentList @('-NoLogo','-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$worker`"") -WindowStyle Hidden
}
$ready = $false
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    try {
        $health = Invoke-RestMethod -Uri ($settings.Dashboard.TrimEnd('/') + '/api/v1/health') -TimeoutSec 2
        if ($health.status -eq 'ok') { $ready = $true; break }
    } catch {}
    Start-Sleep -Milliseconds 500
}
if (-not $ready) { throw "Dashboard unavailable. See $runtime\dashboard-tunnel.log. Desktop agent connection runs independently." }
Write-Output 'Shrink Desktop started; dashboard is ready.'
if (-not $NoBrowser) { Start-Process $settings.Dashboard }
