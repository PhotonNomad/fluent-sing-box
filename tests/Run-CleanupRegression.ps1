$ErrorActionPreference='Stop'
$taskProject=(Resolve-Path "$PSScriptRoot/..").Path
$taskFixture=Join-Path $taskProject ('artifacts/cleanup-regression/'+[guid]::NewGuid().ToString('N'))
function Write-Fixture([string]$Relative,[string]$Value){
    $taskFile=Join-Path $taskFixture $Relative
    New-Item -ItemType Directory -Path (Split-Path $taskFile) -Force|Out-Null
    [IO.File]::WriteAllText($taskFile,$Value)
}
function Assert([bool]$Condition,[string]$Message){if(!$Condition){throw $Message}}
Write-Fixture 'release.json' '{"version":"1.0.1"}'
foreach($taskPath in @('src/App/bin/test.dll','src/App/obj/cache.json','artifacts/publish/Debug/app.exe','artifacts/publish/Release/app.exe','artifacts/publish/Release-Review/app.exe','artifacts/installers/1.0.0/old.msi','artifacts/installers/1.0.1/current.msi','artifacts/tools/wix.exe','output/DailyUse/data/settings.json','artifacts/vscode-data/settings.json','artifacts/ui/data/settings.json','artifacts/local-update-feed/FluentSingBoxUserSetup-1.0.0-x64.exe','artifacts/local-update-feed/FluentSingBoxUserSetup-1.0.1-x64.exe')){Write-Fixture $taskPath 'fixture'}
$taskRsa=[Security.Cryptography.RSA]::Create(3072)
try{
    Write-Fixture 'artifacts/update-signing/local-update.public.pem' ($taskRsa.ExportSubjectPublicKeyInfoPem())
    Write-Fixture 'artifacts/update-signing/local-update.pem' ($taskRsa.ExportPkcs8PrivateKeyPem())
    $taskAsset=Join-Path $taskFixture 'artifacts/local-update-feed/FluentSingBoxUserSetup-1.0.1-x64.exe'
    $taskPayload=[Text.Encoding]::UTF8.GetBytes((@{SchemaVersion=1;Releases=@(@{AssetName='FluentSingBoxUserSetup-1.0.1-x64.exe';Size=(Get-Item $taskAsset).Length;Sha256=(Get-FileHash $taskAsset).Hash})}|ConvertTo-Json -Depth 6 -Compress))
    $taskEnvelope=@{Payload=[Convert]::ToBase64String($taskPayload);Signature=[Convert]::ToBase64String($taskRsa.SignData($taskPayload,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1))}
    Write-Fixture 'artifacts/local-update-feed/latest.json' ($taskEnvelope|ConvertTo-Json)
    & (Join-Path $taskProject 'tools/Clean-DevelopmentArtifacts.ps1') -ProjectRoot $taskFixture|Out-Null
    Assert (Test-Path (Join-Path $taskFixture 'src/App/bin/test.dll')) 'Preview deleted a file'
    & (Join-Path $taskProject 'tools/Clean-DevelopmentArtifacts.ps1') -ProjectRoot $taskFixture -Apply|Out-Null
    foreach($taskKeep in @('artifacts/publish/Debug/app.exe','artifacts/publish/Release/app.exe','artifacts/installers/1.0.1/current.msi','artifacts/tools/wix.exe','output/DailyUse/data/settings.json','artifacts/vscode-data/settings.json','artifacts/update-signing/local-update.pem','artifacts/local-update-feed/latest.json','artifacts/local-update-feed/FluentSingBoxUserSetup-1.0.1-x64.exe')){Assert (Test-Path (Join-Path $taskFixture $taskKeep)) "Removed protected file: $taskKeep"}
    foreach($taskDrop in @('src/App/bin','src/App/obj','artifacts/ui','artifacts/publish/Release-Review','artifacts/installers/1.0.0','artifacts/local-update-feed/FluentSingBoxUserSetup-1.0.0-x64.exe')){Assert (!(Test-Path (Join-Path $taskFixture $taskDrop))) "Retained generated file: $taskDrop"}
    $taskArchive=@(Get-ChildItem (Join-Path $taskFixture 'output/PreservedDevelopmentData') -Directory)[0]
    Assert ((Get-Content (Join-Path $taskArchive.FullName 'artifacts/ui/data/settings.json') -Raw) -eq 'fixture') 'Configuration was not archived'
    Assert (!@(Get-ChildItem $taskArchive.FullName -Recurse -File -Filter '*.msi').Count) 'MSI was copied into configuration archive'
    Write-Fixture 'artifacts/local-update-feed/FluentSingBoxUserSetup-1.0.0-x64.exe' 'fixture'
    $taskEnvelope.Signature=[Convert]::ToBase64String([byte[]]::new(384))
    Write-Fixture 'artifacts/local-update-feed/latest.json' ($taskEnvelope|ConvertTo-Json)
    $taskRejected=$false
    try{& (Join-Path $taskProject 'tools/Clean-DevelopmentArtifacts.ps1') -ProjectRoot $taskFixture -Apply|Out-Null}catch{$taskRejected=$true}
    Assert $taskRejected 'Invalid signature was accepted'
    Assert (Test-Path (Join-Path $taskFixture 'artifacts/local-update-feed/FluentSingBoxUserSetup-1.0.0-x64.exe')) 'Failed signature check still deleted feed assets'
    $taskEnvelope.Signature=[Convert]::ToBase64String($taskRsa.SignData($taskPayload,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1))
    Write-Fixture 'artifacts/local-update-feed/latest.json' ($taskEnvelope|ConvertTo-Json)
    Write-Fixture 'output/PreservedDevelopmentData/legacy/artifacts/updater-theme-test/extracted/File/FIgenerated' 'regenerable payload'
    & (Join-Path $taskProject 'tools/Clean-DevelopmentArtifacts.ps1') -ProjectRoot $taskFixture -Minimal|Out-Null
    Assert (Test-Path (Join-Path $taskFixture 'artifacts/publish/Debug/app.exe')) 'Minimal preview deleted a file'
    & (Join-Path $taskProject 'tools/Clean-DevelopmentArtifacts.ps1') -ProjectRoot $taskFixture -Minimal -Apply|Out-Null
    foreach($taskDrop in @('artifacts/publish','artifacts/installers','artifacts/tools','artifacts/vscode-data','output')){Assert (!(Test-Path (Join-Path $taskFixture $taskDrop))) "Minimal mode retained $taskDrop"}
    foreach($taskKeep in @('artifacts/update-signing/local-update.pem','artifacts/local-update-feed/latest.json','artifacts/local-update-feed/FluentSingBoxUserSetup-1.0.1-x64.exe')){Assert (Test-Path (Join-Path $taskFixture $taskKeep)) "Minimal mode removed $taskKeep"}
    $taskBackup=@(Get-ChildItem (Join-Path $taskFixture 'artifacts/preserved-data') -File -Filter '*.zip')[0]
    $taskZip=[IO.Compression.ZipFile]::OpenRead($taskBackup.FullName)
    try{
        Assert (@($taskZip.Entries|Where-Object FullName -like '*artifacts/ui/data/settings.json').Count -gt 0) 'Compressed backup lost archived configuration'
        Assert (@($taskZip.Entries|Where-Object FullName -like '*output/DailyUse/data/settings.json').Count -gt 0) 'Compressed backup lost portable configuration'
        Assert (@($taskZip.Entries|Where-Object FullName -like '*extracted/File/FIgenerated').Count -eq 0) 'Compressed backup retained generated native payload'
    }finally{$taskZip.Dispose()}
    Write-Output 'PASS normal/minimal cleanup previews, protected update feed, verified compressed configuration backup, generated payload exclusion and invalid-signature rejection'
}finally{$taskRsa.Dispose()}
