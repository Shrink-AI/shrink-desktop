$ErrorActionPreference = 'Stop'
$runtime = Join-Path $env:LOCALAPPDATA 'ShrinkDesktop'
$settings = Get-Content -LiteralPath (Join-Path $runtime 'launcher.json') -Raw | ConvertFrom-Json
$mutex = [Threading.Mutex]::new($false, 'Local\ShrinkDashboardTunnel')
$locked = $false
try {
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { exit }
    $uri = [Uri]$settings.Dashboard
    if (-not $uri.IsLoopback -or $uri.Scheme -ne 'http') { throw 'Dashboard must use a loopback HTTP address.' }
    $ssh = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh.exe'
    $arguments = @('-N','-T','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes',
        '-o','ExitOnForwardFailure=yes','-o','ServerAliveInterval=15','-o','ServerAliveCountMax=3',
        '-o','ConnectTimeout=10','-p',[string]$settings.SshPort,
        '-L',"127.0.0.1:$($uri.Port):127.0.0.1:$($settings.RemotePort)",[string]$settings.SshTarget)
    while ($true) {
        try { & $ssh @arguments 2>> (Join-Path $runtime 'dashboard-tunnel.log') }
        catch { Add-Content -LiteralPath (Join-Path $runtime 'dashboard-tunnel.log') -Value ("{0:u} {1}" -f (Get-Date), $_.Exception.Message) }
        Start-Sleep -Seconds 5
    }
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
