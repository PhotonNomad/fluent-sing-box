param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/../..").Path,
    [string]$ExecutablePath = (Join-Path $ProjectRoot 'artifacts/publish/Debug/fluent sing-box.exe'),
    [ValidateSet('Reachable','NoLocalAddress','Unconfirmed','Unsupported')][string]$Ipv6Status = 'Unconfirmed',
    [switch]$Ipv6Only,
    [switch]$NoNodes
)
$ErrorActionPreference = 'Stop'
$taskWinapp = (Get-Command winapp -ErrorAction Stop).Source
$taskRoot = Join-Path $ProjectRoot ('artifacts/config-save-ui/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
$taskSettings = Join-Path $taskRoot 'settings.json'
$taskSource = Join-Path $taskRoot 'sing-box/user-config.json'
$taskRuntime = Join-Path $taskRoot 'sing-box/config.json'
@{LanguageChoice='en-US';SingBoxExecutablePath=(Join-Path $ProjectRoot 'artifacts/server-editor/core/sing-box.exe');Servers=@(@{Id='save-ui';Name='Save fixture';OutboundJson='{"type":"socks","server":"127.0.0.1","server_port":1}'});DefaultServerId='save-ui';SystemProxyPort=10809;SystemProxyEnabled=$false;Applications=@()} | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $taskSettings
if ($NoNodes) {
    @{LanguageChoice='en-US';SingBoxExecutablePath=(Join-Path $ProjectRoot 'artifacts/server-editor/core/sing-box.exe');Servers=@();SystemProxyPort=10809;SystemProxyEnabled=$false;Applications=@()} | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $taskSettings
}
$taskOldData = $env:FLUENTSINGBOX_DATA_DIR
$taskOldBackground = $env:FLUENTSINGBOX_TEST_BACKGROUND
$taskOldIpv6 = $env:FLUENTSINGBOX_TEST_IPV6_STATUS
$taskProcess = $null
$taskWindow = $null
$taskResults = [Collections.Generic.List[object]]::new()
function Assert([bool]$Ok,[string]$Reason) { if (!$Ok) { throw $Reason } }
function Nodes($Items) { foreach ($item in $Items) { $item; if ($item.children) { Nodes $item.children } } }
function Ui([string[]]$Arguments) {
    $raw = & $taskWinapp ui @Arguments -w "$taskWindow" --json 2>&1 | Out-String
    $exit = $LASTEXITCODE
    $value = $raw | ConvertFrom-Json
    if ($exit -ne 0 -or $value.error -or $value.timedOut) { throw "UI failed: $($Arguments -join ' '): $raw" }
    return $value
}
function Tree { @(Nodes (Ui @('inspect','--depth','20')).windows.elements) }
function Choice([string]$Id,[string]$Value) {
    Ui @('invoke',$Id,'--action','expand') | Out-Null
    $item = @(Tree | Where-Object { $_.automationId -eq ($Id + '_' + $Value) -and $_.type -eq 'ListItem' })[0]
    Assert ($null -ne $item) "Missing choice: $Id $Value"
    Ui @('invoke',$item.selector,'--action','select') | Out-Null
}
function Wait-Until([scriptblock]$Condition) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do { if (& $Condition) { return }; Start-Sleep -Milliseconds 150 } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Timed out waiting for validated automatic save or rollback'
}
function Case([string]$Name,[scriptblock]$Work) {
    if ($Ipv6Only -and !$Name.StartsWith('ipv6/')) { return }
    if ($NoNodes -and $Name -notin @('configuration-ui/port-auto-save-updates-source-and-runtime','configuration-ui/default-outlet-empty')) { return }
    if (!$NoNodes -and $Name -eq 'configuration-ui/default-outlet-empty') { return }
    & $Work
    $taskResults.Add([pscustomobject]@{name=$Name;status='passed'})
    Write-Output "PASS $Name"
}
try {
    $env:FLUENTSINGBOX_DATA_DIR = $taskRoot
    $env:FLUENTSINGBOX_TEST_BACKGROUND = '1'
    $env:FLUENTSINGBOX_TEST_IPV6_STATUS = $Ipv6Status
    $taskProcess = Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path $ExecutablePath) -WindowStyle Hidden -PassThru
    $ready = & $taskWinapp ui wait-for Navigation_Proxy -a "$($taskProcess.Id)" --timeout 15000 --json | Out-String | ConvertFrom-Json
    Assert (!$ready.error -and !$ready.timedOut) 'Isolated app did not start'
    $windows = @(& $taskWinapp ui list-windows -a "$($taskProcess.Id)" --json | Out-String | ConvertFrom-Json)
    Assert ($windows.Count -eq 1) 'Expected one isolated window'
    $taskWindow = $windows[0].hwnd
    Ui @('invoke','Navigation_Proxy') | Out-Null
    Ui @('wait-for','Proxy_SystemSettings') | Out-Null
    $expander = @(Nodes (Ui @('inspect','Proxy_SystemSettings','--depth','3')).windows.elements | Where-Object className -eq 'Microsoft.UI.Xaml.Controls.Expander')[0]
    Ui @('invoke',$expander.selector,'--action','expand') | Out-Null
    Ui @('wait-for','Proxy_PortTextBox') | Out-Null
    Case 'configuration-ui/port-auto-save-updates-source-and-runtime' {
        Assert (@(Tree | Where-Object automationId -eq 'Proxy_Apply').Count -eq 0) 'Page still requires Apply button'
        Ui @('set-value','Proxy_PortTextBox','18091') | Out-Null
        Wait-Until {
            if (!(Test-Path $taskRuntime)) { return $false }
            $source = Get-Content $taskSource -Raw | ConvertFrom-Json
            $runtime = Get-Content $taskRuntime -Raw | ConvertFrom-Json
            $settings = Get-Content $taskSettings -Raw | ConvertFrom-Json
            return @($source.inbounds | Where-Object { $_.tag -eq 'system-http' -and $_.listen_port -eq 18091 }).Count -eq 1 -and @($runtime.inbounds | Where-Object { $_.tag -eq 'system-http' -and $_.listen_port -eq 18091 }).Count -eq 1 -and $settings.SystemProxyPort -eq 18091
        }
    }
    Case 'configuration-ui/default-outlet-empty' {
        Ui @('invoke','Proxy_DefaultOutlet','--action','expand') | Out-Null
        $item = @(Tree | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq 'Direct (no nodes added)' })[0]
        Assert ($null -ne $item) 'No-node direct outlet label missing'
        Ui @('invoke',$item.selector,'--action','select') | Out-Null
        # The initial direct choice already represents a null default; selecting it again is a no-op.
        Assert ((Get-Content $taskSettings -Raw | ConvertFrom-Json).DefaultServerId -in @($null,'direct')) 'No-node default selected a server'
        foreach ($path in @($taskSource,$taskRuntime)) {
            $config = Get-Content $path -Raw | ConvertFrom-Json
            Assert (($config.outbounds | Where-Object tag -eq 'proxy').default -eq 'direct') 'Empty outlet is not direct'
            Assert ($config.route.final -eq 'proxy' -and $config.dns.final -eq 'local') 'Empty outlet default route or DNS incorrect'
        }
        Assert (!(Get-Content $taskSettings -Raw | ConvertFrom-Json).SystemProxyEnabled) 'Empty outlet test enabled system proxy'
        Assert (!(Test-Path (Join-Path $taskRoot 'sing-box/process.json'))) 'Empty outlet test started a core'
    }
    Case 'configuration-ui/default-outlet-direct-and-node-save' {
        foreach ($choice in @('Direct','Save fixture')) {
            Ui @('invoke','Proxy_DefaultOutlet','--action','expand') | Out-Null
            $item = @(Tree | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq $choice })[0]
            Assert ($null -ne $item) "Missing default outlet choice: $choice"
            Ui @('invoke',$item.selector,'--action','select') | Out-Null
            $expected = if ($choice -eq 'Direct') { 'direct' } else { 'proxy-save-ui' }
            Wait-Until {
                $source = Get-Content $taskSource -Raw | ConvertFrom-Json
                $runtime = Get-Content $taskRuntime -Raw | ConvertFrom-Json
                return ($source.outbounds | Where-Object tag -eq 'proxy').default -eq $expected -and ($runtime.outbounds | Where-Object tag -eq 'proxy').default -eq $expected
            }
            $expectedDns = if ($choice -eq 'Direct') { 'local' } else { 'proxy-dns' }
            Assert ((Get-Content $taskSource -Raw | ConvertFrom-Json).dns.final -eq $expectedDns) 'Default outlet change left stale DNS'
            Assert ((Get-Content $taskSettings -Raw | ConvertFrom-Json).DefaultServerId -eq $(if ($choice -eq 'Direct') { 'direct' } else { 'save-ui' })) 'Default outlet choice was not saved'
        }
    }
    Case 'configuration-ui/invalid-port-restores-ui-and-all-files' {
        $sourceHash = (Get-FileHash $taskSource).Hash
        $runtimeHash = (Get-FileHash $taskRuntime).Hash
        Ui @('set-value','Proxy_PortTextBox','99999') | Out-Null
        Wait-Until { (Ui @('get-value','Proxy_PortTextBox')).text -eq '18091' }
        Assert ((Get-FileHash $taskSource).Hash -eq $sourceHash -and (Get-FileHash $taskRuntime).Hash -eq $runtimeHash) 'Invalid port changed committed files'
        Assert ((Get-Content $taskSettings -Raw | ConvertFrom-Json).SystemProxyPort -eq 18091) 'Invalid port persisted in preferences'
        Assert (@(Tree | Where-Object { $_.name -match 'between 1' }).Count -gt 0) 'Rollback did not display reason'
    }
    Case 'configuration-ui/navigation-flush-without-core-or-proxy-start' {
        Ui @('set-value','Proxy_PortTextBox','18092') | Out-Null
        Ui @('invoke','Navigation_DNS') | Out-Null
        Ui @('wait-for','DNS_final') | Out-Null
        Wait-Until { (Get-Content $taskSettings -Raw | ConvertFrom-Json).SystemProxyPort -eq 18092 }
        Assert (!(Test-Path (Join-Path $taskRoot 'sing-box/process.json'))) 'Configuration edit started a core'
        Assert (!(Test-Path (Join-Path $taskRoot 'sing-box/sing-box.log'))) 'Configuration edit touched runtime log'
        Assert (!(Get-Content $taskSettings -Raw | ConvertFrom-Json).SystemProxyEnabled) 'Configuration edit enabled system proxy'
        Ui @('invoke','Navigation_Proxy') | Out-Null
        Ui @('wait-for','Proxy_Tun_0_mtu') | Out-Null
        $add = @(Tree | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Add application group' })[0]
        Assert ($null -ne $add) 'Add application group button missing'
        Ui @('invoke',$add.selector) | Out-Null
        Ui @('wait-for','ApplicationEditor_Name') | Out-Null
        Assert (@(Tree | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Save' }).Count -eq 1) 'Application dialog Save button was removed'
        Ui @('invoke','CloseButton') | Out-Null
    }
    Case "ipv6/dns-hint-$Ipv6Status-respects-choice-and-recheck" {
        Ui @('invoke','Navigation_DNS') | Out-Null
        Ui @('wait-for','DNS_strategy') | Out-Null
        Ui @('wait-for','DNS_IPv6_Status') | Out-Null
        $expected = switch ($Ipv6Status) {
            'Reachable' { 'IPv6 connections succeeded' }
            'NoLocalAddress' { 'No global IPv6 address' }
            'Unsupported' { 'IPv6 socket stack is unavailable' }
            default { 'IPv6 connectivity could not be confirmed' }
        }
        Wait-Until { @(Tree | Where-Object { $_.name -match [regex]::Escape($expected) }).Count -gt 0 }
        Choice 'DNS_strategy' 'prefer_ipv6'
        Wait-Until { (Get-Content $taskSource -Raw | ConvertFrom-Json).dns.strategy -eq 'prefer_ipv6' -and (Test-Path $taskRuntime) -and (Get-Content $taskRuntime -Raw | ConvertFrom-Json).dns.strategy -eq 'prefer_ipv6' }
        Assert ((Get-Content $taskRuntime -Raw | ConvertFrom-Json).dns.strategy -eq 'prefer_ipv6') 'IPv6 detection overwrote explicit strategy in actual config'
        if ($Ipv6Status -ne 'Reachable') {
            Assert (@(Tree | Where-Object { $_.name -match 'This DNS strategy still allows IPv6' }).Count -gt 0) 'IPv6 failure did not warn for IPv6-enabled DNS'
        }
        Choice 'DNS_strategy' 'ipv4_only'
        Wait-Until { (Get-Content $taskSource -Raw | ConvertFrom-Json).dns.strategy -eq 'ipv4_only' -and (Get-Content $taskRuntime -Raw | ConvertFrom-Json).dns.strategy -eq 'ipv4_only' }
        Assert (@(Tree | Where-Object { $_.name -match 'DNS is set to ipv4_only' }).Count -gt 0) 'IPv4-only selection hint missing'
        $hash = (Get-FileHash $taskSource).Hash
        $runtimeHash = (Get-FileHash $taskRuntime).Hash
        Ui @('invoke','DNS_IPv6_Check') | Out-Null
        Ui @('wait-for','DNS_IPv6_Check','--property','IsEnabled','--value','true') | Out-Null
        Assert ((Get-FileHash $taskSource).Hash -eq $hash -and (Get-FileHash $taskRuntime).Hash -eq $runtimeHash) 'Rechecking IPv6 changed configuration'
        Assert (!(Test-Path (Join-Path $taskRoot 'sing-box/process.json'))) 'IPv6 detection started core'
        Ui @('scroll','PageScrollViewer','--to','bottom') | Out-Null
        Ui @('screenshot','-o',(Join-Path $taskRoot 'ipv6-hint.png')) | Out-Null
    }
}
catch {
    $taskResults.Add([pscustomobject]@{name='configuration-ui';status='failed';message=$_.Exception.Message})
    if ($taskWindow) { try { Tree | ConvertTo-Json -Depth 70 | Set-Content (Join-Path $taskRoot 'failure-tree.json') } catch {} }
    throw
}
finally {
    if ($taskProcess) { if (!$taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id }; $taskProcess.Dispose() }
    $env:FLUENTSINGBOX_DATA_DIR = $taskOldData
    $env:FLUENTSINGBOX_TEST_BACKGROUND = $taskOldBackground
    $env:FLUENTSINGBOX_TEST_IPV6_STATUS = $taskOldIpv6
    $taskResults | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $taskRoot 'results.json')
    Write-Output "Configuration UI evidence: $taskRoot"
}
