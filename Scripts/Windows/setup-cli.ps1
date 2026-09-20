[CmdletBinding()]
param(
    [ValidateSet('x64','ARM64')][string]$Architecture,
    # Optional previously downloaded archive; it must match the same official release digest.
    [string]$ArchivePath,
    [string]$InstallDirectory = (Join-Path $env:USERPROFILE '.codex-syncbar/Tools')
)
$ErrorActionPreference = 'Stop'
$version = '0.155.1'
$tag = "rust-v$version"
if (!$Architecture) {
    $Architecture = switch ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()) {
        'X64' { 'x64' }
        'Arm64' { 'ARM64' }
        default { throw 'Codex SyncBar requires Windows x64 or ARM64.' }
    }
}
$target = if ($Architecture -eq 'ARM64') { 'aarch64-pc-windows-msvc' } else { 'x86_64-pc-windows-msvc' }
$assetName = "codex-$target.exe.zip"
$knownDigest = if ($Architecture -eq 'ARM64') {
    '90af85e067b2d5019f376414138cc7d177e225538fd2f4ad3bc60576cf83583c'
} else {
    'ce2269bdb7dfc06bb85c014c9c4e6b1601ffa0d646a2ae5b9b8cc8a427ef61fb'
}
$headers = @{ 'User-Agent' = 'CodexSyncBar-Setup'; 'Accept' = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/openai/codex/releases/tags/$tag" -Headers $headers
$assets = @($release.assets | Where-Object { $_.name -eq $assetName })
if ($release.tag_name -ne $tag -or $assets.Count -ne 1) { throw 'The official Codex release does not contain the requested architecture.' }
$asset = $assets[0]
if ($asset.digest -notmatch '^sha256:([a-fA-F0-9]{64})$') { throw 'The official release has no SHA-256 asset digest; installation stopped.' }
$apiDigest = $Matches[1].ToLowerInvariant()
if ($apiDigest -ne $knownDigest) { throw 'The release digest differs from the reviewed Codex version; installation stopped.' }
$expectedUrl = "https://github.com/openai/codex/releases/download/$tag/$assetName"
if ($asset.browser_download_url -ne $expectedUrl) { throw 'The release download URL is unexpected.' }

$InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory)
$ancestor = $InstallDirectory
while ($ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The CLI installation path must not contain a reparse point.'
    }
    $ancestor = [IO.Path]::GetDirectoryName($ancestor)
}
New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
$staging = Join-Path $InstallDirectory ('.setup-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    if ($ArchivePath) { $archive = (Resolve-Path -LiteralPath $ArchivePath).Path }
    else {
        $archive = Join-Path $staging $assetName
        Invoke-WebRequest -UseBasicParsing -Uri $expectedUrl -Headers $headers -OutFile $archive
    }
    $digest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($digest -ne $apiDigest) { throw 'The Codex archive SHA-256 does not match the official release; nothing was installed.' }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        # Copy only the expected executables; never extract arbitrary paths from an archive.
        $files = @("codex-$target.exe", 'codex-command-runner.exe', 'codex-windows-sandbox-setup.exe')
        foreach ($name in $files) {
            $entries = @($zip.Entries | Where-Object { $_.FullName -eq $name })
            if ($entries.Count -ne 1) { throw "The Codex archive is missing expected file: $name" }
            $destinationName = if ($name -eq "codex-$target.exe") { 'codex.exe' } else { $name }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], (Join-Path $staging $destinationName), $false)
        }
    }
    finally { $zip.Dispose() }

    # Native-architecture smoke check does not start login or open an authentication file.
    $nativeArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    if (($Architecture -eq 'x64' -and $nativeArchitecture -eq 'X64') -or ($Architecture -eq 'ARM64' -and $nativeArchitecture -eq 'Arm64')) {
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = Join-Path $staging 'codex.exe'
        $start.Arguments = '--version'
        $start.WorkingDirectory = $staging
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $process = [Diagnostics.Process]::Start($start)
        try {
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $stderrTask = $process.StandardError.ReadToEndAsync()
            if (!$process.WaitForExit(15000)) { $process.Kill(); throw 'Codex version verification timed out.' }
            $stdout = $stdoutTask.GetAwaiter().GetResult()
            $null = $stderrTask.GetAwaiter().GetResult()
            if ($process.ExitCode -ne 0 -or $stdout.Trim() -ne "codex-cli $version") { throw 'The CLI executable did not report the expected Codex version.' }
        }
        finally { $process.Dispose() }
    }
    foreach ($name in @('codex-command-runner.exe', 'codex-windows-sandbox-setup.exe', 'codex.exe')) {
        $destination = Join-Path $InstallDirectory $name
        if ((Test-Path -LiteralPath $destination) -and ((Get-Item -LiteralPath $destination -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'An existing CLI executable is a reparse point; installation stopped.'
        }
        $source = Join-Path $staging $name
        if (Test-Path -LiteralPath $destination) { [IO.File]::Replace($source, $destination, $null) }
        else { [IO.File]::Move($source, $destination) }
    }
    [pscustomobject]@{ Version = $version; Architecture = $Architecture; Executable = (Join-Path $InstallDirectory 'codex.exe'); Sha256 = $digest; PathChanged = $false }
}
finally { if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force } }
