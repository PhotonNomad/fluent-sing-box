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
    @{LanguageChoice='en-US';SingBoxExecutablePath=(Join-Path $ProjectRoot 'artifacts/server-editor/core/sing-box.exe');Servers=@(@{Id='lang-node';Name='Schema fixture';OutboundJson='{"type":"socks","server":"127.0.0.1","server_port":1}'});DefaultServerId='lang-node';SystemProxyEnabled=$false;Applications=@()} | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $directory 'settings.json')
    $script:sourcePath=Join-Path $directory 'sing-box/user-config.json'
    Start-Fixture $directory
    Case 'route/only-rules-final-ruleset-and-preserve-hidden-options' {
        $d=Get-Content $script:sourcePath -Raw | ConvertFrom-Json
        $d.route | Add-Member -NotePropertyName auto_detect_interface -NotePropertyValue $true -Force
        $d | ConvertTo-Json -Depth 100 | Set-Content $script:sourcePath
        Ui @('invoke','Navigation_Routing') | Out-Null; Ui @('wait-for','Routing_Save') | Out-Null
        $nodes=Tree
        Assert (@($nodes | Where-Object {$_.automationId -match '^Routing_(Inbounds|Outbounds|AddTunPreset|auto_detect_interface)'}).Count -eq 0) 'Removed routing fields remain'
        foreach($id in @('Routing_Rules_Add','Routing_final','Routing_RuleSets_Add')) { Assert (@($nodes | Where-Object automationId -eq $id).Count -eq 1) ('Missing '+$id) }
        Ui @('invoke','Routing_Save') | Out-Null; Ui @('wait-for','Routing_Save','--property','IsEnabled','--value','true') | Out-Null
        $d=Get-Content $script:sourcePath -Raw | ConvertFrom-Json
        Assert ($d.route.auto_detect_interface -eq $true) 'Hidden route setting removed'
        Assert (@($d.inbounds).Count -eq 1 -and @($d.outbounds).Count -gt 1) 'Inbound/outbound modified'
    }
    Case 'route/ruleset-entry-add-edit-save' {
        Ui @('invoke','Routing_RuleSets_Add') | Out-Null; Ui @('wait-for','Routing_RuleSets_Editor_tag') | Out-Null
        Ui @('set-value','Routing_RuleSets_Editor_tag','fixture-set') | Out-Null
        Ui @('set-value','Routing_RuleSets_Editor_rules','[{"domain_suffix":["example.com"]}]') | Out-Null
        Save-Dialog; Ui @('wait-for','Routing_RuleSets_0') | Out-Null; Ui @('invoke','Routing_Save') | Out-Null
        Wait-Document {param($d) @($d.route.rule_set | Where-Object tag -eq 'fixture-set').Count -eq 1} | Out-Null
        Ui @('wait-for','Routing_Save','--property','IsEnabled','--value','true') | Out-Null
        $d=Get-Content $script:sourcePath -Raw | ConvertFrom-Json
        $index=@($d.route.rule_set).Count-1
        Expand-Group ('Routing_RuleSets_'+$index); Ui @('invoke',('Routing_RuleSets_'+$index+'_Edit')) | Out-Null
        Ui @('wait-for','Routing_RuleSets_Editor_tag') | Out-Null
        Assert ((Ui @('get-value','Routing_RuleSets_Editor_tag')).text -eq 'fixture-set') 'Ruleset edit lost tag'
        Save-Dialog
        Ui @('screenshot','-o',(Join-Path $taskRoot 'Routing.png')) | Out-Null
    }
    Case 'proxy/tun-expanded-fields-check-save-invalid-protection' {
        Ui @('invoke','Navigation_Proxy') | Out-Null; Ui @('wait-for','Proxy_AddTunPreset') | Out-Null
        Ui @('invoke','Proxy_AddTunPreset') | Out-Null; Ui @('wait-for','Proxy_Tun_0_mtu') | Out-Null
        foreach($field in @('interface_name','address','mtu','stack','auto_route','strict_route','dns_mode','dns_address','route_address','route_exclude_address','route_address_set','route_exclude_address_set','udp_timeout','udp_mapping','udp_filtering','udp_nat_max')) {
            Assert (@(Tree | Where-Object automationId -eq ('Proxy_Tun_0_'+$field)).Count -eq 1) ('Missing TUN '+$field)
        }
        Ui @('set-value','Proxy_Tun_0_mtu','1400') | Out-Null
        Ui @('set-value','Proxy_Tun_0_route_exclude_address','10.0.0.0/8') | Out-Null
        Ui @('invoke','Proxy_Tun_Save') | Out-Null
        Wait-Document {param($d) @($d.inbounds | Where-Object {$_.type -eq 'tun' -and $_.mtu -eq 1400 -and $_.route_exclude_address -contains '10.0.0.0/8'}).Count -eq 1} | Out-Null
        Ui @('wait-for','Proxy_Tun_Save','--property','IsEnabled','--value','true') | Out-Null
        $hash=(Get-FileHash $script:sourcePath).Hash
        Ui @('set-value','Proxy_Tun_0_address','invalid-cidr') | Out-Null; Ui @('invoke','Proxy_Tun_Save') | Out-Null
        Ui @('wait-for','Proxy_Tun_Save','--property','IsEnabled','--value','true') | Out-Null
        Assert ((Get-FileHash $script:sourcePath).Hash -eq $hash) 'Invalid TUN replaced configuration'
        Ui @('invoke','Proxy_Tun_Reload') | Out-Null; Ui @('wait-for','Proxy_Tun_0_mtu') | Out-Null
        Assert ((Ui @('get-value','Proxy_Tun_0_mtu')).text -eq '1400') 'TUN reload lost saved values'
        Assert (!(Test-Path (Join-Path $directory 'sing-box/process.json'))) 'Editing started core'
        Assert (!(Test-Path (Join-Path $directory 'client-errors.log'))) 'Unhandled client exception'
        Ui @('screenshot','-o',(Join-Path $taskRoot 'Proxy-Tun.png')) | Out-Null
    }
}
catch { if($taskWindow){ Tree | ConvertTo-Json -Depth 100 | Set-Content (Join-Path $taskRoot 'failure-tree.json') }; $taskResults.Add([pscustomobject]@{name='route-tun-ui';status='failed';message=$_.Exception.Message}); throw }
finally {
    Stop-Fixture; $env:FLUENTSINGBOX_DATA_DIR=$taskOldData; $env:FLUENTSINGBOX_TEST_BACKGROUND=$taskOldBackground
    $taskResults | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $taskRoot 'results.json'); Write-Output "Route/TUN UI evidence: $taskRoot"
}
