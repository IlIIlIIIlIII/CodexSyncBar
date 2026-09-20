[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [string]$DevelopmentCertificate,
    [switch]$TrustDevelopmentCertificate,
    [switch]$ForceUpdateFromAnyVersion,
    [switch]$CloseRunningApp
)
$ErrorActionPreference = 'Stop'
$PackagePath = (Resolve-Path $PackagePath).Path
if ($DevelopmentCertificate) {
    if (!$TrustDevelopmentCertificate) { throw 'Pass -TrustDevelopmentCertificate to trust the explicitly selected development publisher.' }
    $certificatePath = (Resolve-Path $DevelopmentCertificate).Path
    $certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2($certificatePath)
    if ($certificate.Subject -ne 'CN=CodexSyncBar') { throw 'The selected certificate publisher does not match CN=CodexSyncBar.' }
    if (!(Test-Path ('Cert:\LocalMachine\TrustedPeople\' + $certificate.Thumbprint))) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Windows MSIX requires the development publisher in LocalMachine\TrustedPeople (CurrentUser trust fails with 0x800B0109). Run this same install command once in PowerShell as Administrator to trust the explicitly selected certificate.'
        }
        Import-Certificate -FilePath $certificatePath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    }
}
Add-AppxPackage -Path $PackagePath -ForceUpdateFromAnyVersion:$ForceUpdateFromAnyVersion -ForceTargetApplicationShutdown:$CloseRunningApp
Get-AppxPackage -Name '14769529-7449-442E-89BB-30402D814A1D' | Select-Object Name,Version,Status,InstallLocation
