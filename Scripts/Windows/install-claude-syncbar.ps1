<#
.SYNOPSIS
Builds Claude SyncBar for Windows 11 and installs it for the current user.

.DESCRIPTION
Publishes the WinUI app self-contained, so no .NET or Windows App SDK runtime has to be
installed, copies it to %LOCALAPPDATA%\Programs\ClaudeSyncBar, adds a Start menu shortcut
and, unless -NoLaunchAtLogin is given, starts it in the tray when the user signs in to
Windows. That is the same Run entry the app's own setting writes, so the setting shows
it. No administrator rights are needed. The accounts and settings the app keeps in
%LOCALAPPDATA%\claude-syncbar are never touched; -Uninstall removes only the program.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File Scripts\Windows\install-claude-syncbar.ps1
.EXAMPLE
powershell -ExecutionPolicy Bypass -File Scripts\Windows\install-claude-syncbar.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$NoLaunchAtLogin,
    [string]$Dotnet,
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\ClaudeSyncBar')
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory)
$executable = Join-Path $InstallDirectory 'ClaudeSyncBar.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue = 'ClaudeSyncBar'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Claude SyncBar.lnk'

# An update or removal must not cut off a switch in progress, so the running app is never killed.
function Assert-NotRunning {
    $running = Get-Process ClaudeSyncBar -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($InstallDirectory + '\', [StringComparison]::OrdinalIgnoreCase) }
    if ($running) {
        throw "설치된 Claude SyncBar가 실행 중이에요. 트레이 메뉴의 '완전히 종료'로 끈 뒤 다시 실행해 주세요."
    }
}

function Get-RunEntry {
    (Get-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue).$runValue
}

if ($Uninstall) {
    Assert-NotRunning
    # Only an entry that starts this installed copy is removed.
    if ((Get-RunEntry) -like "*$executable*") { Remove-ItemProperty -Path $runKey -Name $runValue }
    if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut }
    if (Test-Path -LiteralPath $InstallDirectory) { Remove-Item -LiteralPath $InstallDirectory -Recurse -Force }
    Write-Host "Claude SyncBar를 제거했어요. 계정과 설정은 $env:LOCALAPPDATA\claude-syncbar 에 그대로 남아 있어요."
    return
}

# The SDK on PATH, or the per-user one dotnet-install.ps1 puts in LocalAppData.
if (!$Dotnet) {
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    $perUser = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    $Dotnet = if ($onPath) { $onPath.Source } elseif (Test-Path -LiteralPath $perUser) { $perUser } else { throw '.NET 10 SDK를 찾지 못했어요. -Dotnet으로 dotnet.exe 경로를 알려 주세요.' }
}

$architecture = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
$publish = Join-Path ([IO.Path]::GetTempPath()) ('claude-syncbar-publish-' + [Guid]::NewGuid().ToString('N'))
$project = Join-Path $repo 'Windows\ClaudeSyncBar.Windows.App\ClaudeSyncBar.Windows.App.csproj'
$saved = @{ CLAUDE_CONFIG_DIR = $env:CLAUDE_CONFIG_DIR; CLAUDE_SYNCBAR_STATE_ROOT = $env:CLAUDE_SYNCBAR_STATE_ROOT }
try {
    # A build never inherits the caller's Claude paths.
    Remove-Item Env:CLAUDE_CONFIG_DIR, Env:CLAUDE_SYNCBAR_STATE_ROOT -ErrorAction SilentlyContinue
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    Write-Host "빌드하는 중이에요 (Release, win-$architecture)…"
    & $Dotnet publish $project -c Release -r "win-$architecture" -o $publish -v q -clp:NoSummary
    if ($LASTEXITCODE -ne 0) { throw "빌드에 실패했어요 (exit $LASTEXITCODE)." }
    if (!(Test-Path -LiteralPath (Join-Path $publish 'ClaudeSyncBar.exe'))) { throw '빌드 결과에 ClaudeSyncBar.exe가 없어요.' }
    if (!(Test-Path -LiteralPath (Join-Path $publish 'Runtime\ClaudeSyncBar.AskPass.exe'))) { throw '빌드 결과에 SSH 비밀번호 도우미가 없어요.' }

    Assert-NotRunning
    New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
    # Mirror, so files a newer build no longer has are removed too.
    & robocopy $publish $InstallDirectory /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "설치 폴더로 복사하지 못했어요 (robocopy $LASTEXITCODE)." }

    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    $link.TargetPath = $executable
    $link.WorkingDirectory = $InstallDirectory
    $link.IconLocation = "$executable,0"
    $link.Description = 'Claude Code와 Claude 데스크톱 계정 전환'
    $link.Save()

    if (!$NoLaunchAtLogin) {
        # The app's own "Windows에 로그인하면 시작" setting writes exactly this.
        New-ItemProperty -Path $runKey -Name $runValue -Value "`"$executable`" --background" -PropertyType String -Force | Out-Null
    }
}
finally {
    foreach ($name in $saved.Keys) { if ($null -ne $saved[$name]) { Set-Item "Env:$name" $saved[$name] } }
    if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
}

Write-Host "설치했어요: $executable"
Write-Host "시작 메뉴: Claude SyncBar"
Write-Host $(if ($NoLaunchAtLogin) { '로그인 시 시작: 바꾸지 않음' } elseif (Get-RunEntry) { '로그인 시 시작: 켜짐 (트레이에서 시작)' } else { '로그인 시 시작: 꺼짐' })
