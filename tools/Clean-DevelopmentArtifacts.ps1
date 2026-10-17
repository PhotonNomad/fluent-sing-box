[CmdletBinding()]
param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/..").Path,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$taskArchive = Join-Path $taskRoot ('output/PreservedDevelopmentData/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$taskTargets = [Collections.Generic.List[string]]::new()
$taskVersion = (Get-Content -LiteralPath (Join-Path $taskRoot 'release.json') -Raw | ConvertFrom-Json).version
if ($taskVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid current release version.' }

function Assert-WorkspacePath([string]$Path) {
    $taskFull = [IO.Path]::GetFullPath($Path)
    if (!$taskFull.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the workspace: $taskFull"
    }
    $taskCursor = $taskFull
    while ($taskCursor) {
        if ((Test-Path -LiteralPath $taskCursor) -and ((Get-Item -LiteralPath $taskCursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing a junction or symbolic link: $taskCursor"
        }
        if ($taskCursor.Equals($taskRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
        $taskCursor = [IO.Path]::GetDirectoryName($taskCursor)
    }
    return $taskFull
}

foreach ($taskDirectory in @('src', 'tests', 'output/DefaultConfigTool')) {
    $taskPath = Join-Path $taskRoot $taskDirectory
    if (!(Test-Path -LiteralPath $taskPath)) { continue }
    foreach ($taskCache in Get-ChildItem -LiteralPath $taskPath -Directory -Recurse -Force | Where-Object Name -in @('bin', 'obj')) {
        if (!$taskTargets.Exists([Predicate[string]]{ param($existing) $taskCache.FullName.StartsWith($existing + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) })) {
            $taskTargets.Add($taskCache.FullName)
        }
    }
}
if (Test-Path -LiteralPath (Join-Path $taskRoot '.vs')) { $taskTargets.Add((Join-Path $taskRoot '.vs')) }
if (Test-Path -LiteralPath (Join-Path $taskRoot 'artifacts')) {
    foreach ($taskItem in Get-ChildItem -LiteralPath (Join-Path $taskRoot 'artifacts') -Force) {
        # The installed local update channel depends on these persistent files.
        # In particular, deleting/rotating its private key would strand installed clients.
        if ($taskItem.Name -notin @('publish', 'vscode-data', 'local-update-feed', 'update-signing', 'tools', 'installers')) { $taskTargets.Add($taskItem.FullName) }
    }
}
foreach ($taskContainer in @('artifacts/publish', 'artifacts/installers')) {
    $taskPath = Join-Path $taskRoot $taskContainer
    if (!(Test-Path -LiteralPath $taskPath)) { continue }
    foreach ($taskItem in Get-ChildItem -LiteralPath $taskPath -Directory -Force) {
        $taskKeep = if ($taskContainer -eq 'artifacts/publish') { $taskItem.Name -in @('Debug', 'Release') } else { $taskItem.Name -eq $taskVersion }
        if (!$taskKeep) { $taskTargets.Add($taskItem.FullName) }
    }
}
$taskFeed = Join-Path $taskRoot 'artifacts/local-update-feed'
$taskManifestPath = Join-Path $taskFeed 'latest.json'
if (Test-Path -LiteralPath $taskManifestPath) {
    $null = Assert-WorkspacePath $taskManifestPath
    $taskEnvelope = Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json
    $taskPayload = [Convert]::FromBase64String($taskEnvelope.Payload)
    $taskPublicKeyPath = Join-Path $taskRoot 'artifacts/update-signing/local-update.public.pem'
    $null = Assert-WorkspacePath $taskPublicKeyPath
    $taskRsa = [Security.Cryptography.RSA]::Create()
    try {
        $taskRsa.ImportFromPem((Get-Content -LiteralPath $taskPublicKeyPath -Raw))
        if (!$taskRsa.VerifyData($taskPayload, [Convert]::FromBase64String($taskEnvelope.Signature), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) { throw 'Refusing to prune an unverified local update feed.' }
    } finally { $taskRsa.Dispose() }
    $taskReleases = ([Text.Encoding]::UTF8.GetString($taskPayload) | ConvertFrom-Json).Releases
    if (!$taskReleases) { throw 'Refusing to prune an empty local update feed.' }
    $taskKeepAssets = @($taskReleases | ForEach-Object {
        if ($_.AssetName -notmatch '^FluentSingBox(User)?Setup-\d+\.\d+\.\d+-x64\.exe$') { throw 'Invalid local update asset name.' }
        if (!(Test-Path -LiteralPath (Join-Path $taskFeed $_.AssetName))) { throw 'The active update asset is missing.' }
        $taskActive = Get-Item -LiteralPath (Join-Path $taskFeed $_.AssetName)
        if ($taskActive.Length -ne $_.Size -or (Get-FileHash -LiteralPath $taskActive.FullName -Algorithm SHA256).Hash -ne $_.Sha256) { throw 'The active update asset does not match the signed manifest.' }
        $_.AssetName
    })
    foreach ($taskItem in Get-ChildItem -LiteralPath $taskFeed -File) {
        if ($taskItem.Name -match '^FluentSingBox(User)?Setup-\d+\.\d+\.\d+-x64\.exe$' -and $taskItem.Name -notin $taskKeepAssets) { $taskTargets.Add($taskItem.FullName) }
    }
}
if (Test-Path -LiteralPath (Join-Path $taskRoot 'output')) {
    foreach ($taskItem in Get-ChildItem -LiteralPath (Join-Path $taskRoot 'output') -Directory -Force) {
        if ($taskItem.Name -like 'DailyUse-Backup-*' -or $taskItem.Name -in @('DailyUse-Update', 'icon-concepts')) { $taskTargets.Add($taskItem.FullName) }
    }
}

$taskManifest = [Collections.Generic.List[object]]::new()
$taskPlan = [Collections.Generic.List[object]]::new()
# Keep configuration, source, scripts, documentation and test result summaries.
# Omit generated build files, logs, screenshots, caches and duplicate executables.
$taskDiscardExtensions = @('.exe', '.dll', '.msi', '.wixpdb', '.cab', '.winmd', '.mui', '.xbf', '.pri', '.pdb', '.png', '.ico', '.zip', '.html', '.log', '.db', '.cache', '.v2', '.sqlite', '.sqlite3')
foreach ($taskTarget in $taskTargets) {
    $null = Assert-WorkspacePath $taskTarget
    $taskItems = @((Get-Item -LiteralPath $taskTarget -Force))
    if ($taskItems[0].PSIsContainer) { $taskItems += @(Get-ChildItem -LiteralPath $taskTarget -Recurse -Force) }
    if ($taskItems | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw "Target contains a junction or symbolic link: $taskTarget" }
    $taskFiles = @($taskItems | Where-Object { !$_.PSIsContainer })
    $taskIsBuildCache = (Split-Path -Leaf $taskTarget) -in @('bin', 'obj')
    $taskPreserved = @($taskFiles | Where-Object {
        (!$taskIsBuildCache -or $_.FullName -match '[\\/]data[\\/]') -and $_.Extension.ToLowerInvariant() -notin $taskDiscardExtensions -and $_.Name -notlike 'command-*.json'
    })
    $taskPlan.Add([pscustomobject]@{
        path = $taskTarget.Substring($taskRoot.Length + 1)
        bytes = [long](($taskFiles | Measure-Object Length -Sum).Sum)
        files = $taskFiles.Count
        preservedFiles = $taskPreserved.Count
    })
    foreach ($taskFile in $taskPreserved) {
        $taskRelative = $taskFile.FullName.Substring($taskRoot.Length + 1)
        $taskDestination = Join-Path $taskArchive $taskRelative
        $taskHash = (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash
        if ($Apply) {
            $null = Assert-WorkspacePath $taskDestination
            New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force | Out-Null
            Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination
            if ((Get-FileHash -LiteralPath $taskDestination -Algorithm SHA256).Hash -ne $taskHash) { throw "Archive verification failed: $taskRelative" }
        }
        $taskManifest.Add([pscustomobject]@{originalPath=$taskRelative;sha256=$taskHash;bytes=$taskFile.Length})
    }
}

if (!$Apply) { $taskPlan | Format-Table -AutoSize; Write-Output 'Preview only. Use -Apply to archive configurations and remove these targets.'; return }
New-Item -ItemType Directory -Path $taskArchive -Force | Out-Null
$taskManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskArchive 'manifest.json') -Encoding utf8
$taskRemoved = [Collections.Generic.List[string]]::new()
$taskFailures = [Collections.Generic.List[object]]::new()
foreach ($taskTarget in $taskTargets) {
    $null = Assert-WorkspacePath $taskTarget
    try {
        Remove-Item -LiteralPath $taskTarget -Recurse -Force -ErrorAction Stop
        $taskRemoved.Add($taskTarget.Substring($taskRoot.Length + 1))
    }
    catch { $taskFailures.Add([pscustomobject]@{path=$taskTarget;message=$_.Exception.Message}) }
}
[pscustomobject]@{archive=$taskArchive;plan=$taskPlan;removed=$taskRemoved;failures=$taskFailures} |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskArchive 'cleanup-report.json') -Encoding utf8
[pscustomobject]@{Archive=$taskArchive;PreservedFiles=$taskManifest.Count;RemovedTargets=$taskRemoved.Count;Failures=$taskFailures.Count} | Format-List
if ($taskFailures.Count) { throw 'Some targets could not be removed. See cleanup-report.json; no existing application was stopped.' }
