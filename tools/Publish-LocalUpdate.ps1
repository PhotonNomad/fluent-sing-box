[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$FeedDirectory,
    [Parameter(Mandatory)][string]$SigningKeyPath,
    [ValidateSet('user','machine')][string]$Scope='user'
)
$ErrorActionPreference='Stop'
if($Version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid stable version.'}
$taskRoot=(Resolve-Path "$PSScriptRoot/..").Path
$taskFeed=[IO.Path]::GetFullPath($FeedDirectory)
if(![IO.Path]::IsPathFullyQualified($FeedDirectory)){throw 'Feed path must be absolute.'}
New-Item -ItemType Directory -Path $taskFeed -Force | Out-Null
$taskKey=[IO.Path]::GetFullPath($SigningKeyPath)
$taskPublicKey=[IO.Path]::ChangeExtension($taskKey,'.public.pem')
$taskRsa=[Security.Cryptography.RSA]::Create()
try {
    if(!(Test-Path -LiteralPath $taskKey)){
        if(Test-Path -LiteralPath (Join-Path $taskFeed 'latest.json')){throw 'Existing update feed has no signing private key. Recover the original key; do not rotate it silently.'}
        $taskRsa.KeySize=3072
        New-Item -ItemType Directory -Path (Split-Path $taskKey) -Force | Out-Null
        [IO.File]::WriteAllText($taskKey,$taskRsa.ExportPkcs8PrivateKeyPem(),[Text.UTF8Encoding]::new($false))
    } else { $taskRsa.ImportFromPem((Get-Content -LiteralPath $taskKey -Raw)) }
    if($taskRsa.KeySize -lt 3072){throw 'Update key must be at least 3072 bits.'}
    [IO.File]::WriteAllText($taskPublicKey,$taskRsa.ExportSubjectPublicKeyInfoPem(),[Text.UTF8Encoding]::new($false))
    $taskAsset=if($Scope -eq 'user'){"FluentSingBoxUserSetup-$Version-x64.exe"}else{"FluentSingBoxSetup-$Version-x64.exe"}
    $taskSource=Join-Path $taskRoot "artifacts/installers/$Version/$taskAsset"
    if(!(Test-Path -LiteralPath $taskSource)){throw 'Build the installer before publishing the signed feed.'}
    $taskBundleSource=Join-Path $taskRoot "artifacts/installers/$Version/build-sources/$Scope/Bundle.wxs"
    if(!(Test-Path -LiteralPath $taskBundleSource)){throw 'Missing installer build provenance.'}
    $taskDestination=Join-Path $taskFeed $taskAsset
    if((Test-Path -LiteralPath $taskDestination) -and (Get-FileHash -LiteralPath $taskDestination).Hash -ne (Get-FileHash -LiteralPath $taskSource).Hash){throw 'Immutable version already exists with different bytes. Increment the version.'}
    Copy-Item -LiteralPath $taskSource -Destination $taskDestination -Force
    $taskRelease=[ordered]@{Version=$Version;Scope=$Scope;AssetName=$taskAsset;Url=([uri]$taskDestination).AbsoluteUri;Sha256=(Get-FileHash -LiteralPath $taskDestination -Algorithm SHA256).Hash;Size=(Get-Item -LiteralPath $taskDestination).Length;ReleaseUrl='';Notes='Local build update; not a published GitHub release.'}
    $taskPayload=[Text.Encoding]::UTF8.GetBytes((@{SchemaVersion=1;Releases=@($taskRelease)} | ConvertTo-Json -Depth 8 -Compress))
    $taskSignature=$taskRsa.SignData($taskPayload,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $taskEnvelope=@{Payload=[Convert]::ToBase64String($taskPayload);Signature=[Convert]::ToBase64String($taskSignature)} | ConvertTo-Json
    $taskTemporary=Join-Path $taskFeed ('.manifest-'+[guid]::NewGuid().ToString('N')+'.tmp')
    [IO.File]::WriteAllText($taskTemporary,$taskEnvelope,[Text.UTF8Encoding]::new($false))
    [IO.File]::Move($taskTemporary,(Join-Path $taskFeed 'latest.json'),$true)
    Write-Output "Published authenticated local update $Version ($Scope). Public key: $taskPublicKey"
} finally { $taskRsa.Dispose() }
