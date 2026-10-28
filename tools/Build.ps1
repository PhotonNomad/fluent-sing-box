[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/..").Path,
    [switch]$SelfContained,
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$taskProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$taskOutputRoot = [IO.Path]::GetFullPath((Join-Path $taskProjectRoot 'artifacts/publish'))
foreach ($taskAncestor in @($taskProjectRoot, (Join-Path $taskProjectRoot 'artifacts'), $taskOutputRoot)) {
    if ((Test-Path -LiteralPath $taskAncestor) -and ((Get-Item -LiteralPath $taskAncestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Output ancestor is a junction or symlink: $taskAncestor"
    }
}
New-Item -ItemType Directory -Path $taskOutputRoot -Force | Out-Null
function Assert-OutputPath([string]$Path) {
    $taskResolved = [IO.Path]::GetFullPath($Path)
    if (!$taskResolved.StartsWith($taskOutputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe output path: $taskResolved" }
    if (Test-Path -LiteralPath $taskResolved) {
        $taskItems = @((Get-Item -LiteralPath $taskResolved)) + @(Get-ChildItem -LiteralPath $taskResolved -Recurse -Force)
        if ($taskItems | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw "Output target contains a junction or symlink: $taskResolved" }
    }
}
$taskCurrent = Join-Path $taskOutputRoot $Configuration
$taskPrevious = Join-Path $taskOutputRoot ".previous-$Configuration"
$taskStaging = Join-Path $taskOutputRoot ('.staging-' + $Configuration + '-' + [guid]::NewGuid().ToString('N'))
foreach ($taskPath in @($taskCurrent, $taskPrevious, $taskStaging)) { Assert-OutputPath $taskPath }
try {
    $taskCurrentExe = Join-Path $taskCurrent 'fluent sing-box.exe'
    if (Test-Path -LiteralPath $taskCurrentExe) {
        try {
            $taskProbe = [IO.File]::Open($taskCurrentExe, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
            $taskProbe.Dispose()
        }
        catch { throw "Close the running $Configuration copy before rebuilding. $($_.Exception.Message)" }
    }
    if (!$Version) { $Version = (Get-Content (Join-Path $taskProjectRoot 'release.json') -Raw | ConvertFrom-Json).version }
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected MAJOR.MINOR.PATCH version.' }
    & dotnet publish (Join-Path $taskProjectRoot 'src/FluentSingBox.Client/fluent sing-box.csproj') -c $Configuration -p:Platform=x64 "-p:Version=$Version" --self-contained $SelfContained.IsPresent.ToString().ToLowerInvariant() -o $taskStaging --nologo
    if ($LASTEXITCODE -ne 0) { throw "$Configuration publish failed; the existing output was kept." }
    $taskReleaseSettings = Get-Content (Join-Path $taskProjectRoot 'release.json') -Raw | ConvertFrom-Json
    $taskReleaseSettings.version = $Version
    $taskReleaseSettings | ConvertTo-Json | Set-Content (Join-Path $taskStaging 'release.json') -Encoding utf8
    if (!(Test-Path -LiteralPath (Join-Path $taskStaging 'fluent sing-box.exe'))) { throw 'Publish did not produce the executable.' }
    foreach ($taskRequired in @(
        'Schemas/profile.schema.json',
        'ThirdParty/Schemas/sing-box-1.14.2.schema.json', 'ThirdParty/RuleSets/manifest.json',
        'ThirdParty/RuleSets/geosite-geolocation-cn.srs',
        'ThirdParty/RuleSets/geosite-geolocation-!cn.srs', 'ThirdParty/RuleSets/geoip-cn.srs'
    )) {
        if (!(Test-Path -LiteralPath (Join-Path $taskStaging $taskRequired))) { throw "Publish is missing a required configuration file: $taskRequired" }
    }
    foreach ($taskPath in @($taskCurrent, $taskPrevious, $taskStaging)) { Assert-OutputPath $taskPath }
    if (Test-Path -LiteralPath $taskPrevious) { Remove-Item -LiteralPath $taskPrevious -Recurse -Force }
    if (Test-Path -LiteralPath $taskCurrent) { Move-Item -LiteralPath $taskCurrent -Destination $taskPrevious }
    try { Move-Item -LiteralPath $taskStaging -Destination $taskCurrent }
    catch {
        if (Test-Path -LiteralPath $taskPrevious) { Assert-OutputPath $taskPrevious; Move-Item -LiteralPath $taskPrevious -Destination $taskCurrent }
        throw
    }
    if (Test-Path -LiteralPath $taskPrevious) { Assert-OutputPath $taskPrevious; Remove-Item -LiteralPath $taskPrevious -Recurse -Force }
    $taskFiles = @(Get-ChildItem -LiteralPath $taskCurrent -File -Recurse)
    [pscustomobject]@{
        Configuration = $Configuration
        Directory = $taskCurrent
        Executable = (Join-Path $taskCurrent 'fluent sing-box.exe')
        Files = $taskFiles.Count
        MiB = [math]::Round(($taskFiles | Measure-Object Length -Sum).Sum / 1MB, 1)
    } | Format-List
}
finally {
    Assert-OutputPath $taskStaging
    if (Test-Path -LiteralPath $taskStaging) { Remove-Item -LiteralPath $taskStaging -Recurse -Force }
}
