param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/../..").Path,
    [string]$ExecutablePath = (Join-Path $ProjectRoot 'artifacts/publish/Debug/fluent sing-box.exe'),
    [switch]$ServerOnly,
    [switch]$LayoutOnly
)
$ErrorActionPreference = 'Stop'
$taskWinapp = (Get-Command winapp -ErrorAction Stop).Source
$taskRoot = Join-Path $ProjectRoot ('artifacts/dns-ui/run-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
$taskSettings = Join-Path $taskRoot 'settings.json'
$taskSource = Join-Path $taskRoot 'sing-box/user-config.json'
$taskCore = Join-Path $ProjectRoot 'artifacts/server-editor/core/sing-box.exe'
@{LanguageChoice='en-US';SingBoxExecutablePath=$taskCore;Servers=@(@{Id='dns-ui';Name='DNS fixture';OutboundJson='{"type":"socks","server":"127.0.0.1","server_port":1}'});DefaultServerId='dns-ui';SystemProxyEnabled=$false;Applications=@()} |
    ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $taskSettings
$taskOldData = $env:FLUENTSINGBOX_DATA_DIR; $taskOldBackground = $env:FLUENTSINGBOX_TEST_BACKGROUND
$taskResults = [Collections.Generic.List[object]]::new()
$taskProcess = $null; $taskWindow = $null; $script:taskSequence = 0
function Assert([bool]$Ok,[string]$Reason) { if (!$Ok) { throw $Reason } }
function Nodes($items) { foreach ($item in $items) { $item; if ($item.children) { Nodes $item.children } } }
function Ui([string[]]$Arguments) {
    $script:taskSequence++
    $raw = & $taskWinapp ui @Arguments -w "$taskWindow" --json 2>&1 | Out-String
    $exit = $LASTEXITCODE
    $raw | Set-Content -LiteralPath (Join-Path $taskRoot ('command-{0:D3}.json' -f $script:taskSequence))
    $value = $raw | ConvertFrom-Json
    if ($exit -ne 0 -or $value.error -or $value.timedOut) { throw "UI command failed: $($Arguments -join ' '): $raw" }
    Assert (!$taskProcess.HasExited) 'Test client exited unexpectedly'
    return $value
}
function Tree { return @(Nodes (Ui @('inspect','--depth','20')).windows.elements) }
function Choice([string]$Id,[string]$Value) {
    Ui @('invoke',$Id,'--action','expand') | Out-Null
    $item = @(Tree | Where-Object { $_.automationId -eq ($Id + '_' + $Value) -and $_.type -eq 'ListItem' })[0]
    Assert ($null -ne $item) "Missing choice: $Id $Value"
    Ui @('invoke',$item.selector,'--action','select') | Out-Null
}
function Expand-Group([string]$Id) {
    $inner = @(Nodes (Ui @('inspect',$Id,'--depth','3')).windows.elements | Where-Object className -eq 'Microsoft.UI.Xaml.Controls.Expander')[0]
    Ui @('invoke',$inner.selector,'--action','expand') | Out-Null
}
function Save-Dialog {
    $button = @(Tree | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Save' })[0]
    Ui @('invoke',$button.selector) | Out-Null
}
function Wait-Document([scriptblock]$Condition) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        try {
            $document = Get-Content -LiteralPath $taskSource -Raw | ConvertFrom-Json
            $runtimePath = Join-Path $taskRoot 'sing-box/config.json'
            if ((& $Condition $document) -and (Test-Path $runtimePath)) {
                $runtime = Get-Content -LiteralPath $runtimePath -Raw | ConvertFrom-Json
                if (& $Condition $runtime) { return $document }
            }
        }
        catch [IO.IOException] {} catch [System.Management.Automation.ItemNotFoundException] {}
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'DNS auto save did not complete'
}
function Case([string]$Name,[scriptblock]$Work) {
    if ($LayoutOnly -and $Name -notin @('dns/server-sparse-wide-add-edit-and-autosave','editors/compact-layout-and-ruleset-add')) { return }
    if ($ServerOnly -and $Name -ne 'dns/server-sparse-wide-add-edit-and-autosave') { return }
    try { & $Work; $taskResults.Add([pscustomobject]@{name=$Name;status='passed'}); Write-Output "PASS $Name" }
    catch { $taskResults.Add([pscustomobject]@{name=$Name;status='failed';message=$_.Exception.Message}); throw }
}
try {
    $env:FLUENTSINGBOX_DATA_DIR = $taskRoot; $env:FLUENTSINGBOX_TEST_BACKGROUND = '1'
    $taskProcess = Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path $ExecutablePath) -WindowStyle Hidden -PassThru
    $raw = & $taskWinapp ui wait-for Navigation_DNS -a "$($taskProcess.Id)" --timeout 15000 --json 2>&1 | Out-String
    $ready = $raw | ConvertFrom-Json
    Assert ($LASTEXITCODE -eq 0 -and !$ready.error -and !$ready.timedOut) "DNS fixture did not start: $raw"
    $windows = @(& $taskWinapp ui list-windows -a "$($taskProcess.Id)" --json | Out-String | ConvertFrom-Json)
    Assert ($windows.Count -eq 1) 'Expected one isolated window'
    $taskWindow = $windows[0].hwnd
    Ui @('invoke','Navigation_DNS') | Out-Null; Ui @('wait-for','DNS_final') | Out-Null
    Case 'dns/server-sparse-wide-add-edit-and-autosave' {
        Ui @('invoke','DNS_Servers_Add') | Out-Null
        Ui @('wait-for','DNS_Servers_Editor_tag') | Out-Null
        $tree = Tree
        Assert (@($tree | Where-Object automationId -eq 'DNS_Servers_Editor_detour').Count -eq 0) 'Unused server field shown before adding'
        $add = @($tree | Where-Object automationId -eq 'DNS_Servers_Editor_AddField')[0]
        Assert ($add.width -ge 550 -and $add.width -le 1260 -and $add.height -ge 52) 'DNS server editor is not wide like routing'
        Ui @('set-value','DNS_Servers_Editor_tag','sparse-server') | Out-Null
        Ui @('invoke','DNS_Servers_Editor_AddField') | Out-Null
        Ui @('invoke','DNS_Servers_Editor_AddField_detour') | Out-Null
        Ui @('wait-for','DNS_Servers_Editor_detour') | Out-Null
        Choice 'DNS_Servers_Editor_detour' 'proxy-dns-ui'
        Ui @('screenshot','-o',(Join-Path $taskRoot 'dns-server-added.png')) | Out-Null
        Ui @('invoke','DNS_Servers_Editor_RemoveField_detour') | Out-Null
        Assert (@(Tree | Where-Object automationId -eq 'DNS_Servers_Editor_detour').Count -eq 0) 'Removed server field is still visible'
        Save-Dialog
        $doc = Wait-Document {param($d) @($d.dns.servers | Where-Object tag -eq 'sparse-server').Count -eq 1}
        $index = $doc.dns.servers.Count - 1
        Expand-Group "DNS_Servers_$index"
        Ui @('invoke',"DNS_Servers_${index}_Edit") | Out-Null
        Ui @('wait-for','DNS_Servers_Editor_server') | Out-Null
        Assert (@(Tree | Where-Object automationId -eq 'DNS_Servers_Editor_detour').Count -eq 0) 'Editing a saved server exposed an unused field'
        Ui @('set-value','DNS_Servers_Editor_server','8.8.8.8') | Out-Null
        Ui @('screenshot','-o',(Join-Path $taskRoot 'dns-server-edit.png')) | Out-Null
        Save-Dialog
        Wait-Document {param($d) @($d.dns.servers | Where-Object { $_.tag -eq 'sparse-server' -and $_.server -eq '8.8.8.8' }).Count -eq 1} | Out-Null
        Assert (!(Test-Path (Join-Path $taskRoot 'sing-box/process.json'))) 'DNS server editing started core'
    }
    Case 'editors/compact-layout-and-ruleset-add' {
        foreach ($section in @('DNS','Routing')) {
            Ui @('invoke',('Navigation_' + $section)) | Out-Null
            Ui @('invoke',($section + '_Rules_Add')) | Out-Null
            Ui @('wait-for',($section + '_Rules_Editor_action')) | Out-Null
            Ui @('invoke',($section + '_Rules_Editor_AddField')) | Out-Null
            Ui @('invoke',($section + '_Rules_Editor_AddField_domain_suffix')) | Out-Null
            Ui @('wait-for',($section + '_Rules_Editor_domain_suffix')) | Out-Null
            $nodes = Tree
            $remove = @($nodes | Where-Object automationId -eq ($section + '_Rules_Editor_RemoveField_domain_suffix'))[0]
            $input = @($nodes | Where-Object automationId -eq ($section + '_Rules_Editor_domain_suffix'))[0]
            Assert ($remove.width -lt $input.width / 5) 'Delete button is not compact'
            Assert ($remove.x + $remove.width -le $input.x) 'Delete button is not left of input'
            Ui @('screenshot','-o',(Join-Path $taskRoot ($section + '-compact.png'))) | Out-Null
            Ui @('invoke','CloseButton') | Out-Null
        }
        Ui @('invoke','Routing_RuleSets_Add') | Out-Null
        Ui @('wait-for','Routing_RuleSets_Editor_tag') | Out-Null
        Ui @('set-value','Routing_RuleSets_Editor_tag','fixture-inline') | Out-Null
        Ui @('set-value','Routing_RuleSets_Editor_rules','[{"domain_suffix":["fixture.example"]}]') | Out-Null
        Save-Dialog
        $doc = Wait-Document {param($d) @($d.route.rule_set | Where-Object tag -eq 'fixture-inline').Count -eq 1}
        Assert ($doc.route.rule_set[-1].tag -is [string]) 'Single rule-set tag was converted to an array'
        Ui @('invoke','Routing_RuleSets_Add') | Out-Null
        Choice 'Routing_RuleSets_Editor_type' 'local'
        Ui @('wait-for','Routing_RuleSets_Editor_path') | Out-Null
        Ui @('set-value','Routing_RuleSets_Editor_tag','fixture-local') | Out-Null
        Choice 'Routing_RuleSets_Editor_format' 'binary'
        $path = Join-Path $ProjectRoot 'src/FluentSingBox.Client/ThirdParty/RuleSets/geoip-cn.srs'
        Ui @('set-value','Routing_RuleSets_Editor_path',$path) | Out-Null
        Save-Dialog
        Wait-Document {param($d) @($d.route.rule_set | Where-Object { $_.tag -eq 'fixture-local' -and $_.type -eq 'local' }).Count -eq 1} | Out-Null
        Ui @('invoke','Navigation_DNS') | Out-Null
    }
    Case 'dns/proxy-default-and-selection-only' {
        Assert ((Get-Content $taskSource -Raw | ConvertFrom-Json).dns.final -eq 'proxy-dns') 'Generated default is not proxy DNS'
        Assert ((Ui @('get-value','DNS_final')).text -eq 'proxy-dns') 'Proxy DNS is not selected'
        Assert (@(Tree | Where-Object automationId -eq 'DNS_Save').Count -eq 0) 'Manual DNS save button remains'
        Ui @('invoke','DNS_final','--action','expand') | Out-Null
        Assert (@(Tree | Where-Object automationId -eq 'DNS_final_').Count -eq 0) 'Empty DNS final option remains'
        $item = @(Tree | Where-Object { $_.automationId -eq 'DNS_final_proxy-dns' -and $_.type -eq 'ListItem' })[0]
        Ui @('invoke',$item.selector,'--action','select') | Out-Null
    }
    Case 'dns/strategy-autosave-and-hidden-defaults' {
        Choice 'DNS_strategy' 'prefer_ipv6'
        Wait-Document {param($d) $d.dns.strategy -eq 'prefer_ipv6'} | Out-Null
        $runtime = Get-Content (Join-Path $taskRoot 'sing-box/config.json') -Raw | ConvertFrom-Json
        Assert ($runtime.dns.strategy -eq 'prefer_ipv6') 'DNS strategy was not synchronized to actual core config'
        Ui @('wait-for','DNS_IPv6_Status') | Out-Null
        Assert (@(Tree | Where-Object { $_.name -match 'could not be confirmed|still allows IPv6' }).Count -gt 0) 'IPv6-enabled DNS did not show the connectivity hint'
        Assert ($runtime.dns.strategy -eq 'prefer_ipv6') 'Connectivity hint changed the explicit strategy'
        $document = Get-Content $taskSource -Raw | ConvertFrom-Json
        Assert ($document.dns.reverse_mapping -eq $true) 'Hidden reverse mapping default changed'
        foreach ($key in @('disable_cache','disable_expire','cache_capacity','optimistic','timeout','reverse_mapping','client_subnet')) {
            Assert (@(Tree | Where-Object automationId -eq ('DNS_'+$key)).Count -eq 0) ('Advanced field visible: '+$key)
        }
    }
    Case 'dns/sparse-wide-rule-editor-and-autosave' {
        Ui @('invoke','DNS_Rules_Add') | Out-Null; Ui @('wait-for','DNS_Rules_Editor_action') | Out-Null
        $tree = Tree
        Assert (@($tree | Where-Object automationId -eq 'DNS_Rules_Editor_domain_suffix').Count -eq 0) 'Optional rule field shown before adding'
        Assert (@($tree | Where-Object automationId -eq 'DNS_Rules_Editor_server').Count -eq 1) 'DNS route server destination missing'
        $add = @($tree | Where-Object automationId -eq 'DNS_Rules_Editor_AddField')[0]
        Assert ($add.width -ge 550 -and $add.width -le 1260 -and $add.height -ge 52) 'DNS editor is not wide like routing'
        Ui @('screenshot','-o',(Join-Path $taskRoot 'dns-rule-initial.png')) | Out-Null
        Ui @('invoke','DNS_Rules_Editor_AddField') | Out-Null
        Ui @('invoke','DNS_Rules_Editor_AddField_domain_suffix') | Out-Null
        Ui @('wait-for','DNS_Rules_Editor_domain_suffix') | Out-Null
        Ui @('set-value','DNS_Rules_Editor_domain_suffix','dns-fixture.example') | Out-Null
        Ui @('screenshot','-o',(Join-Path $taskRoot 'dns-rule-added.png')) | Out-Null
        Save-Dialog
        $doc = Wait-Document {param($d) @($d.dns.rules | Where-Object { $_.domain_suffix -contains 'dns-fixture.example' -and $_.server -eq 'proxy-dns' }).Count -eq 1}
        $index = $doc.dns.rules.Count - 1
        Expand-Group "DNS_Rules_$index"
        Ui @('invoke',"DNS_Rules_${index}_Enabled",'--action','toggle-off') | Out-Null
        Wait-Document {param($d) @($d.dns.rules | Where-Object { $_.domain_suffix -contains 'dns-fixture.example' }).Count -eq 0} | Out-Null
        Ui @('invoke',"DNS_Rules_${index}_Enabled",'--action','toggle-on') | Out-Null
        Wait-Document {param($d) @($d.dns.rules | Where-Object { $_.domain_suffix -contains 'dns-fixture.example' }).Count -eq 1} | Out-Null
        Assert (!(Test-Path (Join-Path $taskRoot 'sing-box/process.json'))) 'DNS edits started core'
        Assert (!(Test-Path (Join-Path $taskRoot 'sing-box/sing-box.log'))) 'DNS validation created runtime log'
    }
    Case 'dns/deleted-final-server-preserves-file-and-recovers-selection' {
        Ui @('invoke','DNS_Servers_Add') | Out-Null; Ui @('wait-for','DNS_Servers_Editor_tag') | Out-Null
        Ui @('set-value','DNS_Servers_Editor_tag','temporary-dns') | Out-Null
        Save-Dialog
        $doc = Wait-Document {param($d) @($d.dns.servers | Where-Object tag -eq 'temporary-dns').Count -eq 1}
        Choice 'DNS_final' 'temporary-dns'
        Wait-Document {param($d) $d.dns.final -eq 'temporary-dns'} | Out-Null
        $hash = (Get-FileHash $taskSource).Hash
        $index = $doc.dns.servers.Count - 1
        Expand-Group "DNS_Servers_$index"
        Ui @('invoke',"DNS_Servers_${index}_Delete") | Out-Null
        Ui @('wait-for','Select an existing DNS server.','--type','Text') | Out-Null
        Start-Sleep -Milliseconds 800
        Assert ((Get-FileHash $taskSource).Hash -eq $hash) 'Deleting selected DNS server wrote invalid final'
        Assert ((Ui @('get-value','DNS_final')).text -eq 'temporary-dns') 'Invalid DNS deletion did not restore UI selection'
        Choice 'DNS_final' 'proxy-dns'
        Wait-Document {param($d) $d.dns.final -eq 'proxy-dns' -and @($d.dns.servers | Where-Object tag -eq 'temporary-dns').Count -eq 1} | Out-Null
        Expand-Group "DNS_Servers_$index"
        Ui @('invoke',"DNS_Servers_${index}_Delete") | Out-Null
        Wait-Document {param($d) @($d.dns.servers | Where-Object tag -eq 'temporary-dns').Count -eq 0} | Out-Null
    }
}
catch { if ($taskWindow) { try { Ui @('screenshot','-o',(Join-Path $taskRoot 'failure.png')) | Out-Null } catch {} }; throw }
finally {
    $taskResults | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRoot 'results.json')
    Write-Output "UI evidence: $taskRoot"
    if ($taskProcess) { if (!$taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id }; $taskProcess.Dispose() }
    $env:FLUENTSINGBOX_DATA_DIR = $taskOldData; $env:FLUENTSINGBOX_TEST_BACKGROUND = $taskOldBackground
}
