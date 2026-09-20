[CmdletBinding()]
param(
    [ValidateSet('x64','ARM64')][string]$Architecture = 'x64',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$Dotnet = 'dotnet',
    [string]$OutputDirectory,
    [switch]$Package,
    [string]$CertificateThumbprint,
    [switch]$DemoWidgets
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'dist/windows' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$rid = 'win-' + $Architecture.ToLowerInvariant()
$widgetOutput = Join-Path $OutputDirectory "widget-payload/$rid"
$oldCodexHome = $env:CODEX_HOME
$oldStateRoot = $env:CODEX_SYNCBAR_STATE_ROOT
# Build and tests must never inherit the agent's real authentication paths.
Remove-Item Env:CODEX_HOME,Env:CODEX_SYNCBAR_STATE_ROOT -ErrorAction SilentlyContinue
$oldPath = $env:Path
$oldPathExt = $env:PATHEXT
$env:PATHEXT = ((@(".COM", ".EXE", ".BAT", ".CMD") + ($env:PATHEXT -split ";")) | Select-Object -Unique) -join ";"
$oldUiLanguage = $env:DOTNET_CLI_UI_LANGUAGE
# WSL interop can otherwise pass a colon-separated Linux PATH to cmd.exe.
$windowsProcessPath = (($oldPath -split ';') | Where-Object { $_ -match '^(?:[A-Za-z]:[\\/]|\\\\)' }) -join ';'
$env:Path = $windowsProcessPath + ';' + [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
if (Test-Path $Dotnet) {
    $Dotnet = (Resolve-Path $Dotnet).Path
    $env:Path = (Split-Path $Dotnet) + ';' + $env:Path
}
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
function Invoke-Dotnet([string[]]$Arguments) {
    # Process.Start works consistently under Windows PowerShell launched by WSL.
    # Quote each argument for the Windows command-line parser; never evaluate it.
    $quoted = foreach ($argument in $Arguments) {
        if ($argument -match '[\s"]' -or $argument.Length -eq 0) {
            '"' + ([regex]::Replace([regex]::Replace($argument, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1')) + '"'
        } else { $argument }
    }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Dotnet
    $start.Arguments = $quoted -join ' '
    $start.WorkingDirectory = $repo
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (!$process.Start()) { throw 'Unable to start dotnet.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        Write-Output $stdout.Result
        if ($stderr.Result) { Write-Output $stderr.Result }
        if ($process.ExitCode -ne 0) { throw "dotnet failed ($($process.ExitCode)): $($Arguments[0])" }
    } finally { $process.Dispose() }
}
function Get-NodeExecutable([string]$NodeRid) {
    $runtime = Join-Path $OutputDirectory "node/$NodeRid"
    New-Item -ItemType Directory -Force $runtime | Out-Null
    $nodeName = 'node-v22.23.2-' + $NodeRid + '.zip'
    $nodeArchive = Join-Path $runtime $nodeName
    $nodeExe = Join-Path $runtime ('node-v22.23.2-' + $NodeRid + '/node.exe')
    if (!(Test-Path $nodeExe)) {
        $base = 'https://nodejs.org/dist/v22.23.2/'
        Invoke-WebRequest ($base + 'SHASUMS256.txt') -OutFile (Join-Path $runtime 'SHASUMS256.txt') -UseBasicParsing
        Invoke-WebRequest ($base + $nodeName) -OutFile $nodeArchive -UseBasicParsing
        $expected = ((Get-Content (Join-Path $runtime 'SHASUMS256.txt') | Where-Object { $_.EndsWith('  ' + $nodeName) }) -split '\s+')[0]
        if (!$expected -or (Get-FileHash $nodeArchive -Algorithm SHA256).Hash -ine $expected) { throw 'Node.js checksum mismatch.' }
        Expand-Archive $nodeArchive -DestinationPath $runtime -Force
    }
    return $nodeExe
}
try {
    $nodeExe = Get-NodeExecutable $rid
    $hostRid = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
    $testNodeExe = if ($hostRid -eq $rid) { $nodeExe } else { Get-NodeExecutable $hostRid }
    $env:Path = (Split-Path $testNodeExe) + ';' + $env:Path
    Invoke-Dotnet @('test', (Join-Path $repo 'Windows/CodexSyncBar.Windows.Core.Tests/CodexSyncBar.Windows.Core.Tests.csproj'), '-c', $Configuration, '--nologo', '--logger', 'trx', '--results-directory', (Join-Path $OutputDirectory 'tests'))
    Invoke-Dotnet @('publish', (Join-Path $repo 'Windows/CodexSyncBar.Windows.Widgets/CodexSyncBar.Windows.Widgets.csproj'), '-c', $Configuration, '-r', $rid, '--self-contained', 'true', ('-p:Platform=' + $Architecture), '-o', $widgetOutput, '--nologo')
    $askPassOutput = Join-Path $OutputDirectory "askpass-payload/$rid"
    Invoke-Dotnet @('publish', (Join-Path $repo 'Windows/CodexSyncBar.Windows.AskPass/CodexSyncBar.Windows.AskPass.csproj'), '-c', $Configuration, '-r', $rid, '--self-contained', 'true', '-p:PublishSingleFile=true', '-o', $askPassOutput, '--nologo')
    $app = Join-Path $repo 'Windows/CodexSyncBar.Windows/CodexSyncBar.Windows.csproj'
    $variant = if ($DemoWidgets) { '-demo' } else { '' }
    $arguments = @('build', $app, '-c', $Configuration, '-r', $rid, ('-p:Platform=' + $Architecture),
        ('-p:WidgetPayloadDir=' + $widgetOutput), ('-p:AskPassPayloadDir=' + $askPassOutput), ('-p:SyncBarNodePath=' + $nodeExe), '--nologo', ('-bl:' + (Join-Path $OutputDirectory "build-$rid$variant.binlog")))
    if ($Package) {
        $packageDirectory = if ($DemoWidgets) { Join-Path $OutputDirectory 'demo' } else { $OutputDirectory }
        $arguments += @('-p:GenerateAppxPackageOnBuild=true', '-p:AppxBundle=Never', '-p:UapAppxPackageBuildMode=SideloadOnly', ('-p:AppxPackageDir=' + $packageDirectory + '\'))
        if ($CertificateThumbprint) { $arguments += @('-p:AppxPackageSigningEnabled=true', ('-p:PackageCertificateThumbprint=' + $CertificateThumbprint)) }
        else { $arguments += '-p:AppxPackageSigningEnabled=false' }
    }
    if ($DemoWidgets) {
        # Use a separate build input; never temporarily rewrite tracked source.
        $manifestPath = Join-Path $repo 'Windows/CodexSyncBar.Windows/Package.appxmanifest'
        [xml]$demoManifest = [IO.File]::ReadAllText($manifestPath)
        $namespaces = New-Object Xml.XmlNamespaceManager($demoManifest.NameTable)
        $namespaces.AddNamespace('com', 'http://schemas.microsoft.com/appx/manifest/com/windows10')
        $server = $demoManifest.SelectSingleNode('//com:ExeServer', $namespaces)
        if (!$server) { throw 'Widget COM registration is missing from the package manifest.' }
        $server.SetAttribute('Arguments', '--demo')
        $demoManifestPath = Join-Path $OutputDirectory "Package.$rid.demo.appxmanifest"
        $demoManifest.Save($demoManifestPath)
        $arguments += ('-p:SyncBarPackageManifest=' + $demoManifestPath)
    }
    Invoke-Dotnet $arguments
} finally {
    $env:Path = $oldPath
    $env:PATHEXT = $oldPathExt
    $env:DOTNET_CLI_UI_LANGUAGE = $oldUiLanguage
    $env:CODEX_HOME = $oldCodexHome
    $env:CODEX_SYNCBAR_STATE_ROOT = $oldStateRoot
}
