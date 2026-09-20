param(
    [Parameter(Mandatory = $true)][string] $Adapter,
    [switch] $CheckOnly
)

$ErrorActionPreference = 'Stop'
if ($Adapter -notmatch '^\\\\(?:wsl\.localhost|wsl\$)\\[^\\]+\\') {
    throw 'Adapter must be the UNC path of the installed WSL bridge.'
}
if (-not (Test-Path -LiteralPath $Adapter -PathType Leaf)) {
    throw 'The WSL bridge is not accessible. Start the intended WSL distribution first.'
}
$package = Get-AppxPackage -Name OpenAI.Codex | Sort-Object Version -Descending | Select-Object -First 1
if (-not $package) { throw 'The installed Codex desktop package was not found.' }
$executable = Join-Path $package.InstallLocation 'app\ChatGPT.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Codex desktop executable was not found.' }
$running = @(Get-Process -Name ChatGPT -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable })
if ($CheckOnly) {
    [pscustomobject]@{
        AdapterExists = $true
        DesktopExecutableExists = $true
        DesktopCurrentlyRunning = $running.Count -gt 0
        OverrideScope = 'Temporary user environment during startup; restored afterward'
        KeepsOfficialServerName = 'node_repl'
    } | ConvertTo-Json
    return
}
if ($running.Count -gt 0) {
    throw 'Close Codex completely, then run this launcher again. No running process was stopped.'
}

# The desktop regenerates node_repl configuration on startup. Its supported
# executable override retains the original MCP identity, hooks and policies.
$probePath = Join-Path $PSScriptRoot 'inspect-codex-override.ps1'
if (-not (Test-Path -LiteralPath $probePath -PathType Leaf)) { throw 'Startup verification script is missing.' }
$verificationPath = Join-Path $PSScriptRoot 'startup-verification.json'
$restorePath = Join-Path $PSScriptRoot 'user-environment-restore.json'
$environmentKey = 'CODEX_NODE_REPL_PATH'
$launchLock = New-Object System.Threading.Mutex($false, 'Local\CodexWslNodeReplLaunch')
try { $ownsLock = $launchLock.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $ownsLock = $true }
if (-not $ownsLock) { $launchLock.Dispose(); throw 'Another WSL Codex launch is in progress.' }
$startedAt = Get-Date
$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $executable
$startInfo.WorkingDirectory = $env:USERPROFILE
$startInfo.UseShellExecute = $false
$startInfo.EnvironmentVariables['CODEX_NODE_REPL_PATH'] = $Adapter
$verification = [ordered]@{
    StartedAt = $startedAt.ToUniversalTime().ToString('o')
    ExplicitEnvironmentBlock = $true
    UseShellExecute = $false
    OverrideScope = 'Temporary user environment during startup; restored afterward'
    ExpectedOverride = $Adapter
    EnvironmentVerified = $false
    ConfigurationVerified = $false
    UserEnvironmentRestored = $false
}
$previousUserOverride = $null
$userEnvironmentWritten = $false
try {
    # Chromium lowers its startup privileges with CreateProcessWithTokenW and
    # a NULL environment. That creates the child environment from the user's
    # profile, discarding process-only variables. Preserve that security behavior.
    if (Test-Path -LiteralPath $restorePath) {
        $recovery = Get-Content -LiteralPath $restorePath -Raw | ConvertFrom-Json
        if ([Environment]::GetEnvironmentVariable($environmentKey, 'User') -eq $recovery.TemporaryValue) {
            [Environment]::SetEnvironmentVariable($environmentKey, $recovery.PreviousValue, 'User')
        }
        Remove-Item -LiteralPath $restorePath
    }
    $previousUserOverride = [Environment]::GetEnvironmentVariable($environmentKey, 'User')
    [ordered]@{ PreviousValue = $previousUserOverride; TemporaryValue = $Adapter } |
        ConvertTo-Json | Set-Content -LiteralPath $restorePath -Encoding UTF8
    [Environment]::SetEnvironmentVariable($environmentKey, $Adapter, 'User')
    $userEnvironmentWritten = $true
    $launched = [System.Diagnostics.Process]::Start($startInfo)
    $verification.LaunchedProcessId = $launched.Id
    # A packaged app may relaunch itself. Inspect the stable desktop process,
    # not merely the first launcher PID; read only the exact override key.
    Start-Sleep -Seconds 3
    $deadline = (Get-Date).AddSeconds(30)
    do {
        $desktop = Get-Process -Name ChatGPT -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $executable -and $_.StartTime -ge $startedAt.AddSeconds(-1) } |
            Sort-Object StartTime | Select-Object -First 1
        if ($desktop) {
            try {
                $actual = & $probePath -ProcessId $desktop.Id
                $verification.DesktopProcessId = $desktop.Id
                $verification.EnvironmentVerified = $actual.Present -and $actual.Value -eq $Adapter
            } catch {
                # A relaunch may have replaced this process during inspection.
                $verification.EnvironmentVerified = $false
            }
        }
        $configPath = Join-Path $env:USERPROFILE '.codex\config.toml'
        if (Test-Path -LiteralPath $configPath) {
            $configText = [System.IO.File]::ReadAllText($configPath)
            $serverSection = [regex]::Match($configText, '(?ms)^\[mcp_servers\.node_repl\]\s*\r?\n(.*?)(?=^\[|\z)').Groups[1].Value
            $linuxAdapter = '/' + (($Adapter -replace '^\\\\(?:wsl\.localhost|wsl\$)\\[^\\]+\\', '') -replace '\\', '/')
            $verification.ConfigurationVerified = $serverSection.Contains('command = "' + $linuxAdapter + '"')
        }
        if ($verification.EnvironmentVerified -and $verification.ConfigurationVerified) { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    if (-not $verification.EnvironmentVerified) { throw 'The desktop process did not receive the override. See startup-verification.json.' }
    if (-not $verification.ConfigurationVerified) { throw 'Desktop environment is correct but node_repl configuration is not yet verified. Open the WSL task and check startup-verification.json.' }
    Write-Host 'Verified: the Codex desktop process and node_repl configuration use the WSL adapter.'
} finally {
    try {
        if ($userEnvironmentWritten) {
            if ([Environment]::GetEnvironmentVariable($environmentKey, 'User') -eq $Adapter) {
                [Environment]::SetEnvironmentVariable($environmentKey, $previousUserOverride, 'User')
                $verification.UserEnvironmentRestored = [Environment]::GetEnvironmentVariable($environmentKey, 'User') -eq $previousUserOverride
            } else {
                $verification.ConcurrentUserEnvironmentChangePreserved = $true
            }
            Remove-Item -LiteralPath $restorePath -ErrorAction SilentlyContinue
        }
        $verification | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $verificationPath -Encoding UTF8
    } finally {
        $launchLock.ReleaseMutex()
        $launchLock.Dispose()
    }
}
