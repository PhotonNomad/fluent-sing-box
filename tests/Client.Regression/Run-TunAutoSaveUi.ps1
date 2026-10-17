param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/../..").Path,
    [string]$ExecutablePath = (Join-Path $ProjectRoot 'artifacts/publish/Debug/fluent sing-box.exe')
)
$ErrorActionPreference = 'Stop'
$taskWinapp = (Get-Command winapp -ErrorAction Stop).Source
$taskRoot = Join-Path $ProjectRoot ('artifacts/localization/ui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
$taskOldData = $env:FLUENTSINGBOX_DATA_DIR; $taskOldBackground = $env:FLUENTSINGBOX_TEST_BACKGROUND
$taskResults = [Collections.Generic.List[object]]::new()
$taskProcess = $null; $taskWindow = $null; $script:taskSequence = 0
function Nodes($items) { foreach ($item in $items) { if ($item.type -eq 'ScrollBar' -or ($item.type -eq 'TitleBar' -and $item.automationId -eq 'TitleBar')) { continue }; $item; if ($item.children) { Nodes $item.children } } }
function Ui([string[]]$Arguments) {
    $script:taskSequence++
    $raw = & $taskWinapp ui @Arguments -w "$taskWindow" --json 2>&1 | Out-String
    $exit = $LASTEXITCODE
    $raw | Set-Content -LiteralPath (Join-Path $taskRoot ('command-{0:D3}.json' -f $script:taskSequence))
    $value = $raw | ConvertFrom-Json
    if ($exit -ne 0 -or $value.error -or $value.timedOut) { throw "UI command failed: $($Arguments -join ' '): $raw" }
    return $value
}
function Tree { return @(Nodes (Ui @('inspect','--depth','20')).windows.elements) }
function Expand-Group([string]$Id) {
    $inner = @(Nodes (Ui @('inspect',$Id,'--depth','3')).windows.elements | Where-Object className -eq 'Microsoft.UI.Xaml.Controls.Expander')[0]
    Ui @('invoke',$inner.selector,'--action','expand') | Out-Null
}
function Assert([bool]$Ok,[string]$Message) { if (!$Ok) { throw $Message } }
function Assert-English {
    # Framework scrollbar accessibility names use the Windows display language.
    $bad = @(Tree | Where-Object { $_.type -ne 'ScrollBar' -and $_.className -ne 'Popup' -and $_.name -match '\p{IsCJKUnifiedIdeographs}' })
    Assert ($bad.Count -eq 0) ('Chinese text in English UI: ' + ($bad.name -join ', '))
}
function Start-Fixture([string]$Directory) {
    $env:FLUENTSINGBOX_DATA_DIR = $Directory
    $env:FLUENTSINGBOX_TEST_BACKGROUND = '1'
    $script:taskProcess = Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path $ExecutablePath) -WindowStyle Hidden -PassThru
    $raw = & $taskWinapp ui wait-for Navigation_Servers -a "$($taskProcess.Id)" --timeout 15000 --json 2>&1 | Out-String
    $ready = $raw | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $ready.error -or $ready.timedOut) { throw "Localization fixture did not start: $raw" }
    $windows = @(& $taskWinapp ui list-windows -a "$($taskProcess.Id)" --json | Out-String | ConvertFrom-Json)
    Assert ($windows.Count -eq 1) 'Expected one isolated window'
    $script:taskWindow = $windows[0].hwnd
    Ui @('wait-for','Core_Toggle','--property','IsEnabled','--value','true') | Out-Null
}
function Stop-Fixture {
    if ($script:taskProcess) {
        if (!$script:taskProcess.HasExited) { Stop-Process -Id $script:taskProcess.Id; $script:taskProcess.WaitForExit(5000) | Out-Null }
        $script:taskProcess.Dispose(); $script:taskProcess = $null
    }
}
function Choice([string]$Id,[string]$Value) {
    Ui @('invoke',$Id,'--action','expand') | Out-Null
    $item = @(Tree | Where-Object { $_.automationId -eq ($Id + '_' + $Value) -and $_.type -eq 'ListItem' })[0]
    Assert ($null -ne $item) ('Missing choice ' + $Id + '_' + $Value)
    Ui @('invoke',$item.selector,'--action','select') | Out-Null
}
function Save-Dialog {
    $button=@(Tree | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Save' })[0]
    Ui @('invoke',$button.selector) | Out-Null
}
function Wait-Document([scriptblock]$Condition) {
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    do {
        try { $document=Get-Content -LiteralPath $script:sourcePath -Raw | ConvertFrom-Json; if (& $Condition $document) { return $document } } catch [IO.IOException] {} catch [System.Management.Automation.ItemNotFoundException] {}
        Start-Sleep -Milliseconds 100
    } while([DateTime]::UtcNow -lt $deadline)
    throw 'Configuration save did not complete'
}
function Case([string]$Name,[scriptblock]$Work) { & $Work; $taskResults.Add([pscustomobject]@{name=$Name;status='passed'}); Write-Output "PASS $Name" }


try {
    $directory=Join-Path $taskRoot 'data'; New-Item -ItemType Directory -Path $directory | Out-Null
    @{Configuration=@{DnsStrategy='prefer_ipv4'};LanguageChoice='en-US';SingBoxExecutablePath=(Join-Path $ProjectRoot 'artifacts/server-editor/core/sing-box.exe');Servers=@(@{Id='lang-node';Name='Schema fixture';OutboundJson='{"type":"socks","server":"127.0.0.1","server_port":1}'});DefaultServerId='lang-node';SystemProxyEnabled=$false;Applications=@()} | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $directory 'settings.json')
    $script:sourcePath=Join-Path $directory 'sing-box/user-config.json'
    Start-Fixture $directory
    Case 'tun/top-level-minimal-fields' {
        Ui @('invoke','Navigation_Proxy') | Out-Null; Ui @('wait-for','Proxy_Tun_0_mtu') | Out-Null
        $nodes=Tree
        foreach($field in @('interface_name','address','mtu','dns_mode','dns_address','auto_route','strict_route')) { Assert (@($nodes | Where-Object automationId -eq ('Proxy_Tun_0_'+$field)).Count -eq 1) ('Missing '+$field) }
        Assert (@($nodes | Where-Object {$_.automationId -match '^Proxy_(GlobalTun|AddTunPreset|Tun_Save|Tun_Reload|Tun_0_(type|tag|stack|route_|udp_|loopback))'}).Count -eq 0) 'Removed controls remain'
        Assert (@($nodes | Where-Object automationId -eq 'Proxy_Tun_Enabled').Count -eq 1) 'Enable switch missing'
        Assert (@($nodes | Where-Object name -eq 'TUN').Count -ge 1) 'Uppercase TUN title missing'
    }
    Case 'tun/enable-save-and-invalid-protection' {
        Ui @('invoke','Proxy_Tun_Enabled','--action','toggle') | Out-Null
        Ui @('set-value','Proxy_Tun_0_mtu','1400') | Out-Null

        Wait-Document {param($d) @($d.inbounds | Where-Object {$_.type -eq 'tun' -and $_.mtu -eq 1400}).Count -eq 1} | Out-Null
        $hash=(Get-FileHash $script:sourcePath).Hash
        Ui @('set-value','Proxy_Tun_0_address','bad-cidr') | Out-Null
        $deadline=[DateTime]::UtcNow.AddSeconds(10); $errorSeen=$false
        do { $errorSeen=@(Tree | Where-Object {$_.name -match 'bad-cidr'}).Count -gt 0; if(!$errorSeen){Start-Sleep -Milliseconds 150} } while(!$errorSeen -and [DateTime]::UtcNow -lt $deadline)
        Assert $errorSeen 'Core validation error was not displayed after automatic save'
        Assert ((Get-FileHash $script:sourcePath).Hash -eq $hash) 'Invalid CIDR overwrote configuration'
        Assert ((Ui @('get-value','Proxy_Tun_0_address')).text -notmatch 'bad-cidr') 'Invalid CIDR did not revert in UI'
        $runtime = Get-Content (Join-Path $directory 'sing-box/config.json') -Raw | ConvertFrom-Json
        Assert (@($runtime.inbounds | Where-Object {$_.type -eq 'tun' -and $_.mtu -eq 1400}).Count -eq 1) 'Valid TUN edit missing from next-start config'
        Ui @('wait-for','Proxy_Tun_Status') | Out-Null
        Ui @('set-value','Proxy_Tun_0_address',"172.19.0.1/30`nfdfe:dcba:9876::1/126") | Out-Null
    }
    Case 'tun/ipv6-address-removal-has-no-proxy-hint-and-preserves-dns' {
        Ui @('set-value','Proxy_Tun_0_address','172.19.0.1/30') | Out-Null
        Wait-Document {param($d) @($d.inbounds | Where-Object {$_.type -eq 'tun' -and @($_.address).Count -eq 1}).Count -eq 1} | Out-Null
        Assert (@(Tree | Where-Object { $_.automationId -like 'Proxy_Tun_IPv6*' -or $_.name -eq 'IPv6 connectivity' }).Count -eq 0) 'Proxy page still contains IPv6 connectivity UI'
        Assert ((Get-Content $script:sourcePath -Raw | ConvertFrom-Json).dns.strategy -eq 'prefer_ipv4') 'TUN hint silently changed DNS strategy'
        Ui @('set-value','Proxy_Tun_0_address',"172.19.0.1/30`nfdfe:dcba:9876::1/126") | Out-Null
        Wait-Document {param($d) @($d.inbounds | Where-Object {$_.type -eq 'tun' -and @($_.address).Count -eq 2}).Count -eq 1} | Out-Null
    }
    Case 'tun/disable-and-restore-settings-without-starting-core' {
        Ui @('invoke','Proxy_Tun_Enabled','--action','toggle') | Out-Null
        Wait-Document {param($d) @($d.inbounds | Where-Object type -eq 'tun').Count -eq 0} | Out-Null
        Ui @('invoke','Navigation_Routing') | Out-Null; Ui @('wait-for','Routing_final') | Out-Null
        Ui @('invoke','Navigation_Proxy') | Out-Null; Ui @('wait-for','Proxy_Tun_0_mtu') | Out-Null
        Assert ((Ui @('get-value','Proxy_Tun_0_mtu')).text -eq '1400') 'Disabled TUN lost parameters'
        Ui @('invoke','Proxy_Tun_Enabled','--action','toggle') | Out-Null
        Wait-Document {param($d) @($d.inbounds | Where-Object {$_.type -eq 'tun' -and $_.mtu -eq 1400}).Count -eq 1} | Out-Null
        Assert (!(Test-Path (Join-Path $directory 'sing-box/process.json'))) 'Editing started core'
        Ui @('scroll','PageScrollViewer','--to','bottom') | Out-Null
        Ui @('screenshot','-o',(Join-Path $taskRoot 'Tun-Simple.png')) | Out-Null
    }
}
catch { $taskResults.Add([pscustomobject]@{name='tun-simple-ui';status='failed';message=$_.Exception.Message}); throw }
finally {
    Stop-Fixture; $env:FLUENTSINGBOX_DATA_DIR=$taskOldData; $env:FLUENTSINGBOX_TEST_BACKGROUND=$taskOldBackground
    $taskResults | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $taskRoot 'results.json'); Write-Output "TUN simple UI evidence: $taskRoot"
}
