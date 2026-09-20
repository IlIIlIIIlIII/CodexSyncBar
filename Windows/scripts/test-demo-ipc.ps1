# Runs only the memory-only --demo runtime. Never opens the production dashboard pipe.
param(
    [Parameter(Mandatory = $true)][string]$DemoExecutable,
    [Parameter(Mandatory = $true)][string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path $DemoExecutable).Path
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$hasher = [System.Security.Cryptography.SHA256]::Create()
try { $suffix = ([BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($identity)))).Replace('-', '').Substring(0,20) }
finally { $hasher.Dispose() }
$demoPipe = "CodexSyncBar.Dashboard.v1.$suffix.demo"
$results = [System.Collections.Generic.List[object]]::new()
function Check([string]$Name, [bool]$Condition) {
    $results.Add([pscustomobject]@{ name = $Name; passed = $Condition })
    if (-not $Condition) { throw "Demo QA failed: $Name" }
}
function Receive-Exact($Stream, [int]$Length) {
    $bytes = New-Object byte[] $Length
    $offset = 0
    while ($offset -lt $Length) {
        $count = $Stream.Read($bytes, $offset, $Length - $offset)
        if ($count -eq 0) { throw 'Demo pipe closed unexpectedly.' }
        $offset += $count
    }
    return ,$bytes
}
function Invoke-Demo([string]$Method, [hashtable]$Fields = @{}, [string]$RequestId = '') {
    if (!$RequestId) { $RequestId = [Guid]::NewGuid().ToString('N') }
    $request = @{ method = $Method; requestId = $RequestId }
    foreach ($key in $Fields.Keys) { $request[$key] = $Fields[$key] }
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $demoPipe, [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(5000)
        $payload = [Text.Encoding]::UTF8.GetBytes(($request | ConvertTo-Json -Compress))
        $header = [BitConverter]::GetBytes([int]$payload.Length)
        $pipe.Write($header, 0, $header.Length)
        $pipe.Write($payload, 0, $payload.Length)
        $pipe.Flush()
        $length = [BitConverter]::ToInt32((Receive-Exact $pipe 4), 0)
        if ($length -lt 2 -or $length -gt 262144) { throw 'Invalid response frame.' }
        return [Text.Encoding]::UTF8.GetString((Receive-Exact $pipe $length)) | ConvertFrom-Json
    }
    finally { $pipe.Dispose() }
}
$process = $null
try {
    # Refuse to attach to a pre-existing demo and interfere with another QA session.
    $probe = [IO.Pipes.NamedPipeClientStream]::new('.', $demoPipe, [IO.Pipes.PipeDirection]::InOut)
    $alreadyRunning = $false
    try { $probe.Connect(100); $alreadyRunning = $true } catch [TimeoutException] { } finally { $probe.Dispose() }
    if ($alreadyRunning) { throw 'Close the existing demo app before running isolated demo IPC QA.' }
    $process = Start-Process -FilePath $executable -ArgumentList '--demo','--background' -PassThru
    $snapshot = (Invoke-Demo 'GetSnapshot').snapshot
    Check 'Background startup serves synthetic accounts before UI Loaded' ($snapshot.accounts.Count -eq 2 -and $snapshot.activeProfileId -eq 1)
    $second = Start-Process -FilePath $executable -ArgumentList '--demo','--background' -PassThru
    Check 'Second demo launch redirects to the existing instance' ($second.WaitForExit(10000))
    $refresh = Invoke-Demo 'RefreshUsage'
    Check 'Refresh returns a safe usage snapshot' ($refresh.success -and $refresh.snapshot.accounts[0].usage.session.usedPercent -eq 28)
    $stale = Invoke-Demo 'SwitchAccount' @{ profileId = 2; configurationRevision = 'stale' }
    Check 'Stale widget configuration rejected' (!$stale.success -and $stale.errorCode -eq 'stale_configuration')
    $requestId = [Guid]::NewGuid().ToString('N')
    $fields = @{ profileId = 2; configurationRevision = $snapshot.configurationRevision }
    $switch = Invoke-Demo 'SwitchAccount' $fields $requestId
    Check 'Account selection starts one shared operation' ($switch.success -and $switch.operation.profileId -eq 2)
    $duplicate = Invoke-Demo 'SwitchAccount' $fields $requestId
    Check 'Repeated widget request is idempotent' ($duplicate.operation.id -eq $switch.operation.id)
    $busy = Invoke-Demo 'SwitchAccount' @{ profileId = 1; configurationRevision = $snapshot.configurationRevision }
    Check 'Concurrent account switch is rejected while busy' (!$busy.success -and $busy.errorCode -eq 'busy')
    $operation = $switch.operation
    for ($attempt = 0; $attempt -lt 30 -and !$operation.isComplete; $attempt++) {
        Start-Sleep -Milliseconds 100
        $operation = (Invoke-Demo 'GetOperationStatus' @{ operationId = $switch.operation.id }).operation
    }
    Check 'Switch reaches verified completion' ($operation.state -eq 'completed')
    $final = (Invoke-Demo 'GetSnapshot').snapshot
    Check 'Windows WSL SSH presentation uses the same applied account' ($final.activeProfileId -eq 2 -and @($final.devices | Where-Object { $_.profileId -ne 2 }).Count -eq 0)
    Check 'OpenSettings dispatches successfully' ((Invoke-Demo 'OpenSettings').success)
}
finally {
    $results | ConvertTo-Json -Depth 6 | Set-Content -Path $ReportPath -Encoding UTF8
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue }
}
$results | Format-Table -AutoSize
