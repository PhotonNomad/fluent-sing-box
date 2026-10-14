param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/../..").Path,
    [string]$ExecutablePath = (Join-Path $ProjectRoot 'artifacts/publish/Debug/fluent sing-box.exe'),
    [switch]$DnsOnly
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
    Case 'dns/official-order-and-local-schema' {
        Ui @('invoke','Navigation_DNS') | Out-Null; Ui @('wait-for','DNS_final') | Out-Null
        Assert (@(Tree | Where-Object automationId -eq 'DNS_Save').Count -eq 0) 'Manual DNS save button remains'
        Assert-English
        $nodes=Tree
        foreach($id in @('DNS_final','DNS_strategy')) { Assert (@($nodes | Where-Object automationId -eq $id).Count -eq 1) ('Missing DNS field '+$id) }
        foreach($key in @('disable_cache','disable_expire','cache_capacity','optimistic','timeout','reverse_mapping','client_subnet')) { Assert (@($nodes | Where-Object automationId -eq ('DNS_'+$key)).Count -eq 0) ('Advanced DNS field visible: '+$key) }
        Assert (@($nodes | Where-Object { $_.automationId -in @('DNS_optimistic_Options','DNS_optimistic_Enabled','DNS_optimistic_timeout') }).Count -eq 0) 'Optimistic cache has child settings'
        Assert (@($nodes | Where-Object { $_.name -in @('independent_cache','fakeip') }).Count -eq 0) 'Deprecated DNS fields exposed'
        $source=Get-Content $script:sourcePath -Raw | ConvertFrom-Json
        Assert ($source.'$schema' -eq './schema.json' -and (Test-Path (Join-Path $directory 'sing-box/schema.json'))) 'Offline schema not attached'
    }
    Case 'dns/strategy-edit-and-omitted-options-preservation' {
        Choice 'DNS_strategy' 'prefer_ipv6'
        Wait-Document {param($d) $d.dns.strategy -eq 'prefer_ipv6'} | Out-Null
        Ui @('wait-for','DNS_final','--property','IsEnabled','--value','true') | Out-Null
        $document = Get-Content $script:sourcePath -Raw | ConvertFrom-Json
        foreach ($key in @('disable_expire','cache_capacity','optimistic','timeout','client_subnet')) {
            Assert ($null -eq $document.dns.PSObject.Properties[$key]) ('Saving invented hidden field '+$key)
        }
        Assert ($document.dns.reverse_mapping -eq $true -and $document.dns.disable_cache -eq $false) 'Hidden default values changed'
    }
    if (!$DnsOnly) { Case 'dns/add-modern-fakeip-server' {
        Ui @('invoke','DNS_Servers_Add') | Out-Null; Ui @('wait-for','DNS_Servers_Editor_type') | Out-Null
        Choice 'DNS_Servers_Editor_type' 'fakeip'
        Ui @('wait-for','DNS_Servers_Editor_inet4_range') | Out-Null
        Assert (@(Tree | Where-Object automationId -eq 'DNS_Servers_Editor_server_port').Count -eq 0) 'Old server fields retained after changing type'
        Ui @('set-value','DNS_Servers_Editor_tag','fake-dns') | Out-Null
        Ui @('set-value','DNS_Servers_Editor_inet4_range','198.18.0.0/15') | Out-Null
        Ui @('set-value','DNS_Servers_Editor_inet6_range','fc00::/18') | Out-Null
        Save-Dialog; Ui @('wait-for','DNS_Servers_3') | Out-Null
        Wait-Document {param($d) @($d.dns.servers | Where-Object {$_.type -eq 'fakeip' -and $_.tag -eq 'fake-dns'}).Count -eq 1} | Out-Null
    } }
    Case 'dns/imported-hidden-options-preservation' {
        Stop-Fixture
        $document = Get-Content $script:sourcePath -Raw | ConvertFrom-Json -AsHashtable
        $document['dns']['disable_cache'] = $false
        $document['dns']['disable_expire'] = $false
        $document['dns']['cache_capacity'] = 2048
        $document['dns']['optimistic'] = @{ enabled = $true; timeout = '1h' }
        $document['dns']['timeout'] = '5s'
        $document['dns']['reverse_mapping'] = $true
        $document['dns']['client_subnet'] = '203.0.113.0/24'
        $document | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $script:sourcePath
        Start-Fixture $directory
        Ui @('invoke','Navigation_DNS') | Out-Null; Ui @('wait-for','DNS_strategy') | Out-Null
        Choice 'DNS_strategy' 'ipv4_only'
        Wait-Document {param($d) $d.dns.strategy -eq 'ipv4_only'} | Out-Null
        $saved = Get-Content $script:sourcePath -Raw | ConvertFrom-Json -AsHashtable
        foreach ($key in @('disable_cache','disable_expire','cache_capacity','optimistic','timeout','reverse_mapping','client_subnet')) {
            Assert (($saved['dns'][$key] | ConvertTo-Json -Compress) -ceq ($document['dns'][$key] | ConvertTo-Json -Compress)) ('Hidden value changed: '+$key)
        }
        $nodes = Tree
        foreach ($key in @('disable_cache','disable_expire','cache_capacity','optimistic','timeout','reverse_mapping','client_subnet')) {
            Assert (@($nodes | Where-Object automationId -eq ('DNS_'+$key)).Count -eq 0) ('Hidden control exposed: '+$key)
        }
    }
    if ($DnsOnly) { return }
    Case 'dns/rule-fields-and-reject-action' {
        Ui @('invoke','DNS_Rules_Add') | Out-Null; Ui @('wait-for','DNS_Rules_Editor_action') | Out-Null
        Assert (@(Tree | Where-Object automationId -eq 'DNS_Rules_Editor_process_name').Count -eq 0) 'Optional DNS field visible before adding'
        Choice 'DNS_Rules_Editor_action' 'reject'
        Ui @('invoke','DNS_Rules_Editor_AddField') | Out-Null
        Ui @('invoke','DNS_Rules_Editor_AddField_domain_suffix') | Out-Null
        Ui @('set-value','DNS_Rules_Editor_domain_suffix','blocked.example') | Out-Null
        Assert (@(Tree | Where-Object automationId -eq 'DNS_Rules_Editor_server').Count -eq 0) 'Reject retained server field'
        Save-Dialog; Ui @('wait-for','DNS_Rules_2') | Out-Null
        Wait-Document {param($d) @($d.dns.rules | Where-Object {$_.action -eq 'reject'}).Count -eq 1} | Out-Null
    }
    Case 'route/presets-and-comprehensive-rule-editor' {
        Ui @('invoke','Navigation_Routing') | Out-Null; Ui @('wait-for','Routing_Save') | Out-Null
        Assert-English
        Ui @('invoke','Routing_Rules_Add') | Out-Null; Ui @('wait-for','Routing_Rules_Editor_action') | Out-Null
        Assert (@(Tree | Where-Object automationId -eq 'Routing_Rules_Editor_process_name').Count -eq 0) 'Optional conditions were displayed before being added'
        Assert (@(Tree | Where-Object automationId -eq 'Routing_Rules_Editor_outbound').Count -eq 1) 'Route destination missing'
        $layout = Tree
        $addField = @($layout | Where-Object automationId -eq 'Routing_Rules_Editor_AddField')[0]
        $actionField = @($layout | Where-Object automationId -eq 'Routing_Rules_Editor_action')[0]
        Assert ($addField.width -gt 600 -and $addField.height -ge 52) 'Add field button is not a full width row'
        Assert ($actionField.x -gt $addField.x + $addField.width / 2) 'Input is not aligned on the right'
        Ui @('invoke','Routing_Rules_Editor_AddField') | Out-Null
        foreach($id in @('domain_keyword','process_name','process_path','process_path_regex','source_ip_cidr','port_range','rule_set','invert','inbound')) { Assert (@(Tree | Where-Object automationId -eq ('Routing_Rules_Editor_AddField_'+$id)).Count -eq 1) ('Missing optional route field '+$id) }
        Ui @('invoke','Routing_Rules_Editor_AddField_domain_keyword') | Out-Null
        Ui @('set-value','Routing_Rules_Editor_domain_keyword','discard-me') | Out-Null
        Ui @('invoke','Routing_Rules_Editor_RemoveField_domain_keyword') | Out-Null
        Assert (@(Tree | Where-Object automationId -eq 'Routing_Rules_Editor_domain_keyword').Count -eq 0) 'Removing a field did not hide it'
        Choice 'Routing_Rules_Editor_action' 'reject'
        Ui @('invoke','Routing_Rules_Editor_AddField') | Out-Null
        Ui @('invoke','Routing_Rules_Editor_AddField_process_name') | Out-Null
        Ui @('set-value','Routing_Rules_Editor_process_name','fixture.exe') | Out-Null
        Ui @('screenshot','-o',(Join-Path $taskRoot 'Routing-Rule-Editor.png')) | Out-Null
        Save-Dialog; Ui @('wait-for','Routing_Rules_4') | Out-Null; Ui @('invoke','Routing_Save') | Out-Null
        Wait-Document {param($d) @($d.route.rules | Where-Object {$_.action -eq 'reject' -and $_.process_name -contains 'fixture.exe'}).Count -eq 1} | Out-Null
        Expand-Group 'Routing_Rules_4'; Ui @('invoke','Routing_Rules_4_Edit') | Out-Null
        Ui @('wait-for','Routing_Rules_Editor_process_name') | Out-Null
        Assert ((Ui @('get-value','Routing_Rules_Editor_process_name')).text -eq 'fixture.exe') 'Editing lost existing optional field'
        Assert (@(Tree | Where-Object automationId -eq 'Routing_Rules_Editor_domain_keyword').Count -eq 0) 'Removed field returned when editing'
        Save-Dialog; Ui @('wait-for','Routing_Rules_4') | Out-Null
        $source=Get-Content $script:sourcePath -Raw | ConvertFrom-Json
        Assert (@($source.route.rules | Where-Object {$_.domain_keyword -contains 'discard-me'}).Count -eq 0) 'Removed field was saved'
        Assert (@($source.inbounds | Where-Object {$_.tag -eq 'system-http' -and $_.type -eq 'mixed'}).Count -eq 1) 'System inbound preset missing or duplicated'
        Assert (@($source.outbounds | Where-Object {$_.tag -eq 'direct' -and $_.type -eq 'direct'}).Count -eq 1) 'Direct outbound missing or duplicated'
    }
    Case 'route/tun-preset-is-configuration-only' {
        Ui @('invoke','Routing_AddTunPreset') | Out-Null; Ui @('invoke','Routing_Save') | Out-Null
        Wait-Document {param($d) @($d.inbounds | Where-Object type -eq 'tun').Count -eq 1} | Out-Null
        Assert (!(Test-Path (Join-Path $directory 'sing-box/process.json'))) 'Preset change started the core'
        Assert (!(Test-Path (Join-Path $directory 'client-errors.log'))) 'Client recorded an unhandled exception'
        Ui @('screenshot','-o',(Join-Path $taskRoot 'Routing.png')) | Out-Null
    }
    Case 'route/disable-rule-persist-and-restore' {
        Ui @('invoke','Navigation_Proxy') | Out-Null; Ui @('invoke','Navigation_Routing') | Out-Null
        Ui @('wait-for','Routing_Save') | Out-Null
        Ui @('scroll','PageScrollViewer','--to','bottom') | Out-Null
        Expand-Group 'Routing_Rules_4'; Ui @('wait-for','Routing_Rules_4_Enabled') | Out-Null
        Ui @('invoke','Routing_Rules_4_Enabled','--action','toggle') | Out-Null
        Ui @('invoke','Routing_Save') | Out-Null
        Wait-Document {param($d) @($d.route.rules | Where-Object {$_.process_name -contains 'fixture.exe'}).Count -eq 0} | Out-Null
        Ui @('invoke','Navigation_Proxy') | Out-Null; Ui @('invoke','Navigation_Routing') | Out-Null; Ui @('wait-for','Routing_Save','--property','IsEnabled','--value','true') | Out-Null
        Expand-Group 'Routing_Rules_4'; Ui @('wait-for','Routing_Rules_4_Enabled') | Out-Null
        Ui @('invoke','Routing_Rules_4_Enabled','--action','toggle') | Out-Null
        Ui @('invoke','Routing_Save') | Out-Null
        Wait-Document {param($d) @($d.route.rules | Where-Object {$_.process_name -contains 'fixture.exe'}).Count -eq 1} | Out-Null
    }
}
catch { $taskResults.Add([pscustomobject]@{name='schema-ui';status='failed';message=$_.Exception.Message}); if($taskWindow){ try { Ui @('screenshot','-o',(Join-Path $taskRoot 'failure.png')) | Out-Null } catch {} }; throw }
finally {
    Stop-Fixture; $env:FLUENTSINGBOX_DATA_DIR=$taskOldData; $env:FLUENTSINGBOX_TEST_BACKGROUND=$taskOldBackground
    $taskResults | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $taskRoot 'results.json'); Write-Output "Schema UI evidence: $taskRoot"
}
