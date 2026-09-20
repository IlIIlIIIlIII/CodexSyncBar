[CmdletBinding()]
param([switch]$Demo, [string]$OutputPath)
$ErrorActionPreference = 'Stop'
# Read-only IPC probe. Only aggregate counts and freshness are emitted; the raw
# snapshot, account names/IDs and credentials are never written to disk/output.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$sha = [Security.Cryptography.SHA256]::Create()
try { $suffix = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($identity.User.Value)))).Replace('-', '').Substring(0, 20) }
finally { $sha.Dispose(); $identity.Dispose() }
$name = 'CodexSyncBar.Dashboard.v1.' + $suffix
if ($Demo) { $name += '.demo' }
$pipe = New-Object IO.Pipes.NamedPipeClientStream('.', $name, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
$deadline = New-Object Threading.CancellationTokenSource
$deadline.CancelAfter(10000)
function Read-Exact([byte[]]$Buffer) {
    $offset = 0
    while ($offset -lt $Buffer.Length) {
        $read = $pipe.ReadAsync($Buffer, $offset, $Buffer.Length - $offset, $deadline.Token).GetAwaiter().GetResult()
        if ($read -eq 0) { throw 'Dashboard pipe closed before a complete response.' }
        $offset += $read
    }
}
try {
    $pipe.Connect(5000)
    $request = @{ method = 'GetSnapshot'; requestId = [Guid]::NewGuid().ToString('N') } | ConvertTo-Json -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($request)
    $header = [BitConverter]::GetBytes([int]$bytes.Length)
    $pipe.Write($header, 0, $header.Length)
    $pipe.Write($bytes, 0, $bytes.Length)
    $pipe.Flush()
    $header = New-Object byte[] 4
    Read-Exact $header
    $length = [BitConverter]::ToInt32($header, 0)
    if ($length -lt 2 -or $length -gt 262144) { throw 'Invalid dashboard response length.' }
    $responseBytes = New-Object byte[] $length
    Read-Exact $responseBytes
    $response = [Text.Encoding]::UTF8.GetString($responseBytes) | ConvertFrom-Json
    if (!$response.success -or !$response.snapshot) { throw 'Dashboard did not return a successful snapshot.' }
    $snapshot = $response.snapshot
    $withUsage = @($snapshot.accounts | Where-Object { $null -ne $_.usage })
    $fresh = @($withUsage | Where-Object { ([DateTimeOffset]::UtcNow - [DateTimeOffset]$_.usage.updatedAt).TotalMinutes -le 5 })
    $summary = [ordered]@{
        Source = $(if ($Demo) { 'demo' } else { 'production' })
        Method = 'GetSnapshot'
        AccountCount = @($snapshot.accounts).Count
        AccountsNeedingLogin = @($snapshot.accounts | Where-Object { $_.needsLogin }).Count
        AccountsWithUsage = $withUsage.Count
        UsageUpdatedWithinFiveMinutes = $fresh.Count
        SessionQuotaCount = @($withUsage | Where-Object { $null -ne $_.usage.session }).Count
        WeeklyQuotaCount = @($withUsage | Where-Object { $null -ne $_.usage.weekly }).Count
        DeviceCount = @($snapshot.devices).Count
        ReachableDeviceCount = @($snapshot.devices | Where-Object { $_.isReachable }).Count
        HasAppliedAccount = $null -ne $snapshot.activeProfileId
        IsBusy = [bool]$snapshot.isBusy
        HasDashboardError = [bool]$snapshot.error
        OperationState = $(if ($snapshot.operation) { $snapshot.operation.state } else { $null })
        ReadOnly = $true
    }
    $json = $summary | ConvertTo-Json
    if ($OutputPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $json + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false))) }
    Write-Output $json
} finally { $deadline.Dispose(); $pipe.Dispose() }
