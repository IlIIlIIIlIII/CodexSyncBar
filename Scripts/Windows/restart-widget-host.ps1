[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference = 'Stop'

# The picker can retain the catalog from before Developer Mode was enabled.
# Restart only this user's packaged widget hosts; keep all board data and apps.
$currentSession = (Get-Process -Id $PID).SessionId
$hosts = @(
    @{ Package = 'MicrosoftWindows.Client.WebExperience'; Process = 'WidgetBoard'; Relative = 'WidgetBoard.exe' },
    @{ Package = 'Microsoft.WidgetsPlatformRuntime'; Process = 'WidgetService'; Relative = 'WidgetService\WidgetService.exe' }
)
foreach ($hostEntry in $hosts) {
    $package = Get-AppxPackage -Name $hostEntry.Package
    if (!$package) { continue }
    $expectedPath = Join-Path $package.InstallLocation $hostEntry.Relative
    foreach ($process in @(Get-Process -Name $hostEntry.Process -ErrorAction SilentlyContinue)) {
        if ($process.SessionId -ne $currentSession -or $process.Path -ine $expectedPath) { continue }
        if ($PSCmdlet.ShouldProcess($process.ProcessName, 'Stop widget host for catalog reload')) {
            Stop-Process -Id $process.Id -ErrorAction Stop
            Write-Output "Stopped $($process.ProcessName)."
        }
    }
}
Write-Output 'Open Widgets from the taskbar, then open + to reload the widget picker. No widget data was removed.'
