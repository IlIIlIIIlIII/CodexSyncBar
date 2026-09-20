[CmdletBinding()]
param(
    [string]$PackageName = '14769529-7449-442E-89BB-30402D814A1D',
    [switch]$ActivateWidgetProvider,
    [switch]$ConstructWidgetProvider
)
$ErrorActionPreference = 'Stop'
$package = Get-AppxPackage -Name $PackageName
if (!$package) { throw 'Codex SyncBar MSIX is not installed for the current user.' }
$manifest = Get-AppxPackageManifest -Package $package.PackageFullName
$namespaces = New-Object Xml.XmlNamespaceManager($manifest.NameTable)
$namespaces.AddNamespace('com', 'http://schemas.microsoft.com/appx/manifest/com/windows10')
$namespaces.AddNamespace('uap3', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/3')
$namespaces.AddNamespace('uap5', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/5')
$server = $manifest.SelectSingleNode('//com:ExeServer', $namespaces)
$widgetExtension = $manifest.SelectSingleNode('//uap3:AppExtension[@Name="com.microsoft.windows.widgets"]', $namespaces)
$startup = $manifest.SelectSingleNode('//uap5:StartupTask[@TaskId="CodexSyncBar.Startup"]', $namespaces)
if (!$server -or !$widgetExtension -or !$startup) { throw 'Installed package is missing widget COM/app extension or StartupTask registration.' }
$definition = $widgetExtension.SelectSingleNode('.//*[local-name()="Definition"]')
if (!$definition) { throw 'Installed widget extension has no definition.' }
$classId = [Guid]$server.SelectSingleNode('com:Class', $namespaces).Id
$provider = Join-Path $package.InstallLocation $server.Executable
foreach ($relativePath in @('CodexSyncBar.Windows.exe', 'Widgets\CodexSyncBar.Windows.Widgets.exe', 'Runtime\node.exe', 'Runtime\gpt-switch', 'Runtime\AskPass\CodexSyncBar.AskPass.exe', 'Assets\WidgetPreview.png')) {
    if (!(Test-Path (Join-Path $package.InstallLocation $relativePath))) { throw ('Installed payload is missing ' + $relativePath) }
}

# The package manifest and COM registration alone do not prove that Windows has
# discovered the app extension. These public WinRT calls are read-only; they do
# not launch a provider, create a widget or ask for the app's dashboard/auth data.
Add-Type -AssemblyName System.Runtime.WindowsRuntime
[void][Windows.ApplicationModel.AppExtensions.AppExtensionCatalog, Windows.ApplicationModel.AppExtensions, ContentType=WindowsRuntime]
[void][Windows.ApplicationModel.AppExtensions.AppExtension, Windows.ApplicationModel.AppExtensions, ContentType=WindowsRuntime]
$asTask = @([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetGenericArguments().Count -eq 1 -and
    $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
})[0]
function Wait-WinRtOperation {
    param([object]$Operation, [Type]$ResultType)
    $task = $asTask.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    if (!$task.Wait(15000)) { throw 'Windows app-extension query exceeded 15 seconds.' }
    return ,$task.Result
}
$catalog = [Windows.ApplicationModel.AppExtensions.AppExtensionCatalog]::Open('com.microsoft.windows.widgets')
$extensions = Wait-WinRtOperation $catalog.FindAllAsync() ([System.Collections.Generic.IReadOnlyList[Windows.ApplicationModel.AppExtensions.AppExtension]])
$matchingExtensions = @($extensions | Where-Object {
    $_.Package.Id.FullName -eq $package.PackageFullName -and $_.Id -eq $widgetExtension.Id
})
if ($matchingExtensions.Count -ne 1) { throw 'Windows app-extension catalog did not return exactly one matching installed widget provider.' }
$registeredExtension = $matchingExtensions[0]
if (!$registeredExtension.Package.Status.VerifyIsOK()) { throw 'Windows reports the widget extension package is not healthy.' }
$extensionType = [Windows.ApplicationModel.AppExtensions.AppExtension]
$propertiesType = $extensionType.GetMethod('GetExtensionPropertiesAsync').ReturnType.GenericTypeArguments[0]
$properties = Wait-WinRtOperation $registeredExtension.GetExtensionPropertiesAsync() $propertiesType
$widgetProperties = $properties['WidgetProvider']
$registeredDefinition = $widgetProperties['Definitions']['Definition']
if ([Guid]$widgetProperties['Activation']['CreateInstance']['@ClassId'] -ne $classId -or
    $registeredDefinition['@Id'] -ne $definition.Id -or
    $registeredDefinition['@DisplayName'] -ne $definition.DisplayName -or
    $registeredDefinition['@Description'] -ne $definition.Description -or
    $registeredDefinition['@AllowMultiple'] -ne $definition.AllowMultiple) {
    throw 'Windows app-extension properties differ from the installed widget definition or COM class.'
}
$manifestSizes = @($definition.SelectNodes('.//*[local-name()="Capabilities"]/*[local-name()="Capability"]/*[local-name()="Size"]') | ForEach-Object { $_.Name } | Sort-Object)
$registeredSizes = @($registeredDefinition['Capabilities']['Capability'] | ForEach-Object { $_['Size']['@Name'] } | Sort-Object)
if (($manifestSizes -join ',') -ne ($registeredSizes -join ',')) {
    throw 'Windows app-extension size capabilities differ from the installed manifest.'
}
$manifestIcon = $definition.SelectSingleNode('.//*[local-name()="Icons"]/*[local-name()="Icon"]')
$manifestScreenshot = $definition.SelectSingleNode('.//*[local-name()="Screenshots"]/*[local-name()="Screenshot"]')
if (!$manifestIcon -or !$manifestScreenshot -or
    $registeredDefinition['ThemeResources']['Icons']['Icon']['@Path'] -ne $manifestIcon.Path -or
    $registeredDefinition['ThemeResources']['Screenshots']['Screenshot']['@Path'] -ne $manifestScreenshot.Path) {
    throw 'Windows app-extension image paths differ from the installed manifest.'
}
$folderType = $extensionType.GetMethod('GetPublicFolderAsync').ReturnType.GenericTypeArguments[0]
$publicFolder = Wait-WinRtOperation $registeredExtension.GetPublicFolderAsync() $folderType
$expectedPublicFolder = [IO.Path]::GetFullPath((Join-Path $package.InstallLocation $widgetExtension.PublicFolder))
if (![string]::Equals($publicFolder.Path, $expectedPublicFolder, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Windows app-extension public folder differs from the installed manifest.'
}

# Preview sizing is a separate design-guidance check, not proof that the Widgets
# Board has accepted or rendered this provider. In particular, discovery may
# succeed even when the preview does not meet the documented 300 x 304 guidance.
# https://learn.microsoft.com/windows/apps/design/widgets/widgets-picker-integration
Add-Type -AssemblyName System.Drawing
$packageRoot = [IO.Path]::GetFullPath($package.InstallLocation).TrimEnd('\') + '\'
$widgetImages = @($widgetExtension.SelectNodes('.//*[local-name()="Icon" or local-name()="Screenshot"]') | ForEach-Object {
    $imageNode = $_
    $imagePath = [IO.Path]::GetFullPath((Join-Path $packageRoot $imageNode.Path))
    if (!$imagePath.StartsWith($packageRoot, [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $imagePath -PathType Leaf)) {
        throw ('Installed widget image is missing or outside its package: ' + $imageNode.Path)
    }
    $bitmap = [Drawing.Bitmap]::new($imagePath)
    try {
        $transparentCorners = $bitmap.GetPixel(0, 0).A -eq 0 -and
            $bitmap.GetPixel($bitmap.Width - 1, 0).A -eq 0 -and
            $bitmap.GetPixel(0, $bitmap.Height - 1).A -eq 0 -and
            $bitmap.GetPixel($bitmap.Width - 1, $bitmap.Height - 1).A -eq 0
        [pscustomobject]@{
            Kind = $imageNode.LocalName
            Path = $imageNode.Path
            Width = $bitmap.Width
            Height = $bitmap.Height
            TransparentCorners = $transparentCorners
            PreviewGuidanceCompliant = if ($imageNode.LocalName -eq 'Screenshot') {
                $bitmap.Width -eq 300 -and $bitmap.Height -eq 304 -and $transparentCorners
            } else { $null }
        }
    } finally { $bitmap.Dispose() }
})
$previewGuidanceCompliant = @($widgetImages | Where-Object { $_.Kind -eq 'Screenshot' -and !$_.PreviewGuidanceCompliant }).Count -eq 0
$unlock = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue
$developerMode = $unlock.AllowDevelopmentWithoutDevLicense
$activated = $false
$constructed = $false
if ($ActivateWidgetProvider -or $ConstructWidgetProvider) {
    # Request only the class factory, not IWidgetProvider.CreateWidget. This starts
    # the registered provider executable without requesting account data or auth.
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SyncBarComProbe {
    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr reserved, uint model);
    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();
    [DllImport("ole32.dll")]
    public static extern int CoGetClassObject(ref Guid clsid, uint context, IntPtr reserved, ref Guid iid, out IntPtr result);
    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr result);
}
'@
    $initialized = [SyncBarComProbe]::CoInitializeEx([IntPtr]::Zero, 2)
    $factory = [IntPtr]::Zero
    try {
        if ($initialized -lt 0 -and $initialized -ne -2147417850) { [Runtime.InteropServices.Marshal]::ThrowExceptionForHR($initialized) }
        $factoryId = [Guid]'00000001-0000-0000-C000-000000000046'
        $result = [SyncBarComProbe]::CoGetClassObject([ref]$classId, 4, [IntPtr]::Zero, [ref]$factoryId, [ref]$factory)
        [Runtime.InteropServices.Marshal]::ThrowExceptionForHR($result)
        $activated = $factory -ne [IntPtr]::Zero
        if ($ConstructWidgetProvider) {
            # IUnknown construction follows the Microsoft widget factory sample,
            # WidgetManager.GetWidgetInfos and its read-only dashboard connection.
            # It never calls CreateWidget or an account-switch action.
            $providerInterfaceId = [Guid]'00000000-0000-0000-C000-000000000046'
            $instance = [IntPtr]::Zero
            try {
                $result = [SyncBarComProbe]::CoCreateInstance([ref]$classId, [IntPtr]::Zero, 4, [ref]$providerInterfaceId, [ref]$instance)
                [Runtime.InteropServices.Marshal]::ThrowExceptionForHR($result)
                $constructed = $instance -ne [IntPtr]::Zero
            } finally {
                if ($instance -ne [IntPtr]::Zero) { [void][Runtime.InteropServices.Marshal]::Release($instance) }
            }
        }
    } finally {
        if ($factory -ne [IntPtr]::Zero) { [void][Runtime.InteropServices.Marshal]::Release($factory) }
        if ($initialized -ge 0) { [SyncBarComProbe]::CoUninitialize() }
    }
}
[pscustomobject]@{
    Package = $package.PackageFullName
    Status = $package.Status.ToString()
    Architecture = $package.Architecture.ToString()
    PayloadVerified = $true
    DeveloperModeEnabled = $developerMode -eq 1
    DeveloperModeRegistryValue = $developerMode
    WidgetDefinition = $definition.Id
    ComClass = $classId.ToString().ToUpperInvariant()
    OsAppExtensionDiscovered = $true
    OsAppExtensionPropertiesVerified = $true
    OsAppExtensionPublicFolderVerified = $true
    WidgetImages = $widgetImages
    PreviewGuidanceCompliant = $previewGuidanceCompliant
    PreviewGuidanceNote = 'Checks 300 x 304 dimensions and transparent corner pixels; medium-card content and rounded shape require visual review.'
    StartupTask = $startup.TaskId
    ProviderClassFactoryActivated = $activated
    ProviderInstanceConstructed = $constructed
    WidgetsBoardVisualQa = 'Not exercised by this script'
} | ConvertTo-Json -Depth 5
