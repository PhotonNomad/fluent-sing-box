[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('user','machine','both')][string]$Scope = 'both',
    [switch]$SkipPublish,
    [string]$CertificateThumbprint,
    [string]$SignToolPath,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path "$PSScriptRoot/..").Path
$taskSettings = Get-Content (Join-Path $taskRoot 'release.json') -Raw | ConvertFrom-Json
# Never ship legacy local-feed configuration, even if an old release.json contains it.
$taskSettings.PSObject.Properties.Remove('updateFeedDirectory')
$taskSettings.PSObject.Properties.Remove('updateManifestPublicKey')
if (!$Version) { $Version = $taskSettings.version }
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Version must be MAJOR.MINOR.PATCH.' }
$taskVersionParts = $Version.Split('.') | ForEach-Object { [int]$_ }
if ($taskVersionParts[0] -gt 255 -or $taskVersionParts[1] -gt 255 -or $taskVersionParts[2] -gt 65535) { throw 'Version exceeds MSI limits.' }
$taskWix = Join-Path $taskRoot 'artifacts/tools/wix/wix.exe'
if (!(Test-Path -LiteralPath $taskWix)) { throw 'Run tools/Setup-InstallerTools.ps1 first.' }
if ($CertificateThumbprint -and (!(Test-Path -LiteralPath $SignToolPath) -or !$taskSettings.publisherCertificateSha256)) { throw 'Signing requires SignToolPath and release.json publisherCertificateSha256.' }
function Invoke-Wix([string[]]$Arguments) {
    & $taskWix @Arguments
    if ($LASTEXITCODE -ne 0) { throw "WiX failed: $($Arguments[0])" }
}
function Sign-Artifact([string]$Path) {
    if (!$CertificateThumbprint) { return }
    & $SignToolPath sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $Path
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $Path" }
    & $SignToolPath verify /pa $Path
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed: $Path" }
    $taskSignature = Get-AuthenticodeSignature -LiteralPath $Path
    $taskCertificateHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskSignature.SignerCertificate.RawData))
    if ($taskCertificateHash -ne $taskSettings.publisherCertificateSha256) { throw "Signing certificate does not match release.json: $Path" }
}
function Escape-Xml([string]$Value) { [Security.SecurityElement]::Escape($Value) }
function Get-InstallerString([string]$Culture, [string]$Id) {
    $taskLoc = [xml](Get-Content -LiteralPath (Join-Path $taskRoot "installer/Strings.$Culture.wxl") -Raw -Encoding utf8)
    $taskValue = ($taskLoc.WixLocalization.String | Where-Object Id -eq $Id).Value
    if (!$taskValue) { throw "Missing installer string $Id in $Culture." }
    $taskValue
}
# Shown by the MSI while fluent sing-box is running. The MSI database is single-byte
# (1033), so it carries English; the bundle passes the Chinese text when its UI is Chinese.
$taskAppRunningEnglish = Get-InstallerString 'en-US' 'AppRunningMessage'
$taskAppRunningChinese = Get-InstallerString 'zh-CN' 'AppRunningMessage'
function Get-Id([string]$Value) {
    $taskBytes = [Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())
    $taskHash = [Security.Cryptography.SHA256]::Create()
    try { 'I' + ([BitConverter]::ToString($taskHash.ComputeHash($taskBytes))).Replace('-','').Substring(0,30) }
    finally { $taskHash.Dispose() }
}
function Get-ComponentGuid([string]$Value) {
    $taskHash = [Security.Cryptography.SHA256]::Create()
    try {
        $taskBytes = $taskHash.ComputeHash([Text.Encoding]::UTF8.GetBytes('FluentSingBox/' + $Value.ToLowerInvariant()))[0..15]
        $taskBytes[7] = ($taskBytes[7] -band 0x0f) -bor 0x50
        $taskBytes[8] = ($taskBytes[8] -band 0x3f) -bor 0x80
        ([guid]::new([byte[]]$taskBytes)).ToString().ToUpperInvariant()
    }
    finally { $taskHash.Dispose() }
}
if (!$SkipPublish) { & "$PSScriptRoot/Build.ps1" -Configuration Release -SelfContained -Version $Version }
$taskPublish = Join-Path $taskRoot 'artifacts/publish/Release'
if (!(Test-Path (Join-Path $taskPublish 'coreclr.dll'))) { throw 'Installer requires a self-contained Release publish.' }
$taskPublishedVersion = (Get-Content (Join-Path $taskPublish 'release.json') -Raw | ConvertFrom-Json).version
if ($taskPublishedVersion -ne $Version) { throw 'Published version mismatch. Rebuild without -SkipPublish.' }
$taskOutput = Join-Path $taskRoot "artifacts/installers/$Version"
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskUpdaterOutput = Join-Path $taskRoot 'artifacts/updater/Release'
& dotnet publish (Join-Path $taskRoot 'src/FluentSingBox.Updater/FluentSingBox.Updater.csproj') -c Release "-p:Version=$Version" --self-contained true -o $taskUpdaterOutput --nologo
if ($LASTEXITCODE -ne 0) { throw 'Updater publish failed.' }
$taskScopes = if ($Scope -eq 'both') { @('user','machine') } else { @($Scope) }
foreach ($taskScope in $taskScopes) {
    # Unique staging directories; do not modify runnable Release output or user data.
    $taskStage = Join-Path $taskOutput ('.staging-' + $taskScope + '-' + [guid]::NewGuid().ToString('N'))
    $taskPayload = Join-Path $taskStage 'payload'
    New-Item -ItemType Directory -Path $taskPayload -Force | Out-Null
    Copy-Item -Path (Join-Path $taskPublish '*') -Destination $taskPayload -Recurse
    Copy-Item -LiteralPath (Join-Path $taskUpdaterOutput 'FluentSingBox.Update.exe') -Destination $taskPayload
    $taskSettings.version = $Version
    $taskSettings | ConvertTo-Json | Set-Content (Join-Path $taskPayload 'release.json') -Encoding utf8
    Set-Content (Join-Path $taskPayload 'install-scope.txt') -Value $taskScope -Encoding ascii
    if (Test-Path (Join-Path $taskPayload 'portable.txt')) { throw 'Installer payload must not contain portable.txt.' }
    if (Test-Path (Join-Path $taskPayload 'data')) { throw 'Installer payload must not contain user data.' }
    Sign-Artifact (Join-Path $taskPayload 'fluent sing-box.exe')
    Sign-Artifact (Join-Path $taskPayload 'fluent sing-box.dll')
    Sign-Artifact (Join-Path $taskPayload 'FluentSingBox.Updates.dll')
    Sign-Artifact (Join-Path $taskPayload 'FluentSingBox.Update.exe')
    $taskUser = $taskScope -eq 'user'
    $taskRegistry = if ($taskUser) { 'HKCU' } else { 'HKLM' }
    $taskOpposite = if ($taskUser) { 'HKLM' } else { 'HKCU' }
    $taskInstallScope = if ($taskUser) { 'perUser' } else { 'perMachine' }
    $taskParent = if ($taskUser) { 'LocalAppDataFolder' } else { 'ProgramFiles64Folder' }
    $taskMsiUpgrade = if ($taskUser) { 'E4A6C37D-D12A-4E6C-B699-113CD11DB14E' } else { 'F06A128B-20E4-43C4-A87F-7292B931E903' }
    $taskBundleUpgrade = if ($taskUser) { '4051E629-E6A4-4D13-AC68-C98FAED64A7A' } else { 'F67E4CE6-629A-4EBB-B66C-18AD1DE0D2F2' }
    $taskBaseName = if ($taskUser) { "FluentSingBoxUserSetup-$Version-x64" } else { "FluentSingBoxSetup-$Version-x64" }
    $taskComponents = [Text.StringBuilder]::new()
    $taskDirectories = [Text.StringBuilder]::new()
    $taskDirMap = @{ '' = 'INSTALLFOLDER' }
    foreach ($taskDir in (Get-ChildItem -LiteralPath $taskPayload -Directory -Recurse | Sort-Object FullName)) {
        $taskRelative = $taskDir.FullName.Substring($taskPayload.Length + 1)
        $taskParentRelative = Split-Path $taskRelative -Parent
        if (!$taskParentRelative) { $taskParentRelative = '' }
        $taskId = Get-Id "dir/$taskRelative"
        $taskDirMap[$taskRelative] = $taskId
        [void]$taskDirectories.AppendLine("<DirectoryRef Id=`"$($taskDirMap[$taskParentRelative])`"><Directory Id=`"$taskId`" Name=`"$(Escape-Xml $taskDir.Name)`" /></DirectoryRef>")
    }
    foreach ($taskFile in (Get-ChildItem -LiteralPath $taskPayload -File -Recurse | Sort-Object FullName)) {
        if ($taskFile.Extension -eq '.pdb') { continue }
        $taskRelative = $taskFile.FullName.Substring($taskPayload.Length + 1)
        $taskRelativeDir = Split-Path $taskRelative -Parent
        if (!$taskRelativeDir) { $taskRelativeDir = '' }
        $taskId = Get-Id "file/$taskRelative"
        $taskGuid = Get-ComponentGuid "$taskScope/file/$taskRelative"
        $taskKey = if ($taskUser) { "<RegistryValue Root=`"HKCU`" Key=`"Software\PhotonNomad\FluentSingBox\Components`" Name=`"$taskId`" Type=`"integer`" Value=`"1`" KeyPath=`"yes`" />" } else { '' }
        $taskFileKey = if ($taskUser) { '' } else { ' KeyPath="yes"' }
        [void]$taskComponents.AppendLine("<Component Id=`"C$taskId`" Directory=`"$($taskDirMap[$taskRelativeDir])`" Guid=`"$taskGuid`"><File Id=`"F$taskId`" Source=`"$(Escape-Xml $taskFile.FullName)`"$taskFileKey />$taskKey</Component>")
    }
    foreach ($taskDirectory in $taskDirMap.Values) {
        [void]$taskComponents.AppendLine("<Component Id=`"R$taskDirectory`" Directory=`"$taskDirectory`" Guid=`"*`"><RemoveFolder Id=`"R$taskDirectory`" On=`"uninstall`" /><RegistryValue Root=`"$taskRegistry`" Key=`"Software\PhotonNomad\FluentSingBox\Folders`" Name=`"$taskDirectory`" Type=`"integer`" Value=`"1`" KeyPath=`"yes`" /></Component>")
    }
    $taskInstallDirectory = if ($taskUser) { '<Directory Id="UserPrograms" Name="Programs"><Directory Id="INSTALLFOLDER" Name="fluent sing-box" /></Directory>' } else { '<Directory Id="INSTALLFOLDER" Name="fluent sing-box" />' }
    $taskDefaultFolder = if ($taskUser) { '[LocalAppDataFolder]Programs\fluent sing-box' } else { '[ProgramFiles64Folder]fluent sing-box' }
    $taskMainFileId = 'F' + (Get-Id 'file/fluent sing-box.exe')
    $taskWxs = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs" xmlns:ui="http://wixtoolset.org/schemas/v4/wxs/ui" xmlns:util="http://wixtoolset.org/schemas/v4/wxs/util">
  <Package Name="fluent sing-box" Manufacturer="PhotonNomad" Version="$Version" Language="1033" Scope="$taskInstallScope" UpgradeCode="$taskMsiUpgrade">
    <MajorUpgrade DowngradeErrorMessage="A newer version is already installed." Schedule="afterInstallInitialize" />
    <MediaTemplate EmbedCab="yes" CompressionLevel="high" />
    <!-- Restart Manager detects files in use by path, but never shuts the app down:
         closing it without its own exit cleanup would leave the system proxy and core behind. -->
    <Property Id="MSIRESTARTMANAGERCONTROL" Value="DisableShutdown" />
    <Property Id="FSBAPPRUNNINGMESSAGE" Value="$(Escape-Xml $taskAppRunningEnglish)" Secure="yes" />
    <!-- The tray-hidden window is invisible to the FilesInUse dialog, so ask by process name
         and wait until the user exits from the tray (Retry rechecks; Abort cancels setup). -->
    <util:CloseApplication Id="RunningClient" Target="fluent sing-box.exe" Description="[FSBAPPRUNNINGMESSAGE]" PromptToContinue="yes" CloseMessage="no" EndSessionMessage="no" RebootPrompt="no" />
    <Property Id="ARPPRODUCTICON" Value="AppIcon" />
    <Property Id="ARPURLINFOABOUT" Value="https://github.com/PhotonNomad/fluent-sing-box" />
    <Property Id="INSTALLFOLDER"><RegistrySearch Id="ExistingInstallPath" Root="$taskRegistry" Key="Software\PhotonNomad\FluentSingBox\Install" Name="Path" Type="raw" /></Property>
    <Property Id="PREVIOUSINSTALLFOLDER"><RegistrySearch Id="PreviousInstallPath" Root="$taskRegistry" Key="Software\PhotonNomad\FluentSingBox\Install" Name="Path" Type="raw" /></Property>
    <Property Id="BOOTSTRAPPERINSTALLFOLDER" Secure="yes" />
    <SetProperty Id="DEFAULTBOOTSTRAPPERINSTALLFOLDER" Value="$taskDefaultFolder" After="CostInitialize" Sequence="execute" />
    <SetProperty Id="INSTALLFOLDER" Value="[BOOTSTRAPPERINSTALLFOLDER]" After="SetDEFAULTBOOTSTRAPPERINSTALLFOLDER" Sequence="execute" Condition="BOOTSTRAPPERINSTALLFOLDER &lt;&gt; &quot;&quot; AND (PREVIOUSINSTALLFOLDER = &quot;&quot; OR BOOTSTRAPPERINSTALLFOLDER &lt;&gt; DEFAULTBOOTSTRAPPERINSTALLFOLDER)" />
    <Property Id="OTHER_SCOPE"><RegistrySearch Id="OtherScope" Root="$taskOpposite" Key="Software\PhotonNomad\FluentSingBox\Install" Name="Scope" Type="raw" /></Property>
    <Launch Condition="Installed OR NOT OTHER_SCOPE" Message="Uninstall the other installation scope before switching between user and machine installers. Your settings will be kept." />
    <Property Id="WINDOWSBUILD"><RegistrySearch Id="WindowsBuild" Root="HKLM" Key="SOFTWARE\Microsoft\Windows NT\CurrentVersion" Name="CurrentBuildNumber" Type="raw" /></Property>
    <Launch Condition="Installed OR WINDOWSBUILD &gt;= 19041" Message="Windows 10 build 19041 or later is required." />
    <Icon Id="AppIcon" SourceFile="$(Escape-Xml (Join-Path $taskPayload 'Assets/fluent-sing-box.ico'))" />
    <StandardDirectory Id="$taskParent">$taskInstallDirectory</StandardDirectory>
    <StandardDirectory Id="ProgramMenuFolder"><Directory Id="AppMenu" Name="fluent sing-box" /></StandardDirectory>
    <Component Id="InstallRegistration" Directory="INSTALLFOLDER" Guid="*">
      <RegistryValue Root="$taskRegistry" Key="Software\PhotonNomad\FluentSingBox\Install" Name="Scope" Value="$taskScope" Type="string" KeyPath="yes" />
      <RegistryValue Root="$taskRegistry" Key="Software\PhotonNomad\FluentSingBox\Install" Name="Path" Value="[INSTALLFOLDER]" Type="string" />
    </Component>
    <Component Id="StartMenuShortcut" Directory="AppMenu" Guid="*">
      <Shortcut Id="AppShortcut" Name="fluent sing-box" Target="[INSTALLFOLDER]fluent sing-box.exe" WorkingDirectory="INSTALLFOLDER" Icon="AppIcon" />
      <RemoveFolder Id="RemoveAppMenu" On="uninstall" />
      <RegistryValue Root="$taskRegistry" Key="Software\PhotonNomad\FluentSingBox\Install" Name="Shortcut" Value="1" Type="integer" KeyPath="yes" />
    </Component>
    <Feature Id="Application" Title="fluent sing-box" Level="1"><ComponentGroupRef Id="Payload" /><ComponentRef Id="InstallRegistration" /><ComponentRef Id="StartMenuShortcut" /></Feature>
    <ui:WixUI Id="WixUI_InstallDir" InstallDirectory="INSTALLFOLDER" />
    <WixVariable Id="WixUILicenseRtf" Value="$(Escape-Xml (Join-Path $taskRoot 'installer/License.rtf'))" />
    <CustomAction Id="CleanupOwnedStartup" FileRef="$taskMainFileId" ExeCommand="--remove-startup-entry" Execute="immediate" Return="ignore" />
    <InstallExecuteSequence>
      <Custom Action="CleanupOwnedStartup" Before="RemoveFiles" Condition="Installed AND REMOVE=&quot;ALL&quot; AND NOT UPGRADINGPRODUCTCODE" />
      <!-- The util default (before InstallFiles) is too late: InstallValidate's files-in-use check
           and the MajorUpgrade removal of the old version (after InstallInitialize) come first. -->
      <Custom Action="override Wix4CloseApplications_X64" Before="InstallValidate" Condition="VersionNT &gt; 400" />
    </InstallExecuteSequence>
  </Package>
  <Fragment>$taskDirectories</Fragment>
  <Fragment><ComponentGroup Id="Payload">$taskComponents</ComponentGroup></Fragment>
</Wix>
"@
    $taskSource = Join-Path $taskStage 'Product.wxs'
    Set-Content $taskSource $taskWxs -Encoding utf8
    $taskMsi = Join-Path $taskOutput "$taskBaseName.msi"
    Invoke-Wix @('build',$taskSource,'-arch','x64','-ext','WixToolset.UI.wixext','-ext','WixToolset.Util.wixext','-o',$taskMsi)
    Sign-Artifact $taskMsi
    $taskBundleSource = Join-Path $taskStage 'Bundle.wxs'
    $taskBundle = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs" xmlns:bal="http://wixtoolset.org/schemas/v4/wxs/bal">
  <Bundle Name="fluent sing-box" Manufacturer="PhotonNomad" Version="$Version" UpgradeCode="$taskBundleUpgrade" IconSourceFile="$(Escape-Xml (Join-Path $taskPayload 'Assets/fluent-sing-box.ico'))">
    <BootstrapperApplication>
      <bal:WixStandardBootstrapperApplication Theme="rtfLicense" ThemeFile="$(Escape-Xml (Join-Path $taskRoot 'installer/FluentTheme.xml'))" LocalizationFile="$(Escape-Xml (Join-Path $taskRoot 'installer/Strings.en-US.wxl'))" LogoFile="$(Escape-Xml (Join-Path $taskPayload 'Assets/fluent-sing-box.png'))" LicenseFile="$(Escape-Xml (Join-Path $taskRoot 'installer/License.rtf'))" SuppressOptionsUI="no" SuppressRepair="yes" />
      <Payload Name="fluent.ico" SourceFile="$(Escape-Xml (Join-Path $taskPayload 'Assets/fluent-sing-box.ico'))" />
      <Payload Name="1033\thm.wxl" SourceFile="$(Escape-Xml (Join-Path $taskRoot 'installer/Strings.en-US.wxl'))" />
      <Payload Name="2052\thm.wxl" SourceFile="$(Escape-Xml (Join-Path $taskRoot 'installer/Strings.zh-CN.wxl'))" />
    </BootstrapperApplication>
    <Variable Name="InstallFolder" Type="formatted" Value="$taskDefaultFolder" bal:Overridable="yes" />
    <Chain><MsiPackage DisplayName="fluent sing-box" SourceFile="$(Escape-Xml $taskMsi)" Compressed="yes" Visible="no" bal:DisplayInternalUICondition="false"><MsiProperty Name="BOOTSTRAPPERINSTALLFOLDER" Value="[InstallFolder]" /><MsiProperty Name="FSBAPPRUNNINGMESSAGE" Value="$(Escape-Xml $taskAppRunningChinese)" Condition="WixStdBALanguageId = 2052" /></MsiPackage></Chain>
  </Bundle>
</Wix>
"@
    Set-Content $taskBundleSource $taskBundle -Encoding utf8
    $taskExe = Join-Path $taskOutput "$taskBaseName.exe"
    Invoke-Wix @('build',$taskBundleSource,'-arch','x64','-ext','WixToolset.BootstrapperApplications.wixext','-o',$taskExe)
    if ($CertificateThumbprint) {
        $taskEngine = Join-Path $taskStage 'engine.exe'
        Invoke-Wix @('burn','detach',$taskExe,'-engine',$taskEngine)
        Sign-Artifact $taskEngine
        Invoke-Wix @('burn','reattach',$taskExe,'-engine',$taskEngine,'-o',$taskExe)
        Sign-Artifact $taskExe
    }
    Write-Output "Built $taskExe and $taskMsi"
    # Retain generated source for diagnostics without retaining duplicate application payloads.
    $taskEvidence = Join-Path $taskOutput "build-sources/$taskScope"
    New-Item -ItemType Directory -Path $taskEvidence -Force | Out-Null
    Copy-Item -LiteralPath $taskSource,$taskBundleSource -Destination $taskEvidence -Force
    $taskResolvedStage = [IO.Path]::GetFullPath($taskStage)
    if (!$taskResolvedStage.StartsWith([IO.Path]::GetFullPath($taskOutput) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or (Get-Item $taskResolvedStage).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe staging cleanup target.' }
    Remove-Item -LiteralPath $taskResolvedStage -Recurse -Force
}
Get-ChildItem -LiteralPath $taskOutput -File | Where-Object Extension -in @('.msi','.exe') | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content (Join-Path $taskOutput 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Installer output: $taskOutput"
