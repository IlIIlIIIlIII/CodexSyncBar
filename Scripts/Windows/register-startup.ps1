[CmdletBinding()]
param([string]$ExecutablePath)
$ErrorActionPreference = 'Stop'

$packageName = '14769529-7449-442E-89BB-30402D814A1D'
$package = Get-AppxPackage -Name $packageName
if ($ExecutablePath) {
    $ExecutablePath = (Resolve-Path -LiteralPath $ExecutablePath).Path
    if (!(Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw 'ExecutablePath must identify an installed executable.' }
} elseif (!$package) { throw 'Install Codex SyncBar or pass -ExecutablePath before registering startup.' }
# Ordinary LocalAppData may be redirected into a packaged caller's LocalCache.
# Programs is excluded from that virtualization, so login can find these files.
$launcherDirectory = Join-Path $env:LOCALAPPDATA 'Programs\CodexSyncBarStartup'
New-Item -ItemType Directory -Path $launcherDirectory -Force | Out-Null
$launcherPath = Join-Path $launcherDirectory 'launch.ps1'
$ExecutablePath | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $launcherDirectory 'executable.json') -Encoding UTF8
# Resolve the installed package each time so updates cannot leave a stale versioned path.
@'
$ErrorActionPreference = 'Stop'
$logPath = Join-Path $PSScriptRoot 'startup.log'
function Write-StartupLog([string]$Message) {
    Add-Content -LiteralPath $logPath -Value "$(Get-Date -Format o) $Message"
}
Write-StartupLog 'Login launcher started.'
Start-Sleep -Seconds 10
for ($attempt = 1; $attempt -le 3; $attempt++) {
    try {
        $running = Get-Process -Name 'CodexSyncBar.Windows' -ErrorAction SilentlyContinue |
            Where-Object SessionId -eq ([System.Diagnostics.Process]::GetCurrentProcess().SessionId)
        if ($running) { Write-StartupLog 'App is running.'; exit 0 }
        $executable = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'executable.json') -Raw | ConvertFrom-Json
        if (!$executable) {
            $package = Get-AppxPackage -Name '14769529-7449-442E-89BB-30402D814A1D'
            if (!$package) { throw 'Codex SyncBar package is not installed.' }
            $executable = Join-Path $package.InstallLocation 'CodexSyncBar.Windows.exe'
        }
        $process = Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -ArgumentList '--background' -WindowStyle Hidden -PassThru
        Start-Sleep -Seconds 10
        if (!$process.HasExited) { Write-StartupLog "App started: PID $($process.Id)."; exit 0 }
        throw "App exited with code $($process.ExitCode)."
    } catch {
        Write-StartupLog "Attempt ${attempt}: $($_.Exception.Message)"
        if ($attempt -lt 3) { Start-Sleep -Seconds 10 }
    }
}
exit 1
'@ | Set-Content -LiteralPath $launcherPath -Encoding UTF8

$taskName = 'CodexSyncBar-Logon'
$taskUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument ('-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $launcherPath + '"') -WorkingDirectory $launcherDirectory
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $taskUser
$trigger.Delay = 'PT15S'
$principal = New-ScheduledTaskPrincipal -UserId $taskUser -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 3) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Start Codex SyncBar at user login, with retry logging.' -Force | Out-Null

# Remove the old login routes only after the replacement is registered.
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'Codex SyncBar.lnk'
if (Test-Path -LiteralPath $shortcutPath) { Remove-Item -LiteralPath $shortcutPath }
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name CodexSyncBar -ErrorAction SilentlyContinue

# Replace the previous manual package-state registration; use only one login launcher.
$packageStartupKey = 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\' + $package.PackageFamilyName + '\CodexSyncBar.Startup'
if ($package -and (Test-Path -LiteralPath $packageStartupKey)) {
    Set-ItemProperty -LiteralPath $packageStartupKey -Name State -Value 0 -Type DWord
}
Write-Output $taskName
