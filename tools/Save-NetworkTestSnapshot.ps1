[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$CaseName,
    [Parameter(Mandatory)][ValidateSet('Before','Running','Stopped','Exited','Reopened')][string]$Stage,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/manual-network'),
    [string]$DataDirectory = $(if ($env:FLUENTSINGBOX_DATA_DIR) { $env:FLUENTSINGBOX_DATA_DIR } else { Join-Path $env:LOCALAPPDATA 'fluent sing-box' }),
    [ValidateRange(1,65535)][int]$ProxyPort = 10809,
    [uri]$TestUrl
)
# Read-only Windows inspection. Writes only evidence under OutputDirectory.
# Does not start/stop processes, change proxy settings, or create/remove adapters/routes.
$ErrorActionPreference = 'Stop'
$caseDirectory = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $CaseName
New-Item -ItemType Directory -Path $caseDirectory -Force | Out-Null
$baselinePath = Join-Path $caseDirectory 'snapshot-Before.json'
if ($Stage -eq 'Before' -and (Test-Path -LiteralPath $baselinePath)) { throw 'Baseline already exists. Choose a new CaseName to preserve the original baseline.' }
if ($TestUrl -and ($TestUrl.Scheme -notin @('http','https') -or $TestUrl.UserInfo)) { throw 'TestUrl must be an HTTP/HTTPS URL without embedded credentials.' }
if ($TestUrl) { Add-Type -AssemblyName System.Net.Http }
$captureErrors = [Collections.Generic.List[object]]::new()
function Capture([string]$Name, [scriptblock]$Read) {
    try { @(& $Read) }
    catch { $captureErrors.Add([pscustomobject]@{section=$Name;error=$_.Exception.Message}); @() }
}
function Probe([bool]$UseLocalProxy) {
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $UseLocalProxy
    if ($UseLocalProxy) { $handler.Proxy = [Net.WebProxy]::new("http://127.0.0.1:$ProxyPort") }
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(12)
    $response = $null
    try {
        $response = $client.GetAsync($TestUrl, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        [pscustomobject]@{channel=$(if ($UseLocalProxy) {'explicit-local-http-proxy'} else {'no-http-proxy-from-this-PowerShell-process'});receivedHttpResponse=$true;statusCode=[int]$response.StatusCode;error=$null}
    }
    catch {
        [pscustomobject]@{channel=$(if ($UseLocalProxy) {'explicit-local-http-proxy'} else {'no-http-proxy-from-this-PowerShell-process'});receivedHttpResponse=$false;statusCode=$null;error=$_.Exception.ToString()}
    }
    finally { if ($response) { $response.Dispose() }; $client.Dispose(); $handler.Dispose() }
}
$snapshot = [ordered]@{
    recordedAt = (Get-Date).ToString('o')
    caseName = $CaseName; stage = $Stage; dataDirectory = [IO.Path]::GetFullPath($DataDirectory)
    inspectionProcess = Capture 'inspectionProcess' { Get-Process -Id $PID | Select-Object Id,Path }
    processes = Capture 'processes' {
        Get-CimInstance Win32_Process -Filter "Name = 'sing-box.exe' OR Name = 'fluent sing-box.exe'" |
            Select-Object ProcessId,ParentProcessId,Name,ExecutablePath,CreationDate
    }
    adapters = Capture 'adapters' {
        Get-NetAdapter -IncludeHidden | Select-Object Name,InterfaceDescription,InterfaceGuid,ifIndex,Status,MacAddress
    }
    addresses = Capture 'addresses' {
        Get-NetIPAddress | Select-Object InterfaceAlias,InterfaceIndex,AddressFamily,IPAddress,PrefixLength,AddressState
    }
    activeRoutes = Capture 'activeRoutes' {
        Get-NetRoute -PolicyStore ActiveStore | Select-Object InterfaceAlias,InterfaceIndex,AddressFamily,DestinationPrefix,NextHop,RouteMetric,Protocol
    }
    persistentRoutes = Capture 'persistentRoutes' {
        Get-NetRoute -PolicyStore PersistentStore | Select-Object InterfaceAlias,InterfaceIndex,AddressFamily,DestinationPrefix,NextHop,RouteMetric,Protocol
    }
    dns = Capture 'dns' {
        Get-DnsClientServerAddress | Select-Object InterfaceAlias,InterfaceIndex,AddressFamily,ServerAddresses
    }
    proxy = Capture 'proxy' {
        Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' |
            Select-Object ProxyEnable,ProxyServer,ProxyOverride,AutoConfigURL
    }
    winHttpProxy = Capture 'winHttpProxy' { & netsh winhttp show proxy; if ($LASTEXITCODE -ne 0) { throw 'Unable to read WinHTTP proxy' } }
    listeners = Capture 'listeners' {
        Get-NetTCPConnection -State Listen | Where-Object { $_.LocalPort -eq $ProxyPort } |
            Select-Object LocalAddress,LocalPort,OwningProcess
    }
    udpEndpoints = Capture 'udpEndpoints' {
        Get-NetUDPEndpoint | Where-Object { $_.LocalPort -eq $ProxyPort } | Select-Object LocalAddress,LocalPort,OwningProcess
    }
    lease = Capture 'lease' {
        $path = Join-Path $DataDirectory 'sing-box/process.json'
        if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
    }
    preflight = Capture 'preflight' {
        $path = Join-Path $DataDirectory 'sing-box/startup-preflight.json'
        if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
    }
    probes = @()
}
if ($TestUrl) { $snapshot.probes = @((Probe $false), (Probe $true)) }
$snapshot.captureErrors = @($captureErrors.ToArray())
$json = $snapshot | ConvertTo-Json -Depth 16
$historyPath = Join-Path $caseDirectory ("snapshot-{0}-{1}.json" -f $Stage,(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$json | Set-Content -LiteralPath $historyPath -Encoding UTF8
$json | Set-Content -LiteralPath (Join-Path $caseDirectory "snapshot-$Stage.json") -Encoding UTF8
$lines = [Collections.Generic.List[string]]::new()
$lines.Add("Case: $CaseName / $Stage")
$lines.Add("Snapshot: $historyPath")
$lines.Add("Capture errors: $($captureErrors.Count) (an unavailable section is not a passing result)")
$lines.Add("Local TCP listeners on ${ProxyPort}: $(@($snapshot.listeners).Count)")
$lines.Add("Named TUN observations: $(@($snapshot.adapters | Where-Object Name -eq 'fluent-sing-box').Count)")
if ($Stage -ne 'Before' -and (Test-Path -LiteralPath $baselinePath)) {
    $before = Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
    $diff = [ordered]@{}
    foreach ($section in @('adapters','addresses','activeRoutes','persistentRoutes','dns','proxy','processes','listeners')) {
        $oldKeys = @($before.$section | Where-Object { $null -ne $_ } | ForEach-Object { $_ | ConvertTo-Json -Depth 8 -Compress } | Sort-Object -Unique)
        $newKeys = @($snapshot[$section] | Where-Object { $null -ne $_ } | ForEach-Object { $_ | ConvertTo-Json -Depth 8 -Compress } | Sort-Object -Unique)
        $removed = @($oldKeys | Where-Object { $_ -notin $newKeys })
        $added = @($newKeys | Where-Object { $_ -notin $oldKeys })
        $diff[$section] = @{removed=$removed;added=$added}
        $lines.Add("${section}: removed=$($removed.Count), added=$($added.Count)")
    }
    $diff | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $caseDirectory "diff-$Stage.json") -Encoding UTF8
    $lines.Add('Differences are observations, not proof of ownership or cleanup success; review baseline capture errors and unrelated network changes.')
}
else { $lines.Add('No comparison made: this is the baseline, or no Before snapshot exists.') }
$lines | Set-Content -LiteralPath (Join-Path $caseDirectory "summary-$Stage.txt") -Encoding UTF8
$lines | Write-Output
