[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$packageName = '14769529-7449-442E-89BB-30402D814A1D'
$package = Get-AppxPackage -Name $packageName
if (!$package) { throw 'Install Codex SyncBar before registering startup.' }
$launcherDirectory = Join-Path $env:LOCALAPPDATA 'CodexSyncBar\Startup'
New-Item -ItemType Directory -Path $launcherDirectory -Force | Out-Null
$launcherPath = Join-Path $launcherDirectory 'launch.ps1'
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
        $package = Get-AppxPackage -Name '14769529-7449-442E-89BB-30402D814A1D'
        if (!$package) { throw 'Codex SyncBar package is not installed.' }
        $executable = Join-Path $package.InstallLocation 'CodexSyncBar.Windows.exe'
        $process = Start-Process -FilePath $executable -ArgumentList '--background' -WindowStyle Hidden -PassThru
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

$shortcutPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'Codex SyncBar.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$shortcut.Arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $launcherPath + '"'
$shortcut.WorkingDirectory = $launcherDirectory
$shortcut.WindowStyle = 7
$shortcut.Description = 'Start Codex SyncBar in the notification area at Windows login.'
$shortcut.Save()

# Replace the previous manual package-state registration; use only one login launcher.
$packageStartupKey = 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\' + $package.PackageFamilyName + '\CodexSyncBar.Startup'
if (Test-Path -LiteralPath $packageStartupKey) {
    Set-ItemProperty -LiteralPath $packageStartupKey -Name State -Value 0 -Type DWord
}
Write-Output $shortcutPath
